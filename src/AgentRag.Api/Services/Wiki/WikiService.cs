using System.Text.Json;
using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace AgentRag.Api.Services;

public record WikiRunDto(int Id, string Mode, string Status, string Stage, int Completed, int Total, DateTime CreatedUtc,
    DateTime? StartedUtc, DateTime? FinishedUtc, string? Error, JsonElement? Stats)
{
    public static WikiRunDto From(WikiRun run) => new(run.Id, run.Mode, run.Status, run.Stage, run.Completed, run.Total, run.CreatedUtc,
        run.StartedUtc, run.FinishedUtc, run.Error, run.Stats is null ? null : JsonSerializer.Deserialize<JsonElement>(run.Stats));
}

/// <param name="Folder">Path of the folder the wiki covers, as the user sees it.</param>
/// <param name="RefreshedUtc">End of the last completed run; null when the wiki was never built.</param>
public record WikiDto(int Id, int FolderId, string Folder, string Language, string? Guidelines, int Pages, bool CanRefresh,
    bool CanDelete, WikiRunDto? LastRun, DateTime? RefreshedUtc);
public record WikiPageSummary(string Slug, string Title, string Kind, string Summary, bool Stale, DateTime UpdatedUtc);
/// <param name="Link">Kind of the link; for "implements" the code page is the origin and the functional page the target.</param>
public record WikiLinkDto(string Slug, string Title, string Kind, string Link, string? Note);
public record WikiSourceDto(int? DocumentId, string Title, int? RepositoryId, string? Path);
public record WikiPageDetail(int WikiId, string Slug, string Title, string Kind, string[] Aliases, string Summary, string Content,
    bool Stale, DateTime UpdatedUtc, IReadOnlyList<WikiLinkDto> Links, IReadOnlyList<WikiLinkDto> Backlinks, IReadOnlyList<WikiSourceDto> Sources);
/// <param name="Match">"literal" when the text was found in the page, "semantic" when the page is close in meaning.</param>
public record WikiPageHit(string Slug, string Title, string Kind, string Summary, string Match, string? Excerpt);
/// <param name="Id">Page slug, or "doc:{id}" for a source document.</param>
public record WikiGraphNode(string Id, string Label, string Kind, string? Summary, int Degree);
public record WikiGraphEdge(string Source, string Target, string Kind);
public record WikiGraph(IReadOnlyList<WikiGraphNode> Nodes, IReadOnlyList<WikiGraphEdge> Edges, bool Truncated);
/// <param name="PendingSources">Documents added, changed or removed since the wiki last processed its sources.</param>
/// <param name="ExcludedSources">Documents in subfolders shared with a different audience, which the wiki leaves out.</param>
public record WikiStatus(int Pages, int StalePages, int Sources, int PendingSources, int ExcludedSources);
/// <param name="Functional">Functional pages (features, rules, concepts) connected to the starting point.</param>
/// <param name="Code">Code pages connected to the starting point, with what they do for it.</param>
public record WikiTrace(IReadOnlyList<WikiPageSummary> Pages, IReadOnlyList<WikiLinkDto> Functional, IReadOnlyList<WikiLinkDto> Code,
    IReadOnlyList<WikiSourceDto> Sources);

/// <summary>Reads and administers wikis. Every read of the HTTP API and of the MCP tools goes through here.</summary>
public class WikiService(AppDbContext db, EmbeddingService embeddings, WikiRunQueue queue)
{
    public const int MaxGraphNodes = 1500;
    private const string Unavailable = "This wiki is unavailable or you no longer have access.";

    /// <summary>
    /// Documents a wiki is built from: those of its folder and of the subfolders shared with the same people.
    /// A subfolder with its own members has another audience, so its documents must not flow into this wiki.
    /// </summary>
    public static IQueryable<KnowledgeDocument> ScopeDocuments(AppDbContext db, LibrarySnapshot snapshot, int folderId)
    {
        var root = snapshot.ShareRoot(folderId);
        var folders = snapshot.Descendants(folderId).Where(id => snapshot.ShareRoot(id) == root).ToArray();
        return db.Documents.AsNoTracking().Where(d => d.FolderId != null && folders.Contains(d.FolderId.Value));
    }

