using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using System.ClientModel;
using System.Data;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Timeouts;

namespace AgentRag.Api.Controllers;

/// <param name="Index">False stores the document for reading only, without vector passages.</param>
public record CreateDocumentRequest(string Title, string Content, int? FolderId = null, bool Index = true);
public record DocumentDto(int Id, string Title, DateTime CreatedUtc, int Chunks, int? FolderId = null,
    bool CanWrite = true, string? AddedBy = null, bool Indexed = true)
{
    public static DocumentDto From(KnowledgeDocument document, int chunks, bool canWrite, string? addedBy = null) =>
        new(document.Id, document.Title, document.CreatedUtc, chunks, document.FolderId, canWrite, addedBy, document.Indexed);
}
public record DocumentContentEvent(string Type, int? Id = null, string? Title = null, int? Chunks = null,
    bool? Indexed = null, string? Text = null, string? Error = null, bool AccessChanged = false);
public record IngestionEvent(string Type, IngestionProgress? Progress = null, DocumentDto? Document = null, string? Error = null, bool AccessChanged = false);

[ApiController, Authorize, Route("api/documents")]
public class DocumentsController(AppDbContext db, RagService rag, ILogger<DocumentsController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IEnumerable<DocumentDto>> List(CancellationToken ct)
    {
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var rows = await access.Documents(db).OrderByDescending(d => d.CreatedUtc)
            .Select(d => new { Document = d, Chunks = d.Chunks.Count }).ToListAsync(ct);
        return rows.Select(d => DocumentDto.From(d.Document, d.Chunks, access.CanWriteDocument(d.Document)));
    }

    /// <summary>Characters sent per text event when streaming stored document text.</summary>
    internal const int ContentSegmentCharacters = 32 * 1024;
    /// <summary>Chunks merged per text event for documents stored before full text was kept.</summary>
    internal const int LegacyChunkPageSize = 64;

    /// <summary>
    /// Streams a document's text as NDJSON so large documents render progressively. Access is checked once,
    /// then the text is read with a single query: the stored full text, or the passages of older documents.
    /// </summary>
    [HttpGet("{id:int}/content"), DisableRequestTimeout]
    public async Task<IActionResult> Content(int id, CancellationToken ct)
    {
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var document = await access.Documents(db).Where(d => d.Id == id)
            .Select(d => new { d.Id, d.Title, d.Indexed, Chunks = d.Chunks.Count, HasText = d.Text != null }).SingleOrDefaultAsync(ct);
        if (document is null) return NotFound(new ProblemDetails { Status = 404, Detail = "This document is unavailable." });

        await JsonEventStream.WriteAsync<DocumentContentEvent>(HttpContext, async (write, token) =>
        {
            await write(new("started", Id: document.Id, Title: document.Title, Chunks: document.Chunks, Indexed: document.Indexed));
            try
            {
                Func<string, ValueTask> text = segment => write(new("text", Text: segment));
                if (document.HasText) await StreamStoredTextAsync(id, text, token);
                else await StreamLegacyChunksAsync(id, text, token);
                await write(new("completed"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Document content stream failed for document {DocumentId}", id);
                await write(new("error", Error: "Unable to load this document. Please try again."));
            }
        }, new DocumentContentEvent("heartbeat"), ct, timeProvider);
        return new EmptyResult();
    }

    private async Task StreamStoredTextAsync(int id, Func<string, ValueTask> write, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var content = await db.DocumentTexts.AsNoTracking().Where(t => t.DocumentId == id).Select(t => t.Content).SingleAsync(ct);
            await WriteSegmentsAsync(new StringReader(content), write, ct);
            return;
        }
        // Sequential access lets Npgsql stream the column instead of buffering the whole text in memory.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """SELECT "Content" FROM "DocumentTexts" WHERE "DocumentId" = @id""";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = id;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (await reader.ReadAsync(ct))
            {
                using var text = reader.GetTextReader(0);
                await WriteSegmentsAsync(text, write, ct);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task StreamLegacyChunksAsync(int id, Func<string, ValueTask> write, CancellationToken ct)
    {
        var page = new List<string>(LegacyChunkPageSize);
        var continuation = false;
        await foreach (var chunk in db.Chunks.AsNoTracking().Where(c => c.DocumentId == id).OrderBy(c => c.Index)
            .Select(c => c.Content).AsAsyncEnumerable().WithCancellation(ct))
        {
            page.Add(chunk);
            if (page.Count < LegacyChunkPageSize) continue;
            await write(RagService.ReconstructText(page, continuation));
            continuation = true;
            page.Clear();
        }
        if (page.Count > 0) await write(RagService.ReconstructText(page, continuation));
    }

    /// <summary>Writes text in fixed-size segments without ever splitting a UTF-16 surrogate pair across events.</summary>
    internal static async Task WriteSegmentsAsync(TextReader reader, Func<string, ValueTask> write, CancellationToken ct,
        int segmentCharacters = ContentSegmentCharacters)
    {
        var buffer = new char[segmentCharacters + 1];
        var carried = 0;
        while (true)
        {
            var read = await reader.ReadBlockAsync(buffer.AsMemory(carried, segmentCharacters), ct);
            var length = carried + read;
            if (length == 0) return;
            var end = read > 0 && char.IsHighSurrogate(buffer[length - 1]) ? length - 1 : length;
            if (end > 0) await write(new string(buffer, 0, end));
            carried = length - end;
            if (carried > 0) buffer[0] = buffer[length - 1];
            if (read == 0) return;
        }
    }

    [HttpPost, DisableRequestTimeout]
    public async Task<ActionResult<DocumentDto>> Create(CreateDocumentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest("Title and content are required.");
        try
        {
            var doc = await rag.IngestAsync(UserId, LibraryController.Name(req.Title), req.Content, null, ct, req.FolderId, req.Index);
            return DocumentDto.From(doc, doc.Chunks.Count, true);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            logger.LogWarning("Document ingestion stopped after embedding quota errors (HTTP 429)");
            return Problem(statusCode: 429, detail: RateLimitMessage);
        }
    }

    private const string RateLimitMessage = "The embedding service is still rate-limited after automatic retries. Your document was not added. Wait a few minutes and try again, or ask your administrator to increase the model quota.";

    [HttpPost("upload"), DisableRequestTimeout]
    [RequestSizeLimit(DocumentTextExtractor.MaxFileBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = DocumentTextExtractor.MaxFileBytes)]
    public async Task Upload([FromForm] IFormFile file, [FromForm] string? title,
        [FromServices] DocumentTextExtractor extractor, CancellationToken ct, [FromForm] int? folderId = null, [FromForm] bool index = true)
    {
        if (folderId is { } id) (await new LibraryAccess(db).LoadAsync(UserId, ct)).Require(id, write: true);
        string content;
        try
        {
            content = await extractor.ExtractAsync(file, ct);
        }
        catch (InvalidDataException ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ProblemDetails { Detail = ex.Message }, ct);
            return;
        }

        // Reuse the authenticated ingestion pipeline and its progress stream.
        await Ingest(new(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title, content, folderId, index), ct);
    }

    [HttpPost("ingest"), DisableRequestTimeout]
    public async Task Ingest(CreateDocumentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ProblemDetails { Detail = "Title and content are required." }, ct);
            return;
        }

        LibraryController.Name(req.Title);
        if (req.FolderId is { } folderId) (await new LibraryAccess(db).LoadAsync(UserId, ct)).Require(folderId, write: true);

        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, HttpContext.RequestAborted);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation.Token);
        var events = Channel.CreateUnbounded<IngestionEvent>(new UnboundedChannelOptions { SingleReader = true });
        var userId = UserId;
        var heartbeat = SendHeartbeatsAsync();
        var ingestion = ProduceAsync();
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(clientCancellation.Token))
            {
                await Response.WriteAsync(JsonSerializer.Serialize(item, JsonSerializerOptions.Web) + "\n", clientCancellation.Token);
                await Response.Body.FlushAsync(clientCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
        {
            // The request ended; cleanup below also stops ingestion and heartbeats.
        }
        finally
        {
            await cancellation.CancelAsync();
            await ingestion;
        }

        async Task SendHeartbeatsAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider ?? TimeProvider.System);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                    events.Writer.TryWrite(new("heartbeat"));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        async Task ProduceAsync()
        {
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = HttpContext.TraceIdentifier, ["UserId"] = userId });
            IngestionEvent? terminal = null;
            try
            {
                var doc = await rag.IngestAsync(userId, req.Title.Trim(), req.Content,
                    progress => events.Writer.TryWrite(new("progress", Progress: progress)), cancellation.Token, req.FolderId, req.Index);
                terminal = new("completed", Document: DocumentDto.From(doc, doc.Chunks.Count, true));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                logger.LogInformation("Document ingestion cancelled after client disconnected");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Document ingestion failed");
                terminal = new("error", Error: ex is LibraryException ? ex.Message : ex is ClientResultException { Status: 429 }
                    ? RateLimitMessage : "Unable to add your document. Please try again. If the problem continues, contact your administrator.", AccessChanged: ex is LibraryException);
            }
            finally
            {
                await cancellation.CancelAsync();
                await heartbeat;
                if (terminal is not null && !clientCancellation.IsCancellationRequested)
                    events.Writer.TryWrite(terminal);
                events.Writer.TryComplete();
            }
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var doc = await access.Documents(db).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null) return NotFound();
        if (!access.CanWriteDocument(doc)) throw new LibraryException(403, "Read access does not allow deleting documents.");
        db.Documents.Remove(doc);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Rename(int id, RenameRequest request, CancellationToken ct)
    {
        var name = LibraryController.Name(request.Name);
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var doc = await access.Documents(db).SingleOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new LibraryException(404, "This document is unavailable.");
        if (!access.CanWriteDocument(doc)) throw new LibraryException(403, "Read access does not allow editing documents.");
        db.Attach(doc);
        doc.Title = name;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, MoveRequest request, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var doc = await access.Documents(db).SingleOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new LibraryException(404, "This document is unavailable.");
        if (!access.CanWriteDocument(doc)) throw new LibraryException(403, "Read access does not allow moving documents.");
        if (request.FolderId is { } targetId)
        {
            var target = access.Require(targetId, write: true);
            if (target.OwnerId != doc.OwnerId) throw new LibraryException(400, "Moving between different owners is not supported.");
        }
        else if (doc.OwnerId != UserId) throw new LibraryException(403, "Only the owner can move content outside a shared folder.");
        var from = doc.FolderId is { } source ? access.ShareRoot(source) : null;
        var to = request.FolderId is { } destination ? access.ShareRoot(destination) : null;
        if (from != to)
        {
            if (doc.OwnerId != UserId) throw new LibraryException(403, "Only the owner can change the sharing perimeter.");
            if (!request.ConfirmAccessChange) throw new LibraryException(409, "Confirm the change to who can access this content.");
        }
        db.Attach(doc);
        doc.FolderId = request.FolderId;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }
}
