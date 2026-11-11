using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using System.ClientModel;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Timeouts;

namespace AgentRag.Api.Controllers;

public record CreateDocumentRequest(string Title, string Content, int? FolderId = null);
public record DocumentDto(int Id, string Title, DateTime CreatedUtc, int Chunks, int? FolderId = null,
    bool CanWrite = true, string? AddedBy = null)
{
    public static DocumentDto From(KnowledgeDocument document, int chunks, bool canWrite, string? addedBy = null) =>
        new(document.Id, document.Title, document.CreatedUtc, chunks, document.FolderId, canWrite, addedBy);
}
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

    [HttpPost, DisableRequestTimeout]
    public async Task<ActionResult<DocumentDto>> Create(CreateDocumentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest("Title and content are required.");
        try
        {
            var doc = await rag.IngestAsync(UserId, LibraryController.Name(req.Title), req.Content, null, ct, req.FolderId);
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
        [FromServices] DocumentTextExtractor extractor, CancellationToken ct, [FromForm] int? folderId = null)
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
        await Ingest(new(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title, content, folderId), ct);
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
                    progress => events.Writer.TryWrite(new("progress", Progress: progress)), cancellation.Token, req.FolderId);
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
