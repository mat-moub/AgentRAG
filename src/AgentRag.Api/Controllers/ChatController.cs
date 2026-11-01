using System.Security.Claims;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentRag.Api.Controllers;

public record ChatRequest(string Message);
public record ChatResponse(string Answer);

[ApiController, Authorize, Route("api/chat")]
public class ChatController(RagService rag, RagAgentFactory factory) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post(ChatRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Message)) return BadRequest("Message is required.");
        var agent = factory.Create(rag, User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await agent.RunAsync(req.Message, cancellationToken: ct);
        return new ChatResponse(result.Text);
    }
}