    /// <summary>
    /// Pages the user may read: sharing can change after a build, so a page is hidden as soon as one of the
    /// documents it was written from is no longer readable. The overview lists every page, so it is hidden with them.
    /// </summary>
    public IQueryable<WikiPage> VisiblePages(LibrarySnapshot snapshot, int wikiId)
    {
        var readable = snapshot.Documents(db).Select(d => d.Id);
        var pages = db.WikiPages.AsNoTracking().Where(p => p.WikiId == wikiId &&
            !db.WikiPageSources.Any(s => s.PageId == p.Id && s.DocumentId != null && !readable.Contains(s.DocumentId.Value)));
        return pages.Where(p => p.Kind != WikiPageKinds.Overview || pages.Count() == db.WikiPages.Count(o => o.WikiId == wikiId));
    }

    public async Task<(Wiki Wiki, LibrarySnapshot Snapshot)> RequireAsync(string userId, int wikiId, CancellationToken ct, bool write = false, bool owner = false)
    {
        var wiki = await db.Wikis.SingleOrDefaultAsync(w => w.Id == wikiId, ct) ?? throw new LibraryException(404, Unavailable);
        var snapshot = await new LibraryAccess(db).LoadAsync(userId, ct);
        if (snapshot.Role(wiki.FolderId) is null) throw new LibraryException(404, Unavailable);
        snapshot.Require(wiki.FolderId, write, owner);
        return (wiki, snapshot);
    }

    public async Task<IReadOnlyList<WikiDto>> ListAsync(string userId, CancellationToken ct)
    {
        var snapshot = await new LibraryAccess(db).LoadAsync(userId, ct);
        var readable = snapshot.Folders.Keys.Where(id => snapshot.Role(id) is not null).ToArray();
        var wikis = await db.Wikis.AsNoTracking().Where(w => readable.Contains(w.FolderId)).OrderBy(w => w.Id).ToListAsync(ct);
        var result = new List<WikiDto>();
        foreach (var wiki in wikis) result.Add(await DescribeAsync(wiki, snapshot, ct));
        return result;
    }

    public async Task<WikiDto> GetAsync(string userId, int wikiId, CancellationToken ct)
    {
        var (wiki, snapshot) = await RequireAsync(userId, wikiId, ct);
        return await DescribeAsync(wiki, snapshot, ct);
    }

    public async Task<WikiDto> CreateAsync(string userId, int folderId, string? language, string? guidelines, CancellationToken ct)
    {
        await using var transaction = await LibraryAccess.BeginMutationAsync(db, ct);
        var snapshot = await new LibraryAccess(db).LoadAsync(userId, ct);
        snapshot.Require(folderId, write: true);
        if (await db.Wikis.AnyAsync(w => w.FolderId == folderId, ct)) throw new LibraryException(409, "This folder already has a wiki.");
        var wiki = new Wiki { FolderId = folderId, Language = Language(language), Guidelines = Guidelines(guidelines) };
        db.Wikis.Add(wiki);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await DescribeAsync(wiki, snapshot, ct);
    }

    public async Task<WikiDto> UpdateAsync(string userId, int wikiId, string? language, string? guidelines, CancellationToken ct)
    {
        var (wiki, snapshot) = await RequireAsync(userId, wikiId, ct, write: true);
        (wiki.Language, wiki.Guidelines) = (Language(language), Guidelines(guidelines));
        await db.SaveChangesAsync(ct);
        return await DescribeAsync(wiki, snapshot, ct);
    }

    public async Task DeleteAsync(string userId, int wikiId, CancellationToken ct)
    {
        var (wiki, _) = await RequireAsync(userId, wikiId, ct, owner: true);
        foreach (var run in await db.WikiRuns.Where(r => r.WikiId == wikiId && r.Status == WikiRunStatus.Running).Select(r => r.Id).ToListAsync(ct))
            queue.Cancel(run);
        db.Wikis.Remove(wiki);
        await db.SaveChangesAsync(ct);
    }

