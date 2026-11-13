using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRag.Api.Services;

public record SearchResult(string DocumentTitle, string Content, double Score, int DocumentId, int PassageNumber);
/// <param name="Line">One-based line of the match in its file; null for documents that are not split on line ends.</param>
public record GrepMatch(int DocumentId, string DocumentTitle, int PassageNumber, string Excerpt, int? Line = null);
public record GrepDocumentCount(int DocumentId, string DocumentTitle, int Passages);
/// <param name="TotalPassages">Matching passages across all accessible documents, before paging.</param>
/// <param name="Documents">Documents with the most matching passages.</param>
public record GrepResult(int TotalPassages, IReadOnlyList<GrepDocumentCount> Documents, IReadOnlyList<GrepMatch> Matches);
/// <param name="FirstLine">One-based line of the file where the text starts; null for documents that are not split on line ends.</param>
public record PassageRange(int DocumentId, string DocumentTitle, int FirstPassage, int LastPassage, int TotalPassages, string Text, int? FirstLine = null);
public record IngestionProgress(string Stage, int CompletedChunks, int TotalChunks, DateTimeOffset? RetryAt = null, int RetryAttempt = 0);

public class RagService(AppDbContext db, EmbeddingService embeddings, ILogger<RagService>? logger = null,
    IOptions<EmbeddingOptions>? ingestionOptions = null)
{
    private readonly ILogger<RagService> log = logger ?? NullLogger<RagService>.Instance;
    public const int ChunkSize = 800;
    public const int ChunkOverlap = 100;

    public static IReadOnlyList<string> Chunk(string text, int size = ChunkSize, int overlap = ChunkOverlap)
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

    /// <summary>Rebuilds the text covered by consecutive chunks, removing the overlap added by <see cref="Chunk"/>.</summary>
    /// <param name="continuation">True when these chunks follow chunks already reconstructed, so the first one's overlap is dropped too.</param>
    public static string ReconstructText(IReadOnlyList<string> chunks, bool continuation = false, int overlap = ChunkOverlap)
    {
        var text = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
            text.Append(i == 0 && !continuation ? chunks[i] : chunks[i].AsSpan(Math.Clamp(overlap, 0, chunks[i].Length)));
        return text.ToString();
    }

    public const int LineChunkSize = 1500;

    /// <summary>
    /// Splits source code and other line-oriented text on line ends, without overlap, so each passage knows its
    /// first line and joining the passages gives back the exact text. A single line longer than
    /// <paramref name="size"/> is cut into several passages that share its line number.
    /// </summary>
    public static IReadOnlyList<(string Content, int StartLine)> ChunkLines(string text, int size = LineChunkSize)
    {
        var chunks = new List<(string, int)>();
        var (start, startLine, line) = (0, 1, 1);
        for (var position = 0; position < text.Length;)
        {
            var newline = text.IndexOf('\n', position);
            var end = newline < 0 ? text.Length : newline + 1;
            // Close the current passage before a line that would overflow it.
            if (end - start > size && position > start)
            {
                chunks.Add((text[start..position], startLine));
                (start, startLine) = (position, line);
            }
            // A line too long for one passage: emit full-size pieces, never splitting a surrogate pair.
            while (end - start > size)
            {
                var cut = start + size;
                if (char.IsHighSurrogate(text[cut - 1])) cut--;
                chunks.Add((text[start..cut], startLine));
                start = cut;
            }
            position = end;
            line++;
        }
        if (start < text.Length) chunks.Add((text[start..], startLine));
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
        Action<IngestionProgress>? report, CancellationToken ct = default, int? folderId = null, bool index = true)
    {
        if (folderId is { } initialFolder) (await new LibraryAccess(db).LoadAsync(userId, ct)).Require(initialFolder, write: true);
        var watch = Stopwatch.StartNew();
        using var scope = log.BeginScope(new Dictionary<string, object> { ["IngestionId"] = Guid.NewGuid(), ["UserId"] = userId });
        log.LogInformation("Starting document ingestion ({CharacterCount} characters, indexed: {Indexed})", text.Length, index);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Document content must not be empty.", nameof(text));
        // The full text is always stored so the document can be read without rebuilding it from passages.
        var doc = new KnowledgeDocument
        {
            OwnerId = userId, AddedById = userId, Title = title, FolderId = folderId, Indexed = index,
            Text = new DocumentText { Content = text.Trim() }
        };
        report?.Invoke(new("chunking", 0, 0));
        // Every document gets passages so it can be grepped and read by passage; only indexed ones are embedded.
        var parts = Chunk(text);
        log.LogInformation("Document split into {ChunkCount} chunks; embedding batch size {BatchSize}", parts.Count, embeddings.BatchSize);
        if (!index)
            for (var i = 0; i < parts.Count; i++) doc.Chunks.Add(new DocumentChunk { Index = i, Content = parts[i] });
        for (var start = 0; index && start < parts.Count; start += embeddings.BatchSize)
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

    // SET LOCAL lasts for one transaction only, so pooled connections are never left reconfigured.
    // Iterative scans keep the HNSW index returning rows until the access filter is satisfied (pgvector 0.8+).
    internal const string IndexedScan = "SET LOCAL hnsw.iterative_scan = relaxed_order";
    internal const string ExactScan = "SET LOCAL enable_indexscan = off";
    internal const string GrepTimeout = "SET LOCAL statement_timeout = '15s'";

    /// <param name="exact">True compares the query with every passage instead of using the approximate HNSW index.</param>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string userId, string query, int top = 4, CancellationToken ct = default,
        int? folderId = null, int? documentId = null, bool exact = false)
    {
        if (top <= 0) return [];

        var q = new Vector((await embeddings.GenerateAsync([query], ct: ct))[0].Vector);
        var documents = await ReadableDocumentsAsync(userId, folderId, documentId, ct);
        // A plain ID list lets PostgreSQL walk the HNSW index and filter as it goes.
        // With the access rules as a subquery it sorts every accessible passage instead.
        var documentIds = await documents.Select(d => d.Id).ToArrayAsync(ct);
        var results = await WithLocalSettingAsync(exact ? ExactScan : IndexedScan,
            () => SearchChunks(documentIds, q, top, exact).ToListAsync(ct), ct);
        // A relaxed index scan may return rows slightly out of order.
        return results.OrderByDescending(r => r.Score).ToList();
    }

    internal IQueryable<SearchResult> SearchChunks(string userId, Vector query, int top, bool exact = false) =>
        SearchChunks(db.Documents.Where(d => d.OwnerId == userId), query, top, exact);

    internal IQueryable<SearchResult> SearchChunks(IQueryable<KnowledgeDocument> documents, Vector query, int top, bool exact = false) =>
        Rank(db.Chunks.AsNoTracking().Where(c => documents.Select(d => d.Id).Contains(c.DocumentId)), query, top, exact);

    internal IQueryable<SearchResult> SearchChunks(int[] documentIds, Vector query, int top, bool exact = false) =>
        Rank(db.Chunks.AsNoTracking().Where(c => documentIds.Contains(c.DocumentId)), query, top, exact);

    private static IQueryable<SearchResult> Rank(IQueryable<DocumentChunk> chunks, Vector query, int top, bool exact)
    {
        var ranked = chunks.Where(c => c.Embedding != null).OrderBy(c => c.Embedding!.CosineDistance(query));
        // A second sort key would stop PostgreSQL from reading rows straight from the HNSW index.
        if (exact) ranked = ranked.ThenBy(c => c.Id);
        return ranked.Take(top)
            .Select(c => new SearchResult(c.Document!.Title, c.Content, 1 - c.Embedding!.CosineDistance(query),
                c.DocumentId, c.Index + 1));
    }

    /// <summary>Finds passages containing a literal text or matching a PostgreSQL regular expression. No embedding is generated.</summary>
    public async Task<GrepResult> GrepAsync(string userId, string pattern, bool regex = false, bool caseSensitive = false,
        int offset = 0, int limit = 20, CancellationToken ct = default, int? folderId = null, int? documentId = null, string? path = null)
    {
        var documents = await ReadableDocumentsAsync(userId, folderId, documentId, ct);
        if (!string.IsNullOrWhiteSpace(path)) documents = FilterByTitle(documents, path.Trim());
        var chunks = GrepChunks(documents, pattern, regex, caseSensitive);
        try
        {
            return await WithLocalSettingAsync(GrepTimeout, async () =>
            {
                var counts = await chunks.GroupBy(c => new { c.DocumentId, c.Document!.Title })
                    .Select(g => new { g.Key.DocumentId, g.Key.Title, Passages = g.Count() }).ToListAsync(ct);
                var rows = await chunks.OrderBy(c => c.DocumentId).ThenBy(c => c.Index).Skip(offset).Take(limit)
                    .Select(c => new { c.DocumentId, c.Document!.Title, c.Index, c.Content, c.StartLine }).ToListAsync(ct);
                return new GrepResult(counts.Sum(c => c.Passages),
                    counts.OrderByDescending(c => c.Passages).ThenBy(c => c.DocumentId).Take(MaxGrepDocuments)
                        .Select(c => new GrepDocumentCount(c.DocumentId, c.Title, c.Passages)).ToList(),
                    rows.Select(r =>
                    {
                        var (index, length) = Locate(r.Content, pattern, regex, caseSensitive);
                        return new GrepMatch(r.DocumentId, r.Title, r.Index + 1, Excerpt(r.Content, index, length),
                            r.StartLine is { } first ? first + r.Content.AsSpan(0, index).Count('\n') : null);
                    }).ToList());
            }, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidRegularExpression)
        {
            throw new ArgumentException("The pattern is not a valid PostgreSQL regular expression.", nameof(pattern));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled && !ct.IsCancellationRequested)
        {
            throw new ArgumentException("The pattern took too long to evaluate. Use a simpler or more specific pattern.", nameof(pattern));
        }
    }

    public const int MaxGrepDocuments = 20;

    internal IQueryable<DocumentChunk> GrepChunks(IQueryable<KnowledgeDocument> documents, string pattern, bool regex, bool caseSensitive)
    {
        var chunks = db.Chunks.AsNoTracking().Where(c => documents.Select(d => d.Id).Contains(c.DocumentId));
        // Options are written as constants so the provider can translate them to ~ and ~*.
        if (regex)
            return caseSensitive
                ? chunks.Where(c => Regex.IsMatch(c.Content, pattern))
                : chunks.Where(c => Regex.IsMatch(c.Content, pattern, RegexOptions.IgnoreCase));
        if (!db.Database.IsRelational())
            return caseSensitive
                ? chunks.Where(c => c.Content.Contains(pattern))
                : chunks.Where(c => c.Content.ToLower().Contains(pattern.ToLower()));
        var like = "%" + EscapeLike(pattern) + "%";
        return caseSensitive
            ? chunks.Where(c => EF.Functions.Like(c.Content, like, @"\"))
            : chunks.Where(c => EF.Functions.ILike(c.Content, like, @"\"));
    }

    internal static string EscapeLike(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Cuts a passage down to the text around its first match, so many matches stay cheap to return.</summary>
    internal static string Excerpt(string content, string pattern, bool regex, bool caseSensitive, int radius = 150)
    {
        var (index, length) = Locate(content, pattern, regex, caseSensitive);
        return Excerpt(content, index, length, radius);
    }

    /// <summary>Position of the first match inside a passage PostgreSQL selected; (0, 0) when .NET cannot locate it.</summary>
    internal static (int Index, int Length) Locate(string content, string pattern, bool regex, bool caseSensitive)
    {
        var (index, length) = (0, 0);
        if (regex)
        {
            // PostgreSQL did the matching; .NET only locates it for display. If the dialects disagree, show the passage start.
            try
            {
                var match = Regex.Match(content, pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                if (match.Success) (index, length) = (match.Index, match.Length);
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { }
        }
        else if (content.IndexOf(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) is >= 0 and var found)
            (index, length) = (found, pattern.Length);
        return (index, length);
    }

    internal static string Excerpt(string content, int index, int length, int radius = 150)
    {
        var start = Math.Max(0, index - radius);
        var end = Math.Min(content.Length, index + length + radius);
        // Never cut a surrogate pair in half.
        if (start > 0 && char.IsLowSurrogate(content[start])) start++;
        if (end < content.Length && end > start && char.IsHighSurrogate(content[end - 1])) end--;
        return (start > 0 ? "…" : "") + content[start..end] + (end < content.Length ? "…" : "");
    }

    /// <summary>Returns consecutive passages of one document as continuous text, without the chunk overlap.</summary>
    /// <param name="from">One-based number of the first passage.</param>
    public async Task<PassageRange> ReadPassagesAsync(string userId, int documentId, int from = 1, int count = 5,
        CancellationToken ct = default, int? folderId = null)
    {
        var documents = await ReadableDocumentsAsync(userId, folderId, documentId, ct);
        var document = await documents.Select(d => new { d.Title, d.PassageOverlap, Total = d.Chunks.Count }).SingleOrDefaultAsync(ct)
            ?? throw new LibraryException(404, "This document is unavailable.");
        var passages = await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == documentId && c.Index >= from - 1 && c.Index < from - 1 + count)
            .OrderBy(c => c.Index).Select(c => new { c.Content, c.StartLine }).ToListAsync(ct);
        return new(documentId, document.Title, from, from + passages.Count - 1, document.Total,
            ReconstructText(passages.Select(p => p.Content).ToList(), overlap: document.PassageOverlap),
            passages.FirstOrDefault()?.StartLine);
    }

    /// <summary>Keeps documents whose title (a file path for repository files) contains the text; * matches any characters.</summary>
    public IQueryable<KnowledgeDocument> FilterByTitle(IQueryable<KnowledgeDocument> documents, string text)
    {
        if (db.Database.IsRelational())
        {
            var like = "%" + EscapeLike(text).Replace("*", "%") + "%";
            return documents.Where(d => EF.Functions.ILike(d.Title, like, @"\"));
        }
        foreach (var part in text.ToLowerInvariant().Split('*', StringSplitOptions.RemoveEmptyEntries))
            documents = documents.Where(d => d.Title.ToLower().Contains(part));
        return documents;
    }

    private async Task<IQueryable<KnowledgeDocument>> ReadableDocumentsAsync(string userId, int? folderId, int? documentId, CancellationToken ct)
    {
        var documents = await new LibraryAccess(db).ReadableDocumentsAsync(userId, folderId, ct);
        return documentId is { } id ? documents.Where(d => d.Id == id) : documents;
    }

    private async Task<T> WithLocalSettingAsync<T>(string setting, Func<Task<T>> query, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await query();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(setting, ct);
        var result = await query();
        await transaction.CommitAsync(ct);
        return result;
    }
}
