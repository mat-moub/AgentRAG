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

public record CreateDocumentRequest(string Title, string Content);
public record DocumentDto(int Id, string Title, DateTime CreatedUtc, int Chunks);
public record IngestionEvent(string Type, IngestionProgress? Progress = null, DocumentDto? Document = null, string? Error = null);

[ApiController, Authorize, Route("api/documents")]
public class DocumentsController(AppDbContext db, RagService rag, ILogger<DocumentsController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IEnumerable<DocumentDto>> List(CancellationToken ct) =>
        await db.Documents.Where(d => d.OwnerId == UserId)
            .OrderByDescending(d => d.CreatedUtc)
            .Select(d => new DocumentDto(d.Id, d.Title, d.CreatedUtc, d.Chunks.Count))
            .ToListAsync(ct);

    [HttpPost, DisableRequestTimeout]
    public async Task<ActionResult<DocumentDto>> Create(CreateDocumentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest("Title and content are required.");
        try
        {
            var doc = await rag.IngestAsync(UserId, req.Title.Trim(), req.Content, ct);
            return new DocumentDto(doc.Id, doc.Title, doc.CreatedUtc, doc.Chunks.Count);
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
        [FromServices] DocumentTextExtractor extractor, CancellationToken ct)
    {
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
        await Ingest(new(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title, content), ct);
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
            try
            {
                var doc = await rag.IngestAsync(userId, req.Title.Trim(), req.Content,
                    progress => events.Writer.TryWrite(new("progress", Progress: progress)), cancellation.Token);
                events.Writer.TryWrite(new("completed", Document: new(doc.Id, doc.Title, doc.CreatedUtc, doc.Chunks.Count)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                logger.LogInformation("Document ingestion cancelled after client disconnected");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Document ingestion failed");
                events.Writer.TryWrite(new("error", Error: ex is ClientResultException { Status: 429 }
                    ? RateLimitMessage : "Unable to add your document. Please try again. If the problem continues, contact your administrator."));
            }
            finally
            {
                await cancellation.CancelAsync();
                await heartbeat;
                events.Writer.TryComplete();
            }
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var n = await db.Documents.Where(d => d.Id == id && d.OwnerId == UserId).ExecuteDeleteAsync(ct);
        return n == 0 ? NotFound() : NoContent();
    }
}
