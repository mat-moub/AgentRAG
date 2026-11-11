using System.Security.Claims;
using AgentRag.Api.Services;
using AgentRag.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Diagnostics;

namespace AgentRag.Api.Controllers;

public record ChatHistoryMessage(string Role, string Content);
public record ChatRequest(string Message, IReadOnlyList<ChatHistoryMessage>? History = null, int? FolderId = null, string? LibraryRevision = null);
public record ChatResponse(string Answer);
public record ChatEvent(string Type, string? Text = null, string? Error = null, bool AccessChanged = false);

[ApiController, Authorize, Route("api/chat")]
public class ChatController(RagService rag, RagAgentFactory factory, ILogger<ChatController> logger, AppDbContext? db = null) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post(ChatRequest req, CancellationToken ct)
    {
        if (Validate(req) is { } error) return BadRequest(error);
        await ValidateSourcesAsync(req, ct);
        var agent = factory.Create(rag, User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct, req.FolderId);
        var result = await agent.RunAsync(ToMessages(req), cancellationToken: ct);
        return new ChatResponse(result.Text);
    }

    [HttpPost("stream")]
    public async Task Stream(ChatRequest req, CancellationToken ct)
    {
        if (Validate(req) is { } error)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ProblemDetails { Detail = error }, ct);
            return;
        }

        await ValidateSourcesAsync(req, ct);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = HttpContext.TraceIdentifier, ["UserId"] = userId });
        await JsonEventStream.WriteAsync<ChatEvent>(HttpContext, async (send, token) =>
        {
            var watch = Stopwatch.StartNew();
            var characters = 0;
            logger.LogInformation("Starting RAG response stream");
            try
            {
                await send(new("started"));
                var agent = factory.Create(rag, userId, token, req.FolderId);
                await foreach (var update in agent.RunStreamingAsync(ToMessages(req), cancellationToken: token))
                {
                    // Tool results and reasoning are not part of the answer displayed to the user.
                    if (update.Role is { } role && role != ChatRole.Assistant) continue;
                    if (string.IsNullOrEmpty(update.Text)) continue;
                    if (characters == 0) logger.LogInformation("First RAG text received after {ElapsedMs} ms", watch.ElapsedMilliseconds);
                    characters += update.Text.Length;
                    await send(new("delta", Text: update.Text));
                }
                if (characters == 0) throw new InvalidOperationException("The agent returned no answer text.");
                await send(new("completed"));
                logger.LogInformation("RAG response streamed: {CharacterCount} characters in {ElapsedMs} ms", characters, watch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                logger.LogInformation("RAG response stream cancelled after {ElapsedMs} ms", watch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RAG response stream failed after {ElapsedMs} ms", watch.ElapsedMilliseconds);
                await send(new("error", Error: ex is ClientResultException { Status: 429 }
                    ? "The AI service is rate-limited. Please wait a minute and try again."
                    : "The answer could not be completed. Please try again.", AccessChanged: ex is LibraryException));
            }
        }, new("heartbeat"), ct);
    }

    private async Task ValidateSourcesAsync(ChatRequest request, CancellationToken ct)
    {
        if (db is null) return;
        var access = new LibraryAccess(db);
        var snapshot = await access.LoadAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct);
        if (request.FolderId is { } id) snapshot.Require(id);
        if (request.History is { Count: > 0 } && request.LibraryRevision != await access.RevisionAsync(snapshot, ct))
            throw new LibraryException(409, "Your sources or access changed. Start a new conversation.");
    }

    private static string? Validate(ChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Message)) return "Message is required.";
        if (req.History?.Any(message => message is null || message.Role is not ("user" or "assistant")
            || string.IsNullOrWhiteSpace(message.Content)) == true)
            return "History must contain non-empty user or assistant messages.";
        return null;
    }

    private static List<ChatMessage> ToMessages(ChatRequest req) =>
    [
        .. (req.History ?? []).Select(message => new ChatMessage(
            message.Role == "user" ? ChatRole.User : ChatRole.Assistant, message.Content)),
        new(ChatRole.User, req.Message)
    ];
}
