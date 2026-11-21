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

/// <summary>Mirrors the text files of a connected Git repository as documents of the connection's folder.</summary>
public class RepositorySyncService(AppDbContext db, RepositoryHosts hosts, EmbeddingService embeddings,
    RepositoryTokenProtector tokens, ILogger<RepositorySyncService> logger, IOptions<EmbeddingOptions>? options = null)
{
    public const int MaxFiles = 20_000;
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
            await SaveStateAsync(repositoryId, null, ex is RepositoryHostException or LibraryException ? ex.Message : "The synchronisation failed. Try again.", CancellationToken.None);
            throw;
        }
        finally { Running.TryRemove(repositoryId, out _); }
    }

    private async Task<SyncSummary> SyncCoreAsync(string userId, RepositoryConnection repository, Action<SyncProgress>? report, CancellationToken ct)
    {
        var host = hosts.For(repository.Provider);
        var address = RepositoryAddress.From(repository);
        string token;
        try { token = tokens.Unprotect(repository.ProtectedToken); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new RepositoryHostException("The stored access token can no longer be read. Enter the token again.", 409);
        }

        report?.Invoke(new("listing", 0, 0));
        var tree = await host.GetTreeAsync(address, repository.Branch, token, ct);
        if (tree.Truncated) throw new RepositoryHostException("This repository has too many files for its host to list in one request.", 400);
        var files = tree.Files.Where(f => RepositoryFiles.IsEligible(f.Path, f.Size)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (files.Count > MaxFiles) throw new RepositoryHostException($"This repository has more than {MaxFiles:N0} text files, which is over the limit.", 400);

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
        var batch = new List<(KnowledgeDocument Version, int? Existing)>();
        var replaced = new List<int>();
        var batchCharacters = 0;
        var ownerId = (await db.Folders.AsNoTracking().SingleAsync(f => f.Id == repository.FolderId, ct)).OwnerId;
        await foreach (var (path, content) in host.ReadFilesAsync(address, tree.CommitSha, changed, token, ct))
        {
            report?.Invoke(new("fetching", done++, changed.Count, path));
            if (RepositoryFiles.Decode(content) is not { } text) { skipped++; continue; }
            var document = await BuildAsync(userId, ownerId, repository, files[path], text, ct);
            int? storedId = null;
            if (current.TryGetValue(path, out var previous))
            {
                updated++;
                // A single stored version is updated in place: its id, the wiki pages written from it and doc: citations stay valid.
                if (previous.Count == 1) storedId = previous[0].Id;
                else replaced.AddRange(previous.Select(p => p.Id));
            }
            else
            {
                added++;
            }

            batch.Add((document, storedId));
            batchCharacters += text.Length;
            if (batch.Count < BatchFiles && batchCharacters < BatchCharacters) continue;
            await SaveBatchAsync(userId, repository.FolderId, batch, replaced, ct);
            batchCharacters = 0;
        }
        await SaveBatchAsync(userId, repository.FolderId, batch, replaced, ct);
        report?.Invoke(new("completed", changed.Count, changed.Count));
        return new(tree.CommitSha, added, updated, removed.Count, unchanged, skipped);
    }

    private async Task<KnowledgeDocument> BuildAsync(string userId, string ownerId, RepositoryConnection repository, RepositoryFile file, string text, CancellationToken ct)
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
    private async Task SaveBatchAsync(string userId, int folderId, List<(KnowledgeDocument Version, int? Existing)> batch, List<int> replaced, CancellationToken ct)
    {
        if (batch.Count == 0 && replaced.Count == 0) return;
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(folderId, write: true);
        var ids = replaced.ToArray();
        var targets = batch.Where(b => b.Existing is not null).Select(b => b.Existing!.Value).ToArray();
        await MarkWikiPagesStaleAsync([.. ids, .. targets], ct);
        db.Documents.RemoveRange(await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct));
        var stored = await db.Documents.Include(d => d.Chunks).Include(d => d.Text).Where(d => targets.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        foreach (var (version, existing) in batch)
        {
            if (existing is { } id && stored.TryGetValue(id, out var document)) Update(document, version);
            else db.Documents.Add(version);
        }
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
        await MarkWikiPagesStaleAsync(ids, ct);
        db.Documents.RemoveRange(await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct));
        await SaveWithLongTimeoutAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Gives a stored file the content of its new version, keeping its id.</summary>
    private void Update(KnowledgeDocument document, KnowledgeDocument version)
    {
        (document.Title, document.SourceSha, document.Indexed, document.PassageOverlap) =
            (version.Title, version.SourceSha, version.Indexed, version.PassageOverlap);
        if (document.Text is null) document.Text = version.Text;
        else document.Text.Content = version.Text!.Content;
        db.Chunks.RemoveRange(document.Chunks);
        document.Chunks.Clear();
        document.Chunks.AddRange(version.Chunks);
    }

    /// <summary>
    /// Wiki pages written from files that change or disappear are flagged at once, so readers know before the
    /// next wiki refresh that a page may no longer match the code.
    /// </summary>
    private async Task MarkWikiPagesStaleAsync(int[] documentIds, CancellationToken ct)
    {
        if (documentIds.Length == 0) return;
        var pages = await db.WikiPages
            .Where(p => !p.Stale && p.Sources.Any(s => s.DocumentId != null && documentIds.Contains(s.DocumentId.Value)))
            .ToListAsync(ct);
        foreach (var page in pages) page.Stale = true;
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
