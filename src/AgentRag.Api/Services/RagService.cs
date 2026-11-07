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
        Action<IngestionProgress>? report, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["IngestionId"] = Guid.NewGuid(), ["UserId"] = userId });
        log.LogInformation("Starting document ingestion ({CharacterCount} characters)", text.Length);
        report?.Invoke(new("chunking", 0, 0));
        var parts = Chunk(text);
        if (parts.Count == 0) throw new ArgumentException("Document content must not be empty.", nameof(text));
        log.LogInformation("Document split into {ChunkCount} chunks; embedding batch size {BatchSize}", parts.Count, embeddings.BatchSize);
        var doc = new KnowledgeDocument { OwnerId = userId, Title = title };
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
        db.Documents.Add(doc);
        var previousTimeout = db.Database.GetCommandTimeout();
        try
        {
            // Large vector inserts may take longer than ordinary retrieval queries.
            db.Database.SetCommandTimeout(ingestionOptions?.Value.SaveCommandTimeoutSeconds ?? 600);
            await db.SaveChangesAsync(ct);
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
        log.LogInformation("Document {DocumentId} ingested: {ChunkCount} chunks in {ElapsedMs} ms", doc.Id, parts.Count, watch.ElapsedMilliseconds);
        return doc;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string userId, string query, int top = 4, CancellationToken ct = default)
    {
        if (top <= 0) return [];

        var q = new Vector((await embeddings.GenerateAsync([query], ct: ct))[0].Vector);
        return await SearchChunks(userId, q, top).ToListAsync(ct);
    }

    internal IQueryable<SearchResult> SearchChunks(string userId, Vector query, int top) =>
        db.Chunks.AsNoTracking()
            .Where(c => c.Document!.OwnerId == userId)
            .OrderBy(c => c.Embedding.CosineDistance(query))
            .ThenBy(c => c.Id)
            .Take(top)
            .Select(c => new SearchResult(c.Document!.Title, c.Content, 1 - c.Embedding.CosineDistance(query),
                c.DocumentId, c.Index + 1));
}
