using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Controllers;

public record CreateDocumentRequest(string Title, string Content);
public record DocumentDto(int Id, string Title, DateTime CreatedUtc, int Chunks);

[ApiController, Authorize, Route("api/documents")]
public class DocumentsController(AppDbContext db, RagService rag) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IEnumerable<DocumentDto>> List(CancellationToken ct) =>
        await db.Documents.Where(d => d.OwnerId == UserId)
            .OrderByDescending(d => d.CreatedUtc)
            .Select(d => new DocumentDto(d.Id, d.Title, d.CreatedUtc, d.Chunks.Count))
            .ToListAsync(ct);

    [HttpPost]
    public async Task<ActionResult<DocumentDto>> Create(CreateDocumentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest("Title and content are required.");
        var doc = await rag.IngestAsync(UserId, req.Title.Trim(), req.Content, ct);
        return new DocumentDto(doc.Id, doc.Title, doc.CreatedUtc, doc.Chunks.Count);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var n = await db.Documents.Where(d => d.Id == id && d.OwnerId == UserId).ExecuteDeleteAsync(ct);
        return n == 0 ? NotFound() : NoContent();
    }
}
