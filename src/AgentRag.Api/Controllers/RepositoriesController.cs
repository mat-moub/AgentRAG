using System.Security.Claims;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentRag.Api.Controllers;

/// <param name="Repository">"owner/name" or a github.com URL.</param>
/// <param name="Branch">Branch to mirror; the repository's default branch when omitted.</param>
/// <param name="ParentFolderId">Folder that will contain the repository's folder; the library root when omitted.</param>
public record ConnectRepositoryRequest(string Repository, string Token, string? Branch = null, int? ParentFolderId = null);
public record RepositoryTokenRequest(string Token);
/// <summary>A connected repository. The access token is never part of it.</summary>
public record RepositoryDto(int Id, int FolderId, string Repository, string Branch, string? LastSyncedCommit,
    DateTime? LastSyncedUtc, string? LastError, int Documents, bool CanSync)
{
    public static RepositoryDto From(RepositoryConnection repository, int documents, bool canSync) =>
        new(repository.Id, repository.FolderId, $"{repository.RepositoryOwner}/{repository.RepositoryName}", repository.Branch,
            repository.LastSyncedCommit, repository.LastSyncedUtc, repository.LastError, documents, canSync);
}
public record RepositorySyncEvent(string Type, SyncProgress? Progress = null, SyncSummary? Summary = null, string? Error = null);

[ApiController, Authorize, Route("api/repositories")]
public class RepositoriesController(AppDbContext db, GitHubClient github, RepositoryTokenProtector tokens,
    RepositorySyncService sync, ILogger<RepositoriesController> logger, TimeProvider? timeProvider = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IReadOnlyList<RepositoryDto>> List(CancellationToken ct) => await ListAsync(db, UserId, ct);

    /// <summary>Repositories whose folder the user can read. Shared with the MCP tool so both apply the same access rules.</summary>
    internal static async Task<IReadOnlyList<RepositoryDto>> ListAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        var access = await new LibraryAccess(db).LoadAsync(userId, ct);
        var readable = access.Folders.Keys.Where(id => access.Role(id) is not null).ToArray();
        var rows = await db.Repositories.AsNoTracking().Where(r => readable.Contains(r.FolderId)).OrderBy(r => r.Id)
            .Select(r => new { Repository = r, Documents = db.Documents.Count(d => d.RepositoryId == r.Id) }).ToListAsync(ct);
        return rows.Select(r => RepositoryDto.From(r.Repository, r.Documents, access.CanWrite(r.Repository.FolderId))).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<RepositoryDto>> Connect(ConnectRepositoryRequest request, CancellationToken ct)
    {
        if (!GitHubClient.TryParseRepository(request.Repository, out var owner, out var name))
            throw new LibraryException(400, "Enter a GitHub repository as owner/name or its github.com address.");
        var token = Token(request.Token);
        var branch = request.Branch?.Trim();
        if (branch is { Length: > 255 }) throw new LibraryException(400, "Enter a branch name of at most 255 characters.");
        if (request.ParentFolderId is { } parent) (await new LibraryAccess(db).LoadAsync(UserId, ct)).Require(parent, write: true);

        // Validates the token against the repository before anything is stored.
        string defaultBranch;
        try
        {
            defaultBranch = await github.GetDefaultBranchAsync(owner, name, token, ct);
            if (!string.IsNullOrEmpty(branch)) await github.GetTreeAsync(owner, name, branch, token, ct);
        }
        catch (GitHubException ex) { throw new LibraryException(ex.Status, ex.Message); }

        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var access = await new LibraryAccess(db).LoadAsync(UserId, ct);
        var folderOwner = request.ParentFolderId is { } id ? access.Require(id, write: true).OwnerId : UserId;
        var folder = new KnowledgeFolder { Name = $"{owner}/{name}", OwnerId = folderOwner, ParentId = request.ParentFolderId };
        var repository = new RepositoryConnection
        {
            Folder = folder, RepositoryOwner = owner, RepositoryName = name,
            Branch = string.IsNullOrEmpty(branch) ? defaultBranch : branch, ProtectedToken = tokens.Protect(token)
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return RepositoryDto.From(repository, 0, true);
    }

    /// <summary>Replaces the stored token, for example after it expired.</summary>
    [HttpPut("{id:int}/token")]
    public async Task<IActionResult> ReplaceToken(int id, RepositoryTokenRequest request, CancellationToken ct)
    {
        var token = Token(request.Token);
        var repository = await RequireAsync(id, ct);
        try { await github.GetTreeAsync(repository.RepositoryOwner, repository.RepositoryName, repository.Branch, token, ct); }
        catch (GitHubException ex) { throw new LibraryException(ex.Status, ex.Message); }
        repository.ProtectedToken = tokens.Protect(token);
        repository.LastError = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Streams the synchronisation as NDJSON so a large repository shows progress instead of a silent wait.</summary>
    [HttpPost("{id:int}/sync"), DisableRequestTimeout]
    public async Task<IActionResult> Sync(int id, CancellationToken ct)
    {
        var repository = await RequireAsync(id, ct);
        var userId = UserId;
        await JsonEventStream.WriteAsync<RepositorySyncEvent>(HttpContext, async (write, token) =>
        {
            try
            {
                // Progress comes from the synchronisation loop, one call per file: send at most a few per second
                // and never make the loop wait on a slow client.
                var lastSent = 0L;
                var summary = await sync.SyncAsync(userId, repository.Id, progress =>
                {
                    var now = Environment.TickCount64;
                    if (progress.Stage == "fetching" && now - lastSent < 250) return;
                    lastSent = now;
                    _ = write(new("progress", Progress: progress)).AsTask();
                }, token);
                await write(new("completed", Summary: summary));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is not (GitHubException or LibraryException)) logger.LogError(ex, "Repository synchronisation failed for repository {RepositoryId}", id);
                await write(new("error", Error: ex is GitHubException or LibraryException ? ex.Message
                    : "The synchronisation failed. Files already saved are kept; try again to continue."));
            }
        }, new RepositorySyncEvent("heartbeat"), ct, timeProvider);
        return new EmptyResult();
    }

    /// <summary>Disconnects the repository and removes its synchronised files. The folder itself is kept.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var repository = await RequireAsync(id, ct);
        db.Documents.RemoveRange(await db.Documents.Where(d => d.RepositoryId == id).ToListAsync(ct));
        db.Repositories.Remove(repository);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    private async Task<RepositoryConnection> RequireAsync(int id, CancellationToken ct)
    {
        var repository = await db.Repositories.SingleOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new LibraryException(404, "This item is unavailable or you no longer have access.");
        (await new LibraryAccess(db).LoadAsync(UserId, ct)).Require(repository.FolderId, write: true);
        return repository;
    }

    private static string Token(string? value)
    {
        var token = value?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 500 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new LibraryException(400, "Enter a GitHub access token.");
        return token;
    }
}
