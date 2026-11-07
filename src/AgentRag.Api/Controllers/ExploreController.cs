using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Controllers;

public record ExploreRequest(string? Query, int? DocumentId);

[ApiController, Authorize, Route("api/explore")]
public class ExploreController(AppDbContext db, EmbeddingService embeddings) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Explore(ExploreRequest request, CancellationToken ct)
    {
        var query = request.Query?.Trim() ?? "";
        if (query.Length > 2000) return BadRequest(new ProblemDetails { Detail = "Search text must be 2,000 characters or fewer." });
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var chunks = db.Chunks.AsNoTracking().Where(c => c.Document!.OwnerId == owner);
        if (request.DocumentId is { } id) chunks = chunks.Where(c => c.DocumentId == id);
        var total = await chunks.CountAsync(ct);
        var rows = await chunks.OrderBy(c => c.Id).Take(500)
            .Select(c => new { c.Id, c.DocumentId, Title = c.Document!.Title, c.Index, c.Content, c.Embedding }).ToListAsync(ct);
        float[]? search = query.Length > 0 && rows.Count > 0
            ? (await embeddings.GenerateAsync([query], ct: ct))[0].Vector.ToArray() : null;
        var vectors = rows.Select(r => r.Embedding.ToArray()).ToArray();
        var points = PassageProjection.Project(vectors);
        return Ok(new { total, passages = rows.Select((r, i) => new
        {
            r.Id, r.DocumentId, r.Title, r.Index, r.Content,
            X = points[i][0], Y = points[i][1],
            Score = search == null ? (double?)null : RagService.Cosine(vectors[i], search),
            Exact = query.Length > 0 && r.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
        }) });
    }
}
