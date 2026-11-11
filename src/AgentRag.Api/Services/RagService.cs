using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentRag.Api.Services;

public record SearchResult(string DocumentTitle, string Content, double Score, int DocumentId, int PassageNumber);
public record IngestionProgress(string Stage, int CompletedChunks, int TotalChunks, DateTimeOffset? RetryAt = null, int RetryAttempt = 0);

public class RagService(AppDbContext db, EmbeddingService embeddings, ILogger<RagService>? logger = null,
    IOptions<EmbeddingOptions>? ingestionOptions = null)
{
    private readonly ILogger<RagService> log = logger ?? NullLogger<RagService>.Instance;
    public static IReadOnlyList<string> Chunk(string text, int size = 800, int overlap = 100)
    {
        var chunks = new List<string>();
        text = text.Trim();
        for (var start = 0; start < text.Length; start += size - overlap)
        {
            var len = Math.Min(size, text.Length - start);
            chunks.Add(text.Substring(start, len));
            if (start + len >= text.Length) break;
        }
        return chunks;
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public async Task<KnowledgeDocument> IngestAsync(string userId, string title, string text, CancellationToken ct = default)
        => await IngestAsync(userId, title, text, null, ct);

    public async Task<KnowledgeDocument> IngestAsync(string userId, string title, string text,
        Action<IngestionProgress>? report, CancellationToken ct = default, int? folderId = null)
    {
        if (folderId is { } initialFolder) (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(initialFolder, write: true);
        var watch = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["IngestionId"] = Guid.NewGuid(), ["UserId"] = userId });
        log.LogInformation("Starting document ingestion ({CharacterCount} characters)", text.Length);
        report?.Invoke(new("chunking", 0, 0));
        var parts = Chunk(text);
        if (parts.Count == 0) throw new ArgumentException("Document content must not be empty.", nameof(text));
        log.LogInformation("Document split into {ChunkCount} chunks; embedding batch size {BatchSize}", parts.Count, embeddings.BatchSize);
        var doc = new KnowledgeDocument { OwnerId = userId, AddedById = userId, Title = title, FolderId = folderId };
        for (var start = 0; start < parts.Count; start += embeddings.BatchSize)
        {
            var batch = parts.Skip(start).Take(embeddings.BatchSize).ToArray();
            report?.Invoke(new("embedding", start, parts.Count));
            var vectors = await embeddings.GenerateAsync(batch,
                retry => report?.Invoke(new("rateLimited", start, parts.Count, retry.RetryAt, retry.Attempt)), ct);
            for (var i = 0; i < batch.Length; i++)
            {
                doc.Chunks.Add(new DocumentChunk
                {
                    Index = start + i,
                    Content = batch[i],
                    Embedding = new Vector(vectors[i].Vector)
                });
            }
            log.LogInformation("Embedding progress: {CompletedChunks}/{TotalChunks} chunks", doc.Chunks.Count, parts.Count);
            report?.Invoke(new("embedding", doc.Chunks.Count, parts.Count));
        }
        report?.Invoke(new("saving", parts.Count, parts.Count));
        log.LogInformation("Saving document and {ChunkCount} vectors to PostgreSQL", parts.Count);
        // Embedding work happens outside the transaction. Re-check permissions at commit time,
        // so a revoked writer cannot publish the result of a long-running import.
        await using var transaction = folderId is not null ? await LibraryAccess.BeginMutationAsync(db, ct) : null;
        if (folderId is { } finalFolder)
            doc.OwnerId = (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(finalFolder, write: true).OwnerId;
        db.Documents.Add(doc);
        var relational = db.Database.IsRelational();
        var previousTimeout = relational ? db.Database.GetCommandTimeout() : null;
        try
        {
            // Large vector inserts may take longer than ordinary retrieval queries.
            if (relational) db.Database.SetCommandTimeout(ingestionOptions?.Value.SaveCommandTimeoutSeconds ?? 600);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        finally { if (relational) db.Database.SetCommandTimeout(previousTimeout); }
        log.LogInformation("Document {DocumentId} ingested: {ChunkCount} chunks in {ElapsedMs} ms", doc.Id, parts.Count, watch.ElapsedMilliseconds);
        return doc;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string userId, string query, int top = 4, CancellationToken ct = default, int? folderId = null)
    {
        if (top <= 0) return [];

        var q = new Vector((await embeddings.GenerateAsync([query], ct: ct))[0].Vector);
        var documents = await new LibraryAccess(db).ReadableDocumentsAsync(userId, folderId, ct);
        return await SearchChunks(documents, q, top).ToListAsync(ct);
    }

    internal IQueryable<SearchResult> SearchChunks(string userId, Vector query, int top) =>
        SearchChunks(db.Documents.Where(d => d.OwnerId == userId), query, top);

    internal IQueryable<SearchResult> SearchChunks(IQueryable<KnowledgeDocument> documents, Vector query, int top) =>
        db.Chunks.AsNoTracking()
            .Where(c => documents.Select(d => d.Id).Contains(c.DocumentId))
            .OrderBy(c => c.Embedding.CosineDistance(query))
            .ThenBy(c => c.Id)
            .Take(top)
            .Select(c => new SearchResult(c.Document!.Title, c.Content, 1 - c.Embedding.CosineDistance(query),
                c.DocumentId, c.Index + 1));
}
