using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;

namespace AgentRag.Api.Controllers;

/// <param name="Language">Language of the pages; "auto" or empty follows the sources.</param>
/// <param name="Guidelines">Editorial rules given to the model: glossary, conventions, emphasis.</param>
public record CreateWikiRequest(int FolderId, string? Language = null, string? Guidelines = null);
public record UpdateWikiRequest(string? Language = null, string? Guidelines = null);
/// <param name="Mode">"incremental" (default) processes what changed, "full" rebuilds everything, "lint" only checks links.</param>
public record StartWikiRunRequest(string? Mode = null);
public record WikiRunEvent(string Type, WikiRunDto? Run = null);
public record ResolvedDocument(int DocumentId);

[ApiController, Authorize, Route("api/wikis")]
public class WikiController(WikiService wikis, TimeProvider? timeProvider = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public Task<IReadOnlyList<WikiDto>> List(CancellationToken ct) => wikis.ListAsync(UserId, ct);

    [HttpGet("{id:int}")]
    public Task<WikiDto> Get(int id, CancellationToken ct) => wikis.GetAsync(UserId, id, ct);

    [HttpPost]
    public Task<WikiDto> Create(CreateWikiRequest request, CancellationToken ct) =>
        wikis.CreateAsync(UserId, request.FolderId, request.Language, request.Guidelines, ct);

    [HttpPatch("{id:int}")]
    public Task<WikiDto> Update(int id, UpdateWikiRequest request, CancellationToken ct) =>
        wikis.UpdateAsync(UserId, id, request.Language, request.Guidelines, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await wikis.DeleteAsync(UserId, id, ct);
        return NoContent();
    }

    [HttpGet("{id:int}/status")]
    public Task<WikiStatus> Status(int id, CancellationToken ct) => wikis.StatusAsync(UserId, id, ct);

    /// <summary>Queues a build. It runs in the background and continues when the client disconnects.</summary>
    [HttpPost("{id:int}/runs")]
    public Task<WikiRunDto> StartRun(int id, StartWikiRunRequest request, CancellationToken ct) => wikis.StartRunAsync(UserId, id, request.Mode, ct);

    [HttpGet("{id:int}/runs")]
    public Task<IReadOnlyList<WikiRunDto>> Runs(int id, CancellationToken ct) => wikis.ListRunsAsync(UserId, id, ct);

    [HttpPost("{id:int}/runs/{runId:int}/cancel")]
    public Task<WikiRunDto> CancelRun(int id, int runId, CancellationToken ct) => wikis.CancelRunAsync(UserId, id, runId, ct);

    /// <summary>Streams the progress of a run as NDJSON until it ends. Closing the stream does not stop the run.</summary>
    [HttpGet("{id:int}/runs/{runId:int}/events"), DisableRequestTimeout]
    public async Task<IActionResult> RunEvents(int id, int runId, CancellationToken ct)
    {
        var userId = UserId;
        var first = await wikis.GetRunAsync(userId, id, runId, ct);
        var time = timeProvider ?? TimeProvider.System;
        await JsonEventStream.WriteAsync<WikiRunEvent>(HttpContext, async (write, token) =>
        {
            var last = first;
            await write(new("progress", last));
            while (WikiRunStatus.IsActive(last.Status))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, token);
                // Access is checked on every read: a user removed from the folder stops receiving progress.
                var current = await wikis.GetRunAsync(userId, id, runId, token);
                if (current with { Stats = null } != last with { Stats = null }) await write(new("progress", current));
                last = current;
            }
            await write(new("completed", last));
        }, new WikiRunEvent("heartbeat"), ct, timeProvider);
        return new EmptyResult();
    }

    [HttpGet("{id:int}/pages")]
    public Task<IReadOnlyList<WikiPageSummary>> Pages(int id, CancellationToken ct, string? kind = null, string? q = null) =>
        wikis.ListPagesAsync(UserId, id, ct, kind, q);

    [HttpGet("{id:int}/pages/{slug}")]
    public Task<WikiPageDetail> Page(int id, string slug, CancellationToken ct) => wikis.ReadPageAsync(UserId, id, slug, ct);

    [HttpGet("{id:int}/search")]
    public Task<IReadOnlyList<WikiPageHit>> Search(int id, string q, CancellationToken ct, int top = 10)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 2000) throw new LibraryException(400, "Enter a search text of at most 2,000 characters.");
        return wikis.SearchAsync(UserId, id, q, Math.Clamp(top, 1, 30), ct);
    }

    /// <param name="kinds">Comma-separated page kinds to keep.</param>
    [HttpGet("{id:int}/graph")]
    public Task<WikiGraph> Graph(int id, CancellationToken ct, string? center = null, int depth = 1, string? kinds = null, bool includeSources = false) =>
        wikis.GraphAsync(UserId, id, ct, center, depth, kinds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), includeSources);

    /// <summary>Document that holds a repository file cited by a page, so the citation can be opened.</summary>
    [HttpGet("files")]
    public async Task<ResolvedDocument> File(int repositoryId, string path, CancellationToken ct) =>
        new(await wikis.ResolveRepositoryFileAsync(UserId, repositoryId, path, ct));
}