    public async Task<WikiRunDto> StartRunAsync(string userId, int wikiId, string? mode, CancellationToken ct)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? WikiRunModes.Incremental : mode.Trim().ToLowerInvariant();
        if (!WikiRunModes.All.Contains(mode)) throw new LibraryException(400, "Choose the mode incremental, full or lint.");
        await RequireAsync(userId, wikiId, ct, write: true);
        if (await db.WikiRuns.AnyAsync(r => r.WikiId == wikiId && (r.Status == WikiRunStatus.Queued || r.Status == WikiRunStatus.Running), ct))
            throw new LibraryException(409, "This wiki is already being refreshed.");
        var run = new WikiRun { WikiId = wikiId, Mode = mode, RequestedById = userId };
        db.WikiRuns.Add(run);
        // The partial unique index settles a race between two requests.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new LibraryException(409, "This wiki is already being refreshed."); }
        queue.Signal();
        return WikiRunDto.From(run);
    }

    public async Task<IReadOnlyList<WikiRunDto>> ListRunsAsync(string userId, int wikiId, CancellationToken ct, int limit = 20)
    {
        await RequireAsync(userId, wikiId, ct);
        return (await db.WikiRuns.AsNoTracking().Where(r => r.WikiId == wikiId).OrderByDescending(r => r.Id).Take(limit).ToListAsync(ct))
            .Select(WikiRunDto.From).ToList();
    }

    /// <param name="checkAccess">False for a repeated read of a run whose access was just checked: only the run row is read.</param>
    public async Task<WikiRunDto> GetRunAsync(string userId, int wikiId, int runId, CancellationToken ct, bool checkAccess = true)
    {
        if (checkAccess) await RequireAsync(userId, wikiId, ct);
        var run = await db.WikiRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.WikiId == wikiId, ct)
            ?? throw new LibraryException(404, "This run is unavailable.");
        return WikiRunDto.From(run);
    }

    public async Task<WikiRunDto> CancelRunAsync(string userId, int wikiId, int runId, CancellationToken ct)
    {
        await RequireAsync(userId, wikiId, ct, write: true);
        var run = await db.WikiRuns.SingleOrDefaultAsync(r => r.Id == runId && r.WikiId == wikiId, ct)
            ?? throw new LibraryException(404, "This run is unavailable.");
        // A running build stops at its next model call and records the cancellation itself.
        if (run.Status == WikiRunStatus.Running && queue.Cancel(runId)) return WikiRunDto.From(run);
        if (WikiRunStatus.IsActive(run.Status))
        {
            (run.Status, run.Stage, run.FinishedUtc) = (WikiRunStatus.Cancelled, "cancelled", DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        return WikiRunDto.From(run);
    }

    public async Task<WikiStatus> StatusAsync(string userId, int wikiId, CancellationToken ct)
    {
        var (wiki, snapshot) = await RequireAsync(userId, wikiId, ct);
        var scope = await ScopeDocuments(db, snapshot, wiki.FolderId)
            .Select(d => new ScopeDocument(d.Id, d.Title, d.RepositoryId, d.SourcePath, d.SourceSha)).ToListAsync(ct);
        var folders = snapshot.Descendants(wiki.FolderId).ToArray();
        var inFolders = await db.Documents.CountAsync(d => d.FolderId != null && folders.Contains(d.FolderId.Value), ct);
        var manifest = await db.WikiSources.AsNoTracking().Where(s => s.WikiId == wikiId && s.Type != WikiDelta.Module).ToListAsync(ct);
        var delta = WikiDelta.Compute(scope, manifest);
        var pages = VisiblePages(snapshot, wikiId);
        return new(await pages.CountAsync(ct), await pages.CountAsync(p => p.Stale, ct), scope.Count,
            delta.Added.Count + delta.Changed.Count + delta.Removed.Count, inFolders - scope.Count);
    }

    public async Task<IReadOnlyList<WikiPageSummary>> ListPagesAsync(string userId, int wikiId, CancellationToken ct,
        string? kind = null, string? query = null, int offset = 0, int limit = 500)
    {
        var (_, snapshot) = await RequireAsync(userId, wikiId, ct);
        var pages = VisiblePages(snapshot, wikiId);
        if (!string.IsNullOrWhiteSpace(kind)) pages = pages.Where(p => p.Kind == kind);
        if (!string.IsNullOrWhiteSpace(query)) pages = Containing(pages, query.Trim(), content: false);
        return await pages.OrderBy(p => p.Kind).ThenBy(p => p.Title).Skip(offset).Take(limit)
            .Select(p => new WikiPageSummary(p.Slug, p.Title, p.Kind, p.Summary, p.Stale, p.UpdatedUtc)).ToListAsync(ct);
    }

    public async Task<WikiPageDetail> ReadPageAsync(string userId, int wikiId, string slug, CancellationToken ct)
    {
        var (_, snapshot) = await RequireAsync(userId, wikiId, ct);
        var visible = VisiblePages(snapshot, wikiId);
        var wanted = WikiText.Slug(slug);
        var page = await visible.SingleOrDefaultAsync(p => p.Slug == slug || p.Slug == wanted, ct)
            ?? throw new LibraryException(404, "This page is unavailable.");
        var links = await db.WikiLinks.AsNoTracking().Where(l => l.FromPageId == page.Id && visible.Any(p => p.Id == l.ToPageId))
            .Select(l => new WikiLinkDto(l.ToPage!.Slug, l.ToPage.Title, l.ToPage.Kind, l.Kind, l.Note)).ToListAsync(ct);
        var backlinks = await db.WikiLinks.AsNoTracking().Where(l => l.ToPageId == page.Id && visible.Any(p => p.Id == l.FromPageId))
            .Select(l => new WikiLinkDto(l.FromPage!.Slug, l.FromPage.Title, l.FromPage.Kind, l.Kind, l.Note)).ToListAsync(ct);
        return new(wikiId, page.Slug, page.Title, page.Kind, page.Aliases, page.Summary, page.Content, page.Stale, page.UpdatedUtc,
            Order(links), Order(backlinks), await SourcesAsync([page.Id], ct));
    }

    /// <summary>Literal matches first, then pages close in meaning when the literal search leaves room.</summary>
    public async Task<IReadOnlyList<WikiPageHit>> SearchAsync(string userId, int wikiId, string query, int top, CancellationToken ct)
    {
        var (_, snapshot) = await RequireAsync(userId, wikiId, ct);
        var visible = VisiblePages(snapshot, wikiId);
        query = query.Trim();
        var literal = await Containing(visible, query, content: true).OrderBy(p => p.Title).Take(top * 4)
            .Select(p => new { p.Slug, p.Title, p.Kind, p.Summary, p.Content }).ToListAsync(ct);
        var hits = literal
            .OrderByDescending(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Summary.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(top)
            .Select(p => new WikiPageHit(p.Slug, p.Title, p.Kind, p.Summary, "literal",
                p.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ? RagService.Excerpt(p.Content, query, false, false) : null))
            .ToList();
        if (hits.Count >= top || !db.Database.IsRelational()) return hits;
        Vector vector;
        // The semantic pass only adds pages: when the embedding model is unavailable, the literal hits still answer.
        try { vector = new Vector((await embeddings.GenerateAsync([query], ct: ct))[0].Vector); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return hits; }
        var found = hits.Select(h => h.Slug).ToArray();
        var close = await visible.Where(p => p.Embedding != null && !found.Contains(p.Slug))
            .OrderBy(p => p.Embedding!.CosineDistance(vector)).Take(top - hits.Count)
            .Select(p => new WikiPageHit(p.Slug, p.Title, p.Kind, p.Summary, "semantic", null)).ToListAsync(ct);
        return [.. hits, .. close];
    }

    /// <param name="center">Slug of a page: only pages within <paramref name="depth"/> links of it are returned.</param>
    /// <param name="kinds">Page kinds to keep; all when empty.</param>
    /// <param name="includeSources">Adds the source documents of the pages as nodes.</param>
    public async Task<WikiGraph> GraphAsync(string userId, int wikiId, CancellationToken ct, string? center = null, int depth = 1,
        IReadOnlyCollection<string>? kinds = null, bool includeSources = false)
    {
        var (_, snapshot) = await RequireAsync(userId, wikiId, ct);
        var pages = await VisiblePages(snapshot, wikiId).Select(p => new { p.Id, p.Slug, p.Title, p.Kind, p.Summary }).ToListAsync(ct);
        var byId = pages.ToDictionary(p => p.Id);
        var links = (await db.WikiLinks.AsNoTracking().Where(l => l.WikiId == wikiId)
                .Select(l => new { l.FromPageId, l.ToPageId, l.Kind }).ToListAsync(ct))
            .Where(l => byId.ContainsKey(l.FromPageId) && byId.ContainsKey(l.ToPageId)).ToList();

        var kept = pages.Select(p => p.Id).ToHashSet();
        if (!string.IsNullOrWhiteSpace(center))
        {
            var start = pages.FirstOrDefault(p => p.Slug == center || p.Slug == WikiText.Slug(center))
                ?? throw new LibraryException(404, "This page is unavailable.");
            kept = [start.Id];
            var frontier = new List<int> { start.Id };
            for (var level = 0; level < Math.Clamp(depth, 1, 4) && frontier.Count > 0; level++)
            {
                // The overview links to everything: walking through it would return the whole wiki.
                var from = frontier.Where(id => level == 0 || byId[id].Kind != WikiPageKinds.Overview).ToHashSet();
                frontier = links.SelectMany(l => from.Contains(l.FromPageId) ? [l.ToPageId] : from.Contains(l.ToPageId) ? new[] { l.FromPageId } : [])
                    .Where(kept.Add).ToList();
            }
        }
        if (kinds is { Count: > 0 }) kept.RemoveWhere(id => !kinds.Contains(byId[id].Kind));
        var truncated = kept.Count > MaxGraphNodes;
        var degree = links.SelectMany(l => new[] { l.FromPageId, l.ToPageId }).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        if (truncated) kept = kept.OrderByDescending(id => degree.GetValueOrDefault(id)).Take(MaxGraphNodes).ToHashSet();

        var nodes = kept.Select(id => byId[id])
            .Select(p => new WikiGraphNode(p.Slug, p.Title, p.Kind, p.Summary, degree.GetValueOrDefault(p.Id))).ToList();
        var edges = links.Where(l => kept.Contains(l.FromPageId) && kept.Contains(l.ToPageId))
            .Select(l => new WikiGraphEdge(byId[l.FromPageId].Slug, byId[l.ToPageId].Slug, l.Kind)).ToList();
        if (includeSources)
        {
            var ids = kept.ToArray();
            var sources = await db.WikiPageSources.AsNoTracking().Where(s => ids.Contains(s.PageId) && s.DocumentId != null)
                .Select(s => new { s.PageId, DocumentId = s.DocumentId!.Value, s.Document!.Title, Code = s.Document.RepositoryId != null && !s.Document.Indexed })
                .Take(MaxGraphNodes * 4).ToListAsync(ct);
            nodes.AddRange(sources.GroupBy(s => s.DocumentId)
                .Select(g => new WikiGraphNode($"doc:{g.Key}", g.First().Title, g.First().Code ? "code-file" : "document", null, g.Count())));
            edges.AddRange(sources.Select(s => new WikiGraphEdge(byId[s.PageId].Slug, $"doc:{s.DocumentId}", "source")));
        }
        return new(nodes, edges, truncated);
    }

    /// <summary>
    /// Follows the links between functional documentation and code from a page, a document or a repository file path.
    /// </summary>
    public async Task<WikiTrace> TraceAsync(string userId, int wikiId, string? slug, int? documentId, string? path, CancellationToken ct)
    {
        var (_, snapshot) = await RequireAsync(userId, wikiId, ct);
        var visible = VisiblePages(snapshot, wikiId);
        IQueryable<WikiPage> start;
        if (!string.IsNullOrWhiteSpace(slug))
        {
            var wanted = WikiText.Slug(slug);
            start = visible.Where(p => p.Slug == slug || p.Slug == wanted);
        }
        else if (documentId is { } id)
        {
            start = visible.Where(p => p.Sources.Any(s => s.DocumentId == id));
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            var suffix = ":" + path.Trim().Replace('\\', '/').TrimStart('/');
#pragma warning disable CA1310 // EF Core translates EndsWith(string) to SQL; the StringComparison overload is not translatable.
            start = visible.Where(p => p.Sources.Any(s => s.SourceKey.EndsWith(suffix)));
#pragma warning restore CA1310
        }
        else
        {
            throw new LibraryException(400, "Give a page slug, a document ID or a file path.");
        }

        var pages = await start.OrderBy(p => p.Kind).ThenBy(p => p.Title).Take(50).ToListAsync(ct);
        var ids = pages.Select(p => p.Id).ToArray();
        // "implements" goes from code to functional; both directions are followed, plus one hop through plain links
        // so a file's module page leads to the features its components implement.
        var around = await db.WikiLinks.AsNoTracking()
            .Where(l => (ids.Contains(l.FromPageId) && visible.Any(p => p.Id == l.ToPageId)) || (ids.Contains(l.ToPageId) && visible.Any(p => p.Id == l.FromPageId)))
            .Select(l => new { l.Kind, l.Note, From = new { l.FromPage!.Id, l.FromPage.Slug, l.FromPage.Title, l.FromPage.Kind },
                To = new { l.ToPage!.Id, l.ToPage.Slug, l.ToPage.Title, l.ToPage.Kind } }).ToListAsync(ct);
        var others = around.Select(l => ids.Contains(l.From.Id) ? (Page: l.To, l.Kind, l.Note) : (Page: l.From, l.Kind, l.Note))
            .Where(o => !ids.Contains(o.Page.Id)).ToList();
        var functional = others.Where(o => WikiPageKinds.Functional.Contains(o.Page.Kind));
        var code = others.Where(o => WikiPageKinds.Technical.Contains(o.Page.Kind));
        return new(pages.Select(p => new WikiPageSummary(p.Slug, p.Title, p.Kind, p.Summary, p.Stale, p.UpdatedUtc)).ToList(),
            Order(functional.Select(o => new WikiLinkDto(o.Page.Slug, o.Page.Title, o.Page.Kind, o.Kind, o.Note)).Distinct().ToList()),
            Order(code.Select(o => new WikiLinkDto(o.Page.Slug, o.Page.Title, o.Page.Kind, o.Kind, o.Note)).Distinct().ToList()),
            await SourcesAsync(ids, ct));
    }

    /// <summary>Document that currently holds a repository file, so a repo: citation can be opened.</summary>
    public async Task<int> ResolveRepositoryFileAsync(string userId, int repositoryId, string path, CancellationToken ct)
    {
        var documents = await new LibraryAccess(db).ReadableDocumentsAsync(userId, ct: ct);
        var id = await documents.Where(d => d.RepositoryId == repositoryId && d.SourcePath == path).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        return id ?? throw new LibraryException(404, "This file is no longer in the synchronised repository.");
    }

    private async Task<IReadOnlyList<WikiSourceDto>> SourcesAsync(int[] pageIds, CancellationToken ct) =>
        (await db.WikiPageSources.AsNoTracking().Where(s => pageIds.Contains(s.PageId) && s.DocumentId != null)
            .Select(s => new WikiSourceDto(s.DocumentId, s.Document!.Title, s.Document.RepositoryId, s.Document.SourcePath)).ToListAsync(ct))
        .Distinct().OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Take(200).ToList();

    private async Task<WikiDto> DescribeAsync(Wiki wiki, LibrarySnapshot snapshot, CancellationToken ct)
    {
        var last = await db.WikiRuns.AsNoTracking().Where(r => r.WikiId == wiki.Id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        var refreshed = await db.WikiRuns.AsNoTracking().Where(r => r.WikiId == wiki.Id && r.Status == WikiRunStatus.Completed)
            .OrderByDescending(r => r.Id).Select(r => r.FinishedUtc).FirstOrDefaultAsync(ct);
        return new(wiki.Id, wiki.FolderId, snapshot.Path(wiki.FolderId), wiki.Language, wiki.Guidelines,
            await VisiblePages(snapshot, wiki.Id).CountAsync(ct), snapshot.CanWrite(wiki.FolderId),
            snapshot.Role(wiki.FolderId) == "owner", last is null ? null : WikiRunDto.From(last), refreshed);
    }

    internal IQueryable<WikiPage> Containing(IQueryable<WikiPage> pages, string text, bool content)
    {
        if (!db.Database.IsRelational())
        {
            var lower = text.ToLowerInvariant();
            return pages.Where(p => p.Title.ToLower().Contains(lower) || p.Summary.ToLower().Contains(lower) ||
                                    string.Join("\n", p.Aliases).ToLower().Contains(lower) || (content && p.Content.ToLower().Contains(lower)));
        }
        var like = "%" + RagService.EscapeLike(text) + "%";
        return pages.Where(p => EF.Functions.ILike(p.Title, like, @"\") || EF.Functions.ILike(p.Summary, like, @"\") ||
                                p.Aliases.Any(a => EF.Functions.ILike(a, like, @"\")) || (content && EF.Functions.ILike(p.Content, like, @"\")));
    }

    private static IReadOnlyList<WikiLinkDto> Order(List<WikiLinkDto> links) =>
        links.OrderBy(l => l.Link != WikiLinkKinds.Implements).ThenBy(l => l.Kind).ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Language(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim().Length <= 20 ? language.Trim() : throw new LibraryException(400, "Enter a language of at most 20 characters.");

    private static string? Guidelines(string? guidelines) =>
        string.IsNullOrWhiteSpace(guidelines) ? null : guidelines.Trim().Length <= 8000 ? guidelines.Trim() : throw new LibraryException(400, "Enter guidelines of at most 8,000 characters.");
}
