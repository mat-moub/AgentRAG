using System.Collections.Concurrent;
using AgentRag.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AgentRag.Api.Services;

public record SyncProgress(string Stage, int Completed, int Total, string? Path = null);
/// <param name="Skipped">Changed files that turned out to be binary and were left out.</param>
public record SyncSummary(string Commit, int Added, int Updated, int Removed, int Unchanged, int Skipped);

/// <summary>Encrypts repository access tokens at rest. Tokens only leave this class to call GitHub.</summary>
public sealed class RepositoryTokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("AgentRag.RepositoryToken.v1");
    public string Protect(string token) => protector.Protect(token);
    public string Unprotect(string value) => protector.Unprotect(value);
}

/// <summary>Mirrors the text files of a GitHub repository as documents of the connection's folder.</summary>
public class RepositorySyncService(AppDbContext db, GitHubClient github, EmbeddingService embeddings,
    RepositoryTokenProtector tokens, ILogger<RepositorySyncService> logger, IOptions<EmbeddingOptions>? options = null)
{
    public const int MaxFiles = 20_000;
    /// <summary>Above this many changed files, one archive download replaces one request per file.</summary>
    public const int ArchiveThreshold = 40;
    private const int BatchFiles = 40;
    private const int BatchCharacters = 2_000_000;

    // One synchronisation per repository in this process: two would insert the same files twice.
    private static readonly ConcurrentDictionary<int, bool> Running = new();

    public async Task<SyncSummary> SyncAsync(string userId, int repositoryId, Action<SyncProgress>? report = null, CancellationToken ct = default)
    {
        var repository = await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) throw new LibraryException(404, "This item is unavailable or you no longer have access.");
        (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(repository.FolderId, write: true);
        if (!Running.TryAdd(repositoryId, true)) throw new LibraryException(409, "This repository is already being synchronised.");
        try
        {
            var summary = await SyncCoreAsync(userId, repository, report, ct);
            await SaveStateAsync(repositoryId, summary.Commit, null, ct);
            return summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Files saved before the failure stay: the next run only fetches what is still missing.
            await SaveStateAsync(repositoryId, null, ex is GitHubException or LibraryException ? ex.Message : "The synchronisation failed. Try again.", CancellationToken.None);
            throw;
        }
        finally { Running.TryRemove(repositoryId, out _); }
    }

    private async Task<SyncSummary> SyncCoreAsync(string userId, RepositoryConnection repository, Action<SyncProgress>? report, CancellationToken ct)
    {
        var (owner, name) = (repository.RepositoryOwner, repository.RepositoryName);
        string token;
        try { token = tokens.Unprotect(repository.ProtectedToken); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new GitHubException("The stored access token can no longer be read. Enter the token again.", 409);
        }

        report?.Invoke(new("listing", 0, 0));
        var tree = await github.GetTreeAsync(owner, name, repository.Branch, token, ct);
        if (tree.Truncated) throw new GitHubException("This repository has too many files for GitHub to list in one request.", 400);
        var files = tree.Files.Where(f => RepositoryFiles.IsEligible(f.Path, f.Size)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (files.Count > MaxFiles) throw new GitHubException($"This repository has more than {MaxFiles:N0} text files, which is over the limit.", 400);

        var existing = await db.Documents.AsNoTracking().Where(d => d.RepositoryId == repository.Id)
            .Select(d => new { d.Id, d.SourcePath, d.SourceSha }).ToListAsync(ct);
        var current = existing.Where(d => d.SourcePath is not null).GroupBy(d => d.SourcePath!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var removed = existing.Where(d => d.SourcePath is null || !files.ContainsKey(d.SourcePath)).Select(d => d.Id).ToList();
        var changed = files.Values.Where(f => !current.TryGetValue(f.Path, out var stored) || stored.Count != 1 || stored[0].SourceSha != f.Sha).ToList();
        var unchanged = files.Count - changed.Count;
        logger.LogInformation("Repository {Repository}: {Changed} files to fetch, {Removed} to remove, {Unchanged} unchanged at {Commit}",
            repository.Id, changed.Count, removed.Count, unchanged, tree.CommitSha);

        foreach (var ids in removed.Chunk(200)) await DeleteAsync(userId, repository.FolderId, ids, ct);

        var (added, updated, skipped, done) = (0, 0, 0, 0);
        var batch = new List<KnowledgeDocument>();
        var replaced = new List<int>();
        var batchCharacters = 0;
        var ownerId = (await db.Folders.AsNoTracking().SingleAsync(f => f.Id == repository.FolderId, ct)).OwnerId;
        await foreach (var (path, content) in FetchAsync(owner, name, tree.CommitSha, token, changed, ct))
        {
            report?.Invoke(new("fetching", done++, changed.Count, path));
            if (RepositoryFiles.Decode(content) is not { } text) { skipped++; continue; }
            var document = await BuildAsync(userId, ownerId, repository, files[path], text, ct);
            if (current.TryGetValue(path, out var previous)) { replaced.AddRange(previous.Select(p => p.Id)); updated++; }
            else added++;
            batch.Add(document);
            batchCharacters += text.Length;
            if (batch.Count < BatchFiles && batchCharacters < BatchCharacters) continue;
            await SaveBatchAsync(userId, repository.FolderId, batch, replaced, ct);
            batchCharacters = 0;
        }
        await SaveBatchAsync(userId, repository.FolderId, batch, replaced, ct);
        report?.Invoke(new("completed", changed.Count, changed.Count));
        return new(tree.CommitSha, added, updated, removed.Count, unchanged, skipped);
    }

    private async IAsyncEnumerable<(string Path, byte[] Content)> FetchAsync(string owner, string name, string commit, string token,
        List<GitHubFile> changed, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (changed.Count > ArchiveThreshold)
        {
            var wanted = changed.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            await foreach (var file in github.ReadArchiveAsync(owner, name, commit, token, wanted, ct)) yield return file;
            yield break;
        }
        foreach (var file in changed)
            yield return (file.Path, await github.GetBlobAsync(owner, name, file.Sha, token, ct));
    }

    private async Task<KnowledgeDocument> BuildAsync(string userId, string ownerId, RepositoryConnection repository, GitHubFile file, string text, CancellationToken ct)
    {
        var prose = RepositoryFiles.IsDocumentation(file.Path);
        var document = new KnowledgeDocument
        {
            OwnerId = ownerId, AddedById = userId, FolderId = repository.FolderId, Title = RepositoryFiles.Title(file.Path),
            RepositoryId = repository.Id, SourcePath = file.Path, SourceSha = file.Sha, Indexed = prose,
            PassageOverlap = prose ? RagService.ChunkOverlap : 0
        };
        if (!prose)
        {
            // Code keeps its exact text so line numbers match the repository.
            document.Text = new DocumentText { Content = text };
            var passages = RagService.ChunkLines(text);
            for (var i = 0; i < passages.Count; i++)
                document.Chunks.Add(new DocumentChunk { Index = i, Content = passages[i].Content, StartLine = passages[i].StartLine });
            return document;
        }
        document.Text = new DocumentText { Content = text.Trim() };
        var parts = RagService.Chunk(text);
        for (var start = 0; start < parts.Count; start += embeddings.BatchSize)
        {
            var slice = parts.Skip(start).Take(embeddings.BatchSize).ToArray();
            var vectors = await embeddings.GenerateAsync(slice, ct: ct);
            for (var i = 0; i < slice.Length; i++)
                document.Chunks.Add(new DocumentChunk { Index = start + i, Content = slice[i], Embedding = new Vector(vectors[i].Vector) });
        }
        return document;
    }

    // Fetching and embedding happen outside any transaction. Write access is checked again at each commit,
    // so a revoked writer cannot keep publishing files from a long synchronisation.
    private async Task SaveBatchAsync(string userId, int folderId, List<KnowledgeDocument> batch, List<int> replaced, CancellationToken ct)
    {
        if (batch.Count == 0 && replaced.Count == 0) return;
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(folderId, write: true);
        var ids = replaced.ToArray();
        db.Documents.RemoveRange(await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct));
        db.Documents.AddRange(batch);
        await SaveWithLongTimeoutAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
        replaced.Clear();
    }

    private async Task DeleteAsync(string userId, int folderId, int[] ids, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(folderId, write: true);
        db.Documents.RemoveRange(await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct));
        await SaveWithLongTimeoutAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task SaveWithLongTimeoutAsync(CancellationToken ct)
    {
        var relational = db.Database.IsRelational();
        var previous = relational ? db.Database.GetCommandTimeout() : null;
        try
        {
            if (relational) db.Database.SetCommandTimeout(options?.Value.SaveCommandTimeoutSeconds ?? 600);
            await db.SaveChangesAsync(ct);
        }
        finally { if (relational) db.Database.SetCommandTimeout(previous); }
    }

    private async Task SaveStateAsync(int repositoryId, string? commit, string? error, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var repository = await db.Repositories.SingleOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) return;
        if (commit is not null) (repository.LastSyncedCommit, repository.LastSyncedUtc) = (commit, DateTime.UtcNow);
        repository.LastError = error is { Length: > 500 } ? error[..500] : error;
        await db.SaveChangesAsync(ct);
    }
}
