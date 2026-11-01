using System.Runtime.InteropServices;
using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AgentRag.Api.Services;

public record SearchResult(string DocumentTitle, string Content, double Score);

public class RagService(AppDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
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
    {
        var parts = Chunk(text);
        var vectors = await embeddings.GenerateAsync(parts, cancellationToken: ct);
        var doc = new KnowledgeDocument { OwnerId = userId, Title = title };
        for (var i = 0; i < parts.Count; i++)
        {
            doc.Chunks.Add(new DocumentChunk
            {
                Index = i,
                Content = parts[i],
                Embedding = MemoryMarshal.AsBytes(vectors[i].Vector.Span).ToArray()
            });
        }
        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string userId, string query, int top = 4, CancellationToken ct = default)
    {
        var q = (await embeddings.GenerateAsync([query], cancellationToken: ct))[0].Vector.ToArray();
        var chunks = await db.Chunks.AsNoTracking()
            .Where(c => c.Document!.OwnerId == userId)
            .Select(c => new { c.Content, c.Embedding, Title = c.Document!.Title })
            .ToListAsync(ct);
        return chunks
            .Select(c => new SearchResult(c.Title, c.Content, Cosine(q, MemoryMarshal.Cast<byte, float>(c.Embedding))))
            .OrderByDescending(r => r.Score)
            .Take(top)
            .ToList();
    }
}
