using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Controllers;

public record ExploreRequest(string? Query, int? DocumentId, int? FolderId = null);

[ApiController, Authorize, Route("api/explore")]
public class ExploreController(AppDbContext db, EmbeddingService embeddings) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Explore(ExploreRequest request, CancellationToken ct)
    {
        var query = request.Query?.Trim() ?? "";
        if (query.Length > 2000) return BadRequest(new ProblemDetails { Detail = "Search text must be 2,000 characters or fewer." });
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var access = new LibraryAccess(db);
        var documents = await access.ReadableDocumentsAsync(owner, request.FolderId, ct);
        if (request.DocumentId is { } requestedDocument && !await documents.AnyAsync(d => d.Id == requestedDocument, ct))
            throw new LibraryException(404, "This document is unavailable in the selected sources.");
        var documentIds = await documents.Select(d => d.Id).ToArrayAsync(ct);
        var chunks = db.Chunks.AsNoTracking().Where(c => documentIds.Contains(c.DocumentId));
        if (request.DocumentId is { } id) chunks = chunks.Where(c => c.DocumentId == id);
        var total = await chunks.CountAsync(ct);
        var sampleIds = PassageSampling.SelectAccessibleIds(db, request.DocumentId is { } selected ? [selected] : documentIds);
        var rows = await chunks.Where(c => sampleIds.Contains(c.Id)).OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.DocumentId, Title = c.Document!.Title, c.Index, c.Content, c.Embedding }).ToListAsync(ct);
        float[]? search = query.Length > 0 && rows.Count > 0
            ? (await embeddings.GenerateAsync([query], ct: ct))[0].Vector.ToArray() : null;
        // Model calls can take time: never return passages revoked while they were running.
        var currentDocuments = await access.ReadableDocumentsAsync(owner, request.FolderId, ct);
        var currentIds = await currentDocuments.Select(d => d.Id).ToArrayAsync(ct);
        rows = rows.Where(r => currentIds.Contains(r.DocumentId)).ToList();
        total = await chunks.Where(c => currentIds.Contains(c.DocumentId)).CountAsync(ct);
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
