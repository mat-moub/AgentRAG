using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AgentRag.Api.Services;

/// <summary>Outcome of a run, stored on it and shown in the interface.</summary>
public sealed class WikiRunStats
{
    public int SourcesAdded { get; set; }
    public int SourcesChanged { get; set; }
    public int SourcesRemoved { get; set; }
    /// <summary>Documents left out because their subfolder is shared with a different audience.</summary>
    public int SourcesExcluded { get; set; }
    public int SourcesExtracted { get; set; }
    public int SourcesFailed { get; set; }
    public int PagesCreated { get; set; }
    public int PagesUpdated { get; set; }
    public int PagesRemoved { get; set; }
    public int PagesFailed { get; set; }
    public int Pages { get; set; }
    public int Links { get; set; }
    /// <summary>Links from code pages to the functional pages they implement.</summary>
    public int ImplementationLinks { get; set; }
    /// <summary>Functional pages searched in the code during this run.</summary>
    public int PagesLinkedToCode { get; set; }
    /// <summary>Functional pages for which no implementing code was found.</summary>
    public List<string> WithoutImplementation { get; set; } = [];
    public int BrokenLinks { get; set; }
    public List<string> OrphanPages { get; set; } = [];
    public int UnresolvedCitations { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>
/// Builds a wiki in stages: what changed, extraction per source, resolution into pages, writing, links between
/// functional documentation and code, then indexing and checks. Every stage saves what it finished, so an
/// interrupted run continues where it stopped.
/// </summary>
public class WikiBuilder(AppDbContext db, WikiModel model, EmbeddingService embeddings, IServiceScopeFactory scopes,
    IOptions<WikiOptions> options, ILogger<WikiBuilder> logger)
{
    public const string OverviewSlug = "index";
    private const int MaxFactsPerPage = 60;
    private const int MaxExamples = 30;
    private static readonly string[] ItemKinds =
        [WikiPageKinds.Feature, WikiPageKinds.Rule, WikiPageKinds.Concept, WikiPageKinds.Component, WikiPageKinds.DataEntity, WikiPageKinds.Api];
    private static readonly string[] KindOrder =
        [WikiPageKinds.Feature, WikiPageKinds.Module, WikiPageKinds.Rule, WikiPageKinds.Api, WikiPageKinds.Concept,
            WikiPageKinds.Component, WikiPageKinds.DataEntity, WikiPageKinds.Source
        ];

    private WikiOptions Options => options.Value;
    private int Parallelism => Math.Clamp(Options.MaxParallelism, 1, 16);

    private sealed record Run(WikiRun Row, Wiki Wiki, WikiRunStats Stats);

    /// <param name="Modules">Code modules by the key of their manifest row.</param>
    private sealed record Scope(Dictionary<int, ScopeDocument> Documents, Dictionary<string, CodeModule> Modules, Dictionary<int, string> Repositories);

    /// <summary>A page to have at the end of the run, with everything it is written from.</summary>
    private sealed class PagePlan
    {
        public required string Family { get; init; }
        public required string Title { get; set; }
        public string Slug { get; set; } = "";
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Kinds { get; } = [];
        public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Descriptions { get; } = [];
        public List<PageFact> Facts { get; } = [];
        public Dictionary<string, int?> Sources { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Related { get; } = new(StringComparer.Ordinal);
        public HashSet<PagePlan> Modules { get; } = [];
        public List<string> DependsOn { get; } = [];
        /// <summary>Items extracted from a document, listed on its source page.</summary>
        public List<PagePlan> Items { get; } = [];
        /// <summary>Set for pages composed without the model.</summary>
        public string? FixedSummary { get; set; }
        public WikiPage? Page { get; set; }
        public bool Written { get; set; }
        public string Kind => Kinds.Count == 0 ? WikiPageKinds.Concept : Kinds.MaxBy(k => k.Value).Key;
    }

    public async Task RunAsync(int runId, CancellationToken ct)
    {
        var row = await db.WikiRuns.SingleAsync(r => r.Id == runId, ct);
        var wiki = await db.Wikis.SingleAsync(w => w.Id == row.WikiId, ct);
        var run = new Run(row, wiki, new WikiRunStats());
        // A rebuild forgets what was extracted, once: a run resumed after a restart keeps what it already redid.
        var reset = row.Mode == WikiRunModes.Full && row.StartedUtc is null;
        (row.Status, row.StartedUtc, row.Error) = (WikiRunStatus.Running, row.StartedUtc ?? DateTime.UtcNow, null);
        await StageAsync(run, "listing", 0, 0, ct);
        if (reset)
        {
            db.WikiSources.RemoveRange(await db.WikiSources.Where(s => s.WikiId == wiki.Id).ToListAsync(ct));
            foreach (var page in await db.WikiPages.Where(p => p.WikiId == wiki.Id).ToListAsync(ct)) page.InputHash = "";
            await db.SaveChangesAsync(ct);
        }

        var scope = await ScopeAsync(run, ct);
        if (row.Mode != WikiRunModes.Lint)
        {
            await DeltaAsync(run, scope, ct);
            await ExtractAsync(run, scope, ct);
            var plans = await ResolveAsync(run, scope, ct);
            await WriteAsync(run, plans, ct);
            await LinkStructureAsync(run, plans, ct);
            await LinkImplementationAsync(run, scope, plans, ct);
        }
        await IndexAsync(run, scope, ct);

        (run.Stats.InputTokens, run.Stats.OutputTokens) = (model.InputTokens, model.OutputTokens);
        row.Stats = JsonSerializer.Serialize(run.Stats, JsonSerializerOptions.Web);
        await StageAsync(run, "completed", 0, 0, ct);
        logger.LogInformation("Wiki {WikiId} run {RunId} completed: {Pages} pages, {Links} links", wiki.Id, runId, run.Stats.Pages, run.Stats.Links);
    }

    // Model calls happen outside any transaction. Write access is checked again before each save,
    // so a writer whose access was revoked cannot keep publishing pages from a long run.
    private async Task<LibrarySnapshot> StageAsync(Run run, string stage, int completed, int total, CancellationToken ct)
    {
        var snapshot = await new LibraryAccess(db).LoadAsync(run.Row.RequestedById, ct);
        snapshot.Require(run.Wiki.FolderId, write: true);
        (run.Row.Stage, run.Row.Completed, run.Row.Total) = (stage, completed, total);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    /// <summary>Records progress inside a stage with one UPDATE; the access check is left to <see cref="StageAsync"/> at each batch.</summary>
    private async Task ProgressAsync(Run run, string stage, int completed, int total, CancellationToken ct)
    {
        (run.Row.Stage, run.Row.Completed, run.Row.Total) = (stage, completed, total);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Scope> ScopeAsync(Run run, CancellationToken ct)
    {
        var snapshot = await new LibraryAccess(db).LoadAsync(run.Row.RequestedById, ct);
        var listed = await WikiService.ScopeDocuments(db, snapshot, run.Wiki.FolderId)
            .Select(d => new ScopeDocument(d.Id, d.Title, d.RepositoryId, d.SourcePath, d.SourceSha)).ToListAsync(ct);
        var folders = snapshot.Descendants(run.Wiki.FolderId).ToArray();
        run.Stats.SourcesExcluded = await db.Documents.CountAsync(d => d.FolderId != null && folders.Contains(d.FolderId.Value), ct) - listed.Count;
        var documents = listed.GroupBy(WikiDelta.Key, StringComparer.Ordinal).Select(g => g.MaxBy(d => d.Id)!).ToDictionary(d => d.Id);

        var code = documents.Values.Where(d => WikiDelta.Type(d) == WikiDelta.Code).ToList();
        var codeIds = code.Select(d => d.Id).ToArray();
        var lengths = await db.DocumentTexts.AsNoTracking().Where(t => codeIds.Contains(t.DocumentId))
            .Select(t => new { t.DocumentId, t.Content.Length }).ToDictionaryAsync(t => t.DocumentId, t => t.Length, ct);
        var modules = new Dictionary<string, CodeModule>(StringComparer.Ordinal);
        foreach (var repository in code.GroupBy(d => d.RepositoryId!.Value))
        {
            foreach (var module in CodeMap.Modules(repository.Select(d =>
                         new CodeFile(d.SourcePath!, d.Id, WikiDelta.Fingerprint(d), lengths.GetValueOrDefault(d.Id))), Options.ModuleCharacters))
                modules[$"module:{repository.Key}:{module.Key}"] = module;
        }

        var repositoryIds = documents.Values.Where(d => d.RepositoryId is not null).Select(d => d.RepositoryId!.Value).Distinct().ToArray();
        var repositories = (await db.Repositories.AsNoTracking().Where(r => repositoryIds.Contains(r.Id)).ToListAsync(ct))
            .ToDictionary(r => r.Id, r => RepositoryAddress.From(r).DisplayName);
        return new(documents, modules, repositories);
    }

    private async Task DeltaAsync(Run run, Scope scope, CancellationToken ct)
    {
        var manifest = await db.WikiSources.Where(s => s.WikiId == run.Wiki.Id).ToListAsync(ct);
        var delta = WikiDelta.Compute(scope.Documents.Values, manifest.Where(s => s.Type != WikiDelta.Module));
        (run.Stats.SourcesAdded, run.Stats.SourcesChanged, run.Stats.SourcesRemoved) = (delta.Added.Count, delta.Changed.Count, delta.Removed.Count);

        // Key of each touched source and the document that now holds it; null when it is gone.
        var touched = new Dictionary<string, (int? DocumentId, bool Stale)>(StringComparer.Ordinal);
        foreach (var source in delta.Removed)
        {
            db.WikiSources.Remove(source);
            touched[source.SourceKey] = (null, true);
        }
        foreach (var (source, document) in delta.Changed)
        {
            (source.DocumentId, source.Fingerprint, source.Type, source.Extraction, source.ProcessedUtc) =
                (document.Id, WikiDelta.Fingerprint(document), WikiDelta.Type(document), null, DateTime.UtcNow);
            touched[source.SourceKey] = (document.Id, true);
        }
        foreach (var (source, document) in delta.Relinked)
        {
            source.DocumentId = document.Id;
            touched[source.SourceKey] = (document.Id, false);
        }
        foreach (var document in delta.Added)
        {
            db.WikiSources.Add(new WikiSource
            {
                WikiId = run.Wiki.Id, SourceKey = WikiDelta.Key(document), DocumentId = document.Id,
                Fingerprint = WikiDelta.Fingerprint(document), Type = WikiDelta.Type(document)
            });
        }

        // Pages written from a touched source follow it to its new document and are marked until rewritten.
        foreach (var keys in touched.Keys.Chunk(1000))
        {
            foreach (var source in await db.WikiPageSources.Include(s => s.Page)
                         .Where(s => s.Page!.WikiId == run.Wiki.Id && keys.Contains(s.SourceKey)).ToListAsync(ct))
            {
                var (documentId, stale) = touched[source.SourceKey];
                if (documentId is null) db.WikiPageSources.Remove(source);
                else source.DocumentId = documentId;
                if (stale) source.Page!.Stale = true;
            }
        }

        // Module rows are derived from the code files: a module is extracted again when any of its files changed.
        var moduleRows = manifest.Where(s => s.Type == WikiDelta.Module).ToDictionary(s => s.SourceKey, StringComparer.Ordinal);
        foreach (var (key, module) in scope.Modules)
        {
            var fingerprint = CodeMap.Fingerprint(module);
            if (!moduleRows.TryGetValue(key, out var source))
                db.WikiSources.Add(new WikiSource { WikiId = run.Wiki.Id, SourceKey = key, Type = WikiDelta.Module, Fingerprint = fingerprint });
            else if (source.Fingerprint != fingerprint)
                (source.Fingerprint, source.Extraction, source.ProcessedUtc) = (fingerprint, null, DateTime.UtcNow);
        }
        db.WikiSources.RemoveRange(moduleRows.Values.Where(s => !scope.Modules.ContainsKey(s.SourceKey)));
        await StageAsync(run, "listing", 0, 0, ct);
    }

    private async Task ExtractAsync(Run run, Scope scope, CancellationToken ct)
    {
        var pending = await db.WikiSources.Where(s => s.WikiId == run.Wiki.Id && s.Extraction == null && s.Type != WikiDelta.Code)
            .OrderBy(s => s.Id).ToListAsync(ct);
        var done = 0;
        Exception? failure = null;
        foreach (var batch in pending.Chunk(Parallelism))
        {
            await StageAsync(run, "extracting", done, pending.Count, ct);
            // Inputs are read one after the other: the context is not safe for concurrent use. Only model calls run in parallel.
            var jobs = new List<(WikiSource Source, Func<Task<SourceExtraction>> Extract)>();
            foreach (var source in batch)
            {
                if (source.Type == WikiDelta.Module)
                {
                    if (!scope.Modules.TryGetValue(source.SourceKey, out var module)) continue;
                    var ids = module.Files.Select(f => f.DocumentId).ToArray();
                    var texts = await db.DocumentTexts.AsNoTracking().Where(t => ids.Contains(t.DocumentId))
                        .ToDictionaryAsync(t => t.DocumentId, t => t.Content, ct);
                    var digest = CodeMap.Digest(module, texts, Options.ModulePromptCharacters);
                    var repository = scope.Repositories.GetValueOrDefault(RepositoryOf(source.SourceKey), "repository");
                    jobs.Add((source, () => model.ExtractModuleAsync(repository, module.Name, digest, run.Wiki.Language, run.Wiki.Guidelines, ct)));
                }
                else if (source.DocumentId is { } id && scope.Documents.TryGetValue(id, out var document))
                {
                    var windows = await WindowsAsync(id, ct);
                    jobs.Add((source, () => ExtractDocumentAsync(run.Wiki, document.Title, windows, ct)));
                }
            }
            var results = jobs.Select(async job =>
            {
                try { return (job.Source, Extraction: (SourceExtraction?)await job.Extract(), Error: (Exception?)null); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return (job.Source, null, ex); }
            }).ToList();
            // Results are taken as they complete, so the progress moves with every source and not once per batch.
            await foreach (var finished in Task.WhenEach(results).WithCancellation(ct))
            {
                var (source, extraction, error) = await finished;
                done++;
                if (extraction is null)
                {
                    // The source stays pending and is tried again at the next refresh.
                    run.Stats.SourcesFailed++;
                    failure ??= error;
                    logger.LogWarning(error, "Wiki {WikiId}: extraction failed for {Source}", run.Wiki.Id, source.SourceKey);
                }
                else
                {
                    (source.Extraction, source.ProcessedUtc) = (JsonSerializer.Serialize(extraction, JsonSerializerOptions.Web), DateTime.UtcNow);
                    run.Stats.SourcesExtracted++;
                }
                await ProgressAsync(run, "extracting", done, pending.Count, ct);
            }
            // Nothing works at all: the model is unreachable or misconfigured, so stop instead of failing every source.
            if (failure is not null && run.Stats.SourcesExtracted == 0 && done >= Math.Min(pending.Count, Parallelism * 2))
                throw new InvalidOperationException("The model could not extract any source.", failure);
        }
        await StageAsync(run, "extracting", pending.Count, pending.Count, ct);
    }

    private async Task<SourceExtraction> ExtractDocumentAsync(Wiki wiki, string title, IReadOnlyList<string> windows, CancellationToken ct)
    {
        var parts = new List<SourceExtraction>();
        foreach (var window in windows) parts.Add(await model.ExtractDocumentAsync(title, window, wiki.Language, wiki.Guidelines, ct));
        if (parts.Count == 1) return parts[0];
        var summary = string.Join(' ', parts.Select(p => p.Summary).Where(s => !string.IsNullOrWhiteSpace(s)).Take(3));
        return new(null, summary, null, null, parts.SelectMany(p => p.Items ?? []).ToArray());
    }

    /// <summary>The document as numbered passages, cut into windows the model reads one at a time.</summary>
    private async Task<IReadOnlyList<string>> WindowsAsync(int documentId, CancellationToken ct)
    {
        var overlap = await db.Documents.Where(d => d.Id == documentId).Select(d => d.PassageOverlap).SingleAsync(ct);
        var passages = await db.Chunks.AsNoTracking().Where(c => c.DocumentId == documentId).OrderBy(c => c.Index).Select(c => c.Content).ToListAsync(ct);
        var windows = new List<string>();
        var window = new StringBuilder();
        for (var i = 0; i < passages.Count; i++)
        {
            var text = i == 0 ? passages[i] : passages[i][Math.Min(overlap, passages[i].Length)..];
            window.Append("[passage ").Append(i + 1).AppendLine("]").AppendLine(text).AppendLine();
            if (window.Length < Options.ExtractWindowCharacters) continue;
            windows.Add(window.ToString());
            window.Clear();
        }
        if (window.Length > 0) windows.Add(window.ToString());
        return windows;
    }

    private async Task<List<PagePlan>> ResolveAsync(Run run, Scope scope, CancellationToken ct)
    {
        await StageAsync(run, "resolving", 0, 0, ct);
        var sources = await db.WikiSources.AsNoTracking().Where(s => s.WikiId == run.Wiki.Id && s.Extraction != null && s.Type != WikiDelta.Code)
            .OrderBy(s => s.Id).ToListAsync(ct);
        var index = new Dictionary<(string Family, string Key), PagePlan>();
        var fixedPlans = new List<PagePlan>();

        foreach (var source in sources)
        {
            var extraction = JsonSerializer.Deserialize<SourceExtraction>(source.Extraction!, JsonSerializerOptions.Web);
            if (extraction is null) continue;
            if (source.Type == WikiDelta.Module)
            {
                if (!scope.Modules.TryGetValue(source.SourceKey, out var module)) continue;
                var repositoryId = RepositoryOf(source.SourceKey);
                var files = module.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
                var repositoryName = scope.Repositories.GetValueOrDefault(repositoryId, "repository");
                var plan = new PagePlan
                {
                    Family = "module", Title = WikiText.Truncate(string.IsNullOrWhiteSpace(extraction.Title) ? module.Name : extraction.Title.Trim(), 200),
                    Slug = "module-" + WikiText.Slug($"{repositoryName.Split('/')[^1]} {module.Name}")
                };
                plan.Kinds[WikiPageKinds.Module] = 1;
                plan.Aliases.Add($"{repositoryName}: {module.Name}");
                if (!string.IsNullOrWhiteSpace(extraction.Summary)) plan.Descriptions.Add(extraction.Summary.Trim());
                plan.DependsOn.AddRange(extraction.DependsOn ?? []);
                // Every file of the module is a source of its page, so any file path leads to the page.
                foreach (var file in module.Files) plan.Sources[$"repo:{repositoryId}:{file.Path}"] = file.DocumentId;
                foreach (var fact in extraction.Facts ?? []) AddFact(plan, fact, Cite);
                fixedPlans.Add(plan);
                foreach (var item in extraction.Items ?? [])
                    if (Candidate(item, WikiPageKinds.Component, Cite) is { } candidate) candidate.Modules.Add(plan);

                (string? Citation, string? Key, int? DocumentId) Cite(string? reference)
                {
                    var (path, line) = CodeReference(reference);
                    var file = path is null ? null : files.GetValueOrDefault(path) ?? files.Values.FirstOrDefault(f => f.Path.EndsWith("/" + path, StringComparison.Ordinal));
                    return file is null ? (null, null, null) : (WikiText.CodeCitation(repositoryId, file.Path, line), $"repo:{repositoryId}:{file.Path}", file.DocumentId);
                }
            }
            else if (source.DocumentId is { } id && scope.Documents.TryGetValue(id, out var document))
            {
                var plan = new PagePlan { Family = "source", Title = WikiText.Truncate(document.Title, 200), FixedSummary = extraction.Summary?.Trim() ?? "" };
                plan.Slug = "source-" + WikiText.Slug(document.Title, 80);
                if (fixedPlans.Any(p => p.Slug == plan.Slug)) plan.Slug += "-" + id.ToString(CultureInfo.InvariantCulture);
                plan.Kinds[WikiPageKinds.Source] = 1;
                plan.Sources[source.SourceKey] = id;
                fixedPlans.Add(plan);
                foreach (var item in extraction.Items ?? [])
                    if (Candidate(item, WikiPageKinds.Concept, Cite) is { } candidate && !plan.Items.Contains(candidate)) plan.Items.Add(candidate);

                // A repository file is cited by path, which survives its next synchronisation; an uploaded document by passage.
                (string? Citation, string? Key, int? DocumentId) Cite(string? reference) => (document is { RepositoryId: { } repository, SourcePath: { } path }
                    ? WikiText.CodeCitation(repository, path, null)
                    : WikiText.DocumentCitation(document.Title, id, int.TryParse(reference?.Trim().TrimStart('p', '.', ' '), CultureInfo.InvariantCulture, out var passage) && passage > 0 ? passage : null),
                    source.SourceKey, id);
            }
        }

        var items = index.Values.Distinct().ToList();
        var pages = await db.WikiPages.Where(p => p.WikiId == run.Wiki.Id).ToListAsync(ct);
        Claim(items, pages);
        await MergeDuplicatesAsync(run, items, index, ct);
        items = index.Values.Distinct().OrderByDescending(p => p.Page is not null).ThenByDescending(p => p.Facts.Count)
            .Take(Math.Max(Options.MaxPages - fixedPlans.Count, 0)).ToList();
        Claim(items, pages);

        // Existing pages keep their slug; new ones get a free one.
        var taken = new HashSet<string>(StringComparer.Ordinal) { OverviewSlug };
        foreach (var plan in fixedPlans)
        {
            while (!taken.Add(plan.Slug)) plan.Slug += "-2";
            plan.Page = pages.FirstOrDefault(p => p.Slug == plan.Slug && p.Kind == plan.Kind);
        }
        foreach (var plan in items.Where(p => p.Page is not null)) taken.Add(plan.Slug = plan.Page!.Slug);
        foreach (var plan in items.Where(p => p.Page is null))
        {
            var slug = WikiText.Slug(plan.Title);
            if (!taken.Add(slug) && !taken.Add(slug = $"{slug}-{plan.Kind}"))
                for (var n = 2; !taken.Add(slug = $"{WikiText.Slug(plan.Title)}-{n}"); n++) { }
            plan.Slug = slug;
        }
        return [.. fixedPlans.Where(p => p.Family == "module" || items.Count > 0 || p.FixedSummary is { Length: > 0 }), .. items];

        PagePlan? Candidate(ExtractedItem item, string defaultKind, Func<string?, (string? Citation, string? Key, int? DocumentId)> cite)
        {
            var name = item.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 150) return null;
            var kind = ItemKinds.FirstOrDefault(k => string.Equals(k, item.Kind?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? defaultKind;
            // A functional notion and the code that implements it stay two pages, even under the same name:
            // the link between them is what the wiki is for.
            var family = WikiPageKinds.Functional.Contains(kind) ? "functional" : "technical";
            var aliases = (item.Aliases ?? []).Select(a => a?.Trim() ?? "").Where(a => a.Length is >= 2 and <= 100 && !a.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var keys = aliases.Select(a => WikiText.Slug(a)).Prepend(WikiText.Slug(name)).Where(k => k != "page").Distinct().ToList();
            if (keys.Count == 0) return null;
            var matches = keys.Select(k => index.GetValueOrDefault((family, k))).Where(p => p is not null).Distinct().ToList();
            var plan = matches.FirstOrDefault() ?? new PagePlan { Family = family, Title = WikiText.Truncate(name, 200) };
            foreach (var other in matches.Skip(1)) Merge(plan, other!, index);
            foreach (var key in keys)
            {
                plan.Keys.Add(key);
                index[(family, key)] = plan;
            }
            plan.Kinds[kind] = plan.Kinds.GetValueOrDefault(kind) + 1;
            foreach (var alias in aliases) plan.Aliases.Add(alias);
            if (!string.IsNullOrWhiteSpace(item.Description)) plan.Descriptions.Add(item.Description.Trim());
            foreach (var fact in item.Facts ?? []) AddFact(plan, new ExtractedFact(fact.Text, fact.Ref), cite);
            if (plan.Sources.Count == 0 && cite(null) is { Key: { } sourceKey } whole) plan.Sources[sourceKey] = whole.DocumentId;
            foreach (var related in item.Related ?? [])
                if (!string.IsNullOrWhiteSpace(related)) plan.Related.Add(WikiText.Slug(related));
            return plan;
        }

        static void AddFact(PagePlan plan, ExtractedFact fact, Func<string?, (string? Citation, string? Key, int? DocumentId)> cite)
        {
            if (string.IsNullOrWhiteSpace(fact.Text)) return;
            var (citation, key, documentId) = cite(fact.Ref);
            plan.Facts.Add(new(fact.Text.Trim(), citation));
            if (key is not null) plan.Sources[key] = documentId;
        }
    }

    /// <summary>Gives each plan the existing page that already covers its subject, so the page keeps its slug.</summary>
    private static void Claim(List<PagePlan> plans, List<WikiPage> pages)
    {
        var claimed = plans.Where(p => p.Page is not null).Select(p => p.Page!).ToHashSet();
        foreach (var plan in plans.Where(p => p.Page is null))
        {
            var functional = plan.Family == "functional";
            plan.Page = pages.FirstOrDefault(p => !claimed.Contains(p) && p.Kind is not (WikiPageKinds.Overview or WikiPageKinds.Source or WikiPageKinds.Module) &&
                WikiPageKinds.Functional.Contains(p.Kind) == functional &&
                (plan.Keys.Contains(p.Slug) || plan.Keys.Contains(WikiText.Slug(p.Title)) || p.Aliases.Any(a => plan.Keys.Contains(WikiText.Slug(a)))));
            if (plan.Page is not null) claimed.Add(plan.Page);
        }
    }

    private static void Merge(PagePlan into, PagePlan other, Dictionary<(string Family, string Key), PagePlan> index)
    {
        if (ReferenceEquals(into, other)) return;
        foreach (var key in other.Keys)
        {
            into.Keys.Add(key);
            index[(into.Family, key)] = into;
        }
        foreach (var (kind, votes) in other.Kinds) into.Kinds[kind] = into.Kinds.GetValueOrDefault(kind) + votes;
        into.Aliases.Add(other.Title);
        into.Aliases.UnionWith(other.Aliases);
        into.Aliases.Remove(into.Title);
        into.Descriptions.AddRange(other.Descriptions);
        into.Facts.AddRange(other.Facts);
        foreach (var (key, document) in other.Sources) into.Sources[key] = document;
        into.Related.UnionWith(other.Related);
        into.Modules.UnionWith(other.Modules);
        into.Page ??= other.Page;
    }

    /// <summary>Asks the model which new titles name a subject that already has a page or another new title.</summary>
    private async Task MergeDuplicatesAsync(Run run, List<PagePlan> items, Dictionary<(string Family, string Key), PagePlan> index, CancellationToken ct)
    {
        foreach (var family in items.GroupBy(p => p.Family))
        {
            if (family.All(p => p.Page is not null) || family.Count() < 2) continue;
            // New titles first: they are the ones that may duplicate something.
            foreach (var names in family.OrderBy(p => p.Page is not null).Select(p => p.Title).Distinct().Chunk(400))
            {
                string[][] groups;
                try { groups = await model.FindDuplicatesAsync(names, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deduplication is an improvement, not a requirement: duplicates can still be merged at a later refresh.
                    logger.LogWarning(ex, "Wiki {WikiId}: duplicate detection failed", run.Wiki.Id);
                    continue;
                }
                foreach (var group in groups)
                {
                    var plans = (group ?? []).Select(n => index.GetValueOrDefault((family.Key, WikiText.Slug(n ?? "")))).Where(p => p is not null).Distinct().ToList();
                    if (plans.Count < 2) continue;
                    var keep = plans.OrderByDescending(p => p!.Page is not null).ThenByDescending(p => p!.Facts.Count).First()!;
                    foreach (var other in plans.Where(p => !ReferenceEquals(p, keep))) Merge(keep, other!, index);
                }
            }
        }
    }

    private async Task WriteAsync(Run run, List<PagePlan> plans, CancellationToken ct)
    {
        var planned = plans.Where(p => p.Page is not null).Select(p => p.Page!).ToHashSet();
        var obsolete = await db.WikiPages.Where(p => p.WikiId == run.Wiki.Id && p.Kind != WikiPageKinds.Overview).ToListAsync(ct);
        obsolete.RemoveAll(planned.Contains);
        db.WikiPages.RemoveRange(obsolete);
        run.Stats.PagesRemoved = obsolete.Count;

        var bySlug = plans.ToDictionary(p => p.Slug, StringComparer.Ordinal);
        var byName = NameIndex(plans);
        var bySource = plans.SelectMany(p => p.Sources.Keys.Select(k => (Key: k, Plan: p))).ToLookup(x => x.Key, x => x.Plan, StringComparer.Ordinal);
        var work = new List<(PagePlan Plan, string Hash, PageBrief? Brief)>();
        foreach (var plan in plans)
        {
            if (plan.FixedSummary is { } summary)
            {
                var content = SourceContent(plan);
                var hash = WikiText.Hash($"{plan.Title}\n{summary}\n{content}");
                if (plan.Page?.InputHash != hash) work.Add((plan, hash, null));
                continue;
            }
            var descriptions = plan.Descriptions.Distinct().Take(8).ToList();
            var facts = plan.Facts.DistinctBy(f => (f.Text, f.Citation)).Take(MaxFactsPerPage).ToList();
            var input = WikiText.Hash(JsonSerializer.Serialize(new
            {
                plan.Title, plan.Kind, Aliases = plan.Aliases.Order(StringComparer.Ordinal), descriptions, facts, run.Wiki.Language, run.Wiki.Guidelines
            }));
            if (plan.Page?.InputHash == input) continue;
            // Link candidates: named relations first, then the module, then pages written from the same sources.
            var candidates = plan.Related.Select(k => byName.GetValueOrDefault(k)).Concat(plan.Modules)
                .Concat(plan.Sources.Keys.Take(20).SelectMany(k => bySource[k]))
                .Where(p => p is not null && !ReferenceEquals(p, plan)).Distinct().Take(40)
                .Select(p => new PageReference(p!.Slug, p.Title, p.Kind, WikiText.Truncate(p.Page?.Summary is { Length: > 0 } s ? s : p.Descriptions.FirstOrDefault() ?? p.FixedSummary, 200)))
                .ToList();
            work.Add((plan, input, new(plan.Title, plan.Kind, plan.Aliases.Order(StringComparer.Ordinal).ToList(), descriptions, facts, candidates,
                plan.Page?.Content, run.Wiki.Language, run.Wiki.Guidelines)));
        }

        var done = 0;
        Exception? failure = null;
        foreach (var batch in work.Chunk(Parallelism))
        {
            await StageAsync(run, "writing", done, work.Count, ct);
            var results = batch.Select(async item =>
            {
                if (item.Brief is null) return (item.Plan, item.Hash, Written: (WrittenPage?)new WrittenPage(item.Plan.FixedSummary!, SourceContent(item.Plan)), Error: (Exception?)null);
                try { return (item.Plan, item.Hash, Written: (WrittenPage?)await model.WritePageAsync(item.Brief, ct), Error: (Exception?)null); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return (item.Plan, item.Hash, Written: (WrittenPage?)null, Error: (Exception?)ex); }
            }).ToList();
            await foreach (var finished in Task.WhenEach(results).WithCancellation(ct))
            {
                var (plan, hash, written, error) = await finished;
                done++;
                if (written is null)
                {
                    run.Stats.PagesFailed++;
                    failure ??= error;
                    logger.LogWarning(error, "Wiki {WikiId}: page {Slug} could not be written", run.Wiki.Id, plan.Slug);
                    await ProgressAsync(run, "writing", done, work.Count, ct);
                    continue;
                }
                if (plan.Page is null)
                {
                    db.WikiPages.Add(plan.Page = new WikiPage { WikiId = run.Wiki.Id, Slug = plan.Slug });
                    run.Stats.PagesCreated++;
                }
                else
                {
                    run.Stats.PagesUpdated++;
                }

                var page = plan.Page;
                (page.Title, page.Kind, page.Aliases) = (plan.Title, plan.Kind, plan.Aliases.Order(StringComparer.Ordinal).Take(20).ToArray());
                page.Summary = WikiText.Truncate(written.Summary?.Trim(), 1000);
                // Only links to pages that exist are kept as links; the others stay as plain text.
                page.Content = WikiText.ReplaceLinks(written.Content?.Trim() ?? "", (slug, label) =>
                    (bySlug.GetValueOrDefault(slug) ?? byName.GetValueOrDefault(slug)) is { } target ? $"[[{target.Slug}|{label}]]" : null);
                (page.InputHash, page.Stale, page.UpdatedUtc, page.Embedding) = (hash, false, DateTime.UtcNow, null);
                plan.Written = true;
                await ProgressAsync(run, "writing", done, work.Count, ct);
            }
            if (failure is not null && run.Stats.PagesCreated + run.Stats.PagesUpdated == 0 && done >= Math.Min(work.Count, Parallelism * 2))
                throw new InvalidOperationException("The model could not write any page.", failure);
        }
        await StageAsync(run, "writing", work.Count, work.Count, ct);

        // Provenance is brought in line for every page, rewritten or not: it decides who may read the page.
        var existing = (await db.WikiPageSources.Where(s => s.Page!.WikiId == run.Wiki.Id).ToListAsync(ct)).ToLookup(s => s.PageId);
        foreach (var plan in plans.Where(p => p.Page is not null))
        {
            var current = existing[plan.Page!.Id].ToDictionary(s => s.SourceKey, StringComparer.Ordinal);
            foreach (var (key, documentId) in plan.Sources)
                if (current.Remove(key, out var source)) source.DocumentId = documentId;
                else db.WikiPageSources.Add(new WikiPageSource { PageId = plan.Page.Id, SourceKey = key, DocumentId = documentId });
            db.WikiPageSources.RemoveRange(current.Values);
        }
        // What is still marked was not rewritten because its content did not need to change, unless a source failed.
        if (run.Stats.SourcesFailed == 0)
            foreach (var plan in plans.Where(p => p.Page is { Stale: true })) plan.Page!.Stale = false;
        await db.SaveChangesAsync(ct);
    }

    private static string SourceContent(PagePlan plan)
    {
        var content = new StringBuilder(plan.FixedSummary);
        if (plan.Items.Count == 0) return content.ToString();
        content.AppendLine().AppendLine().AppendLine("## Subjects");
        foreach (var item in plan.Items.OrderBy(i => Array.IndexOf(KindOrder, i.Kind)).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
            content.Append("- [[").Append(item.Slug).Append('|').Append(item.Title.Replace("]", ")").Replace("|", "/")).Append("]] (").Append(item.Kind).AppendLine(")");
        return content.ToString().TrimEnd();
    }

    /// <summary>Every name a page can be referred to by. A page's own slug wins over another page's alias.</summary>
    private static Dictionary<string, PagePlan> NameIndex(List<PagePlan> plans)
    {
        var names = new Dictionary<string, PagePlan>(StringComparer.Ordinal);
        foreach (var plan in plans) names[plan.Slug] = plan;
        foreach (var plan in plans)
        {
            foreach (var key in plan.Keys.Append(WikiText.Slug(plan.Title)).Concat(plan.Aliases.Select(a => WikiText.Slug(a))))
                names.TryAdd(key, plan);
        }

        return names;
    }

    /// <summary>Links that follow from the extraction rather than from the text: related subjects, module membership, dependencies.</summary>
    private async Task LinkStructureAsync(Run run, List<PagePlan> plans, CancellationToken ct)
    {
        var names = NameIndex(plans);
        var modules = plans.Where(p => p.Family == "module").ToList();
        var wanted = new HashSet<(int From, int To, string Kind)>();
        foreach (var plan in plans.Where(p => p.Page is not null))
        {
            foreach (var module in plan.Modules.Where(m => m.Page is not null)) wanted.Add((plan.Page!.Id, module.Page!.Id, WikiLinkKinds.PartOf));
            foreach (var related in plan.Related)
                if (names.GetValueOrDefault(related) is { Page: { } target } && target.Id != plan.Page!.Id) wanted.Add((plan.Page.Id, target.Id, WikiLinkKinds.Related));
            foreach (var dependency in plan.DependsOn.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                var key = WikiText.Slug(dependency);
                // A dependency is named loosely: by title, by directory, or by the last part of a directory.
                var target = modules.FirstOrDefault(m => WikiText.Slug(m.Title) == key || m.Aliases.Any(a => WikiText.Slug(a.Split(": ")[^1]) == key || WikiText.Slug(a.Split('/')[^1]) == key))
                             ?? names.GetValueOrDefault(key);
                if (target is { Page: { } page } && page.Id != plan.Page!.Id && target.Family != "functional") wanted.Add((plan.Page.Id, page.Id, WikiLinkKinds.DependsOn));
            }
        }
        await SyncLinksAsync(run, wanted, [WikiLinkKinds.PartOf, WikiLinkKinds.Related, WikiLinkKinds.DependsOn], ct);
    }

    private async Task SyncLinksAsync(Run run, HashSet<(int From, int To, string Kind)> wanted, string[] kinds, CancellationToken ct)
    {
        var existing = await db.WikiLinks.Where(l => l.WikiId == run.Wiki.Id && kinds.Contains(l.Kind)).ToListAsync(ct);
        foreach (var link in existing)
            if (!wanted.Remove((link.FromPageId, link.ToPageId, link.Kind))) db.WikiLinks.Remove(link);
        db.WikiLinks.AddRange(wanted.Select(l => new WikiLink { WikiId = run.Wiki.Id, FromPageId = l.From, ToPageId = l.To, Kind = l.Kind }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The link the wiki exists for: which code implements each functional subject. The model searches the
    /// repositories with the same tools as the chat agent and confirms what it finds before linking.
    /// </summary>
    private async Task LinkImplementationAsync(Run run, Scope scope, List<PagePlan> plans, CancellationToken ct)
    {
        var technical = plans.Where(p => p.Page is not null && p.Family is "technical" or "module").ToList();
        var functional = plans.Where(p => p.Page is not null && p.Family == "functional").ToList();
        if (technical.Count == 0 || functional.Count == 0) return;
        var existing = (await db.WikiLinks.Where(l => l.WikiId == run.Wiki.Id && l.Kind == WikiLinkKinds.Implements).ToListAsync(ct)).ToLookup(l => l.ToPageId);
        // Rewritten pages are searched again; when the code changed, so are the pages still without an implementation.
        var codeChanged = technical.Any(p => p.Written);
        var targets = functional.Where(p => p.Written || run.Row.Mode == WikiRunModes.Full || (codeChanged && !existing[p.Page!.Id].Any()))
            .OrderBy(p => Array.IndexOf(KindOrder, p.Kind)).ThenByDescending(p => p.Facts.Count).Take(Options.MaxLinkedPages).ToList();
        if (targets.Count == 0) return;

        var bySlug = technical.ToDictionary(p => p.Slug, StringComparer.Ordinal);
        // File path to the module page that covers it, for links the model found by reading a file.
        var byPath = new Dictionary<string, (int RepositoryId, PagePlan Module)>(StringComparer.Ordinal);
        foreach (var module in technical.Where(p => p.Family == "module"))
        {
            foreach (var key in module.Sources.Keys)
            {
                if (key.Split(':', 3) is ["repo", var repository, var path] && int.TryParse(repository, CultureInfo.InvariantCulture, out var repositoryId))
                    byPath.TryAdd(path, (repositoryId, module));
            }
        }

        var words = technical.ToDictionary(p => p, p => Words($"{p.Title} {string.Join(' ', p.Aliases)} {p.Page!.Summary}"));
        var tools = Tools(run.Row.RequestedById, run.Wiki.FolderId, ct);

        var done = 0;
        foreach (var batch in targets.Chunk(Parallelism))
        {
            await StageAsync(run, "linking", done, targets.Count, ct);
            var results = batch.Select(async plan =>
            {
                var own = Words($"{plan.Title} {string.Join(' ', plan.Aliases)} {plan.Page!.Summary}");
                var candidates = words.Select(w => (Plan: w.Key, Score: w.Value.Count(own.Contains))).Where(c => c.Score > 0)
                    .OrderByDescending(c => c.Score).Take(12).Select(c => c.Plan)
                    .Concat(technical.Where(p => p.Family == "module").Take(8)).Distinct()
                    .Select(p => new PageReference(p.Slug, p.Title, p.Kind, WikiText.Truncate(p.Page!.Summary, 200))).ToList();
                try
                {
                    return (plan, Found: await model.FindImplementationAsync(new(plan.Slug, plan.Title, plan.Kind, plan.Page.Summary), plan.Page.Content, candidates, tools, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Wiki {WikiId}: implementation search failed for {Slug}", run.Wiki.Id, plan.Slug);
                    return (plan, Found: (ImplementationLinks?)null);
                }
            }).ToList();
            await foreach (var finished in Task.WhenEach(results).WithCancellation(ct))
            {
                var (plan, found) = await finished;
                done++;
                if (found is null) { await ProgressAsync(run, "linking", done, targets.Count, ct); continue; }
                run.Stats.PagesLinkedToCode++;
                var links = new Dictionary<int, string>();
                foreach (var link in found.Links ?? [])
                {
                    var file = link.Path is { Length: > 0 } path && byPath.TryGetValue(path.Trim().TrimStart('/'), out var located) ? located : default;
                    var target = (link.Slug is { Length: > 0 } slug ? bySlug.GetValueOrDefault(WikiText.Slug(slug)) ?? bySlug.GetValueOrDefault(slug) : null) ?? file.Module;
                    if (target?.Page is null) continue;
                    var note = (link.Note?.Trim() ?? "") + (file.Module is null ? "" : " " + WikiText.CodeCitation(file.RepositoryId, link.Path!.Trim().TrimStart('/'), link.Line is > 0 ? link.Line : null));
                    links.TryAdd(target.Page.Id, WikiText.Truncate(note.Trim(), 1000));
                }
                db.WikiLinks.RemoveRange(existing[plan.Page!.Id]);
                db.WikiLinks.AddRange(links.Select(l => new WikiLink
                {
                    WikiId = run.Wiki.Id, FromPageId = l.Key, ToPageId = plan.Page.Id, Kind = WikiLinkKinds.Implements, Note = l.Value.Length == 0 ? null : l.Value
                }));
                await ProgressAsync(run, "linking", done, targets.Count, ct);
            }
        }
        await StageAsync(run, "linking", targets.Count, targets.Count, ct);
    }

    private static HashSet<string> Words(string text) =>
        WikiText.Slug(text, 2000).Split('-').Where(w => w.Length >= 4).ToHashSet(StringComparer.Ordinal);

    /// <summary>Search tools limited to the wiki's folder. Each call uses its own scope, so calls can run in parallel.</summary>
    private IList<AITool> Tools(string userId, int folderId, CancellationToken ct)
    {
        [Description("Find passages of the repositories and documents that literally contain a term, identifier or phrase. Returns excerpts with document IDs, file paths as titles, passage numbers and line numbers.")]
        async Task<string> GrepPassages(
            [Description("Short distinctive text to find, 2 to 200 characters. Not a question.")] string pattern,
            [Description("True to treat the pattern as a PostgreSQL regular expression.")] bool regex = false)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim().Length is < 2 or > 200) return Serialize(new { error = "Provide a pattern of 2 to 200 characters." });
            await using var scope = scopes.CreateAsyncScope();
            try { return Serialize(await scope.ServiceProvider.GetRequiredService<RagService>().GrepAsync(userId, pattern.Trim(), regex, limit: 10, ct: ct, folderId: folderId)); }
            catch (Exception ex) when (ex is ArgumentException or LibraryException) { return Serialize(new { error = ex.Message.Split(" (Parameter")[0] }); }
        }

        [Description("Read consecutive passages of one document or file as continuous text, to confirm a grep match in context.")]
        async Task<string> ReadPassages(
            [Description("Document ID returned by grep_passages.")] int documentId,
            [Description("One-based number of the first passage to read.")] int from,
            [Description("Number of passages, between 1 and 6.")] int count = 3)
        {
            await using var scope = scopes.CreateAsyncScope();
            try { return Serialize(await scope.ServiceProvider.GetRequiredService<RagService>().ReadPassagesAsync(userId, documentId, Math.Max(from, 1), Math.Clamp(count, 1, 6), ct, folderId)); }
            catch (LibraryException ex) { return Serialize(new { error = ex.Message }); }
        }

        return [AIFunctionFactory.Create(GrepPassages, "grep_passages"), AIFunctionFactory.Create(ReadPassages, "read_passages")];

        static string Serialize(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
    }

    /// <summary>Links found in the text, the overview page, page embeddings and the checks.</summary>
    private async Task IndexAsync(Run run, Scope scope, CancellationToken ct)
    {
        await StageAsync(run, "indexing", 0, 0, ct);
        var pages = await db.WikiPages.Where(p => p.WikiId == run.Wiki.Id).ToListAsync(ct);
        if (run.Row.Mode != WikiRunModes.Lint) pages = await OverviewAsync(run, pages, ct);

        var targets = new Dictionary<string, WikiPage>(StringComparer.Ordinal);
        foreach (var page in pages) targets[page.Slug] = page;
        foreach (var page in pages)
        {
            foreach (var key in page.Aliases.Select(a => WikiText.Slug(a)).Prepend(WikiText.Slug(page.Title)))
                targets.TryAdd(key, page);
        }

        var stronger = (await db.WikiLinks.AsNoTracking().Where(l => l.WikiId == run.Wiki.Id && l.Kind != WikiLinkKinds.Mentions)
            .Select(l => new { l.FromPageId, l.ToPageId }).ToListAsync(ct)).Select(l => (l.FromPageId, l.ToPageId)).ToHashSet();
        var mentions = new HashSet<(int From, int To, string Kind)>();
        foreach (var page in pages)
        {
            foreach (var slug in WikiText.LinkedSlugs(page.Content))
            {
                if (!targets.TryGetValue(slug, out var target)) run.Stats.BrokenLinks++;
                // A typed link between two pages already says more than a mention.
                else if (target.Id != page.Id && !stronger.Contains((page.Id, target.Id))) mentions.Add((page.Id, target.Id, WikiLinkKinds.Mentions));
            }
        }

        await SyncLinksAsync(run, mentions, [WikiLinkKinds.Mentions], ct);

        if (db.Database.IsRelational())
        {
            foreach (var batch in pages.Where(p => p.Embedding is null).Chunk(embeddings.BatchSize))
            {
                var vectors = await embeddings.GenerateAsync(batch.Select(p => $"{p.Title}\n{string.Join(", ", p.Aliases)}\n{p.Summary}").ToArray(), ct: ct);
                for (var i = 0; i < batch.Length; i++) batch[i].Embedding = new Vector(vectors[i].Vector);
                await db.SaveChangesAsync(ct);
            }
        }

        var links = await db.WikiLinks.AsNoTracking().Where(l => l.WikiId == run.Wiki.Id).Select(l => new { l.FromPageId, l.ToPageId, l.Kind }).ToListAsync(ct);
        var overview = pages.FirstOrDefault(p => p.Kind == WikiPageKinds.Overview)?.Id;
        var connected = links.Where(l => l.FromPageId != overview).SelectMany(l => new[] { l.FromPageId, l.ToPageId }).ToHashSet();
        var keys = scope.Documents.Values.Select(WikiDelta.Key).ToHashSet(StringComparer.Ordinal);
        run.Stats.OrphanPages = pages.Where(p => p.Kind != WikiPageKinds.Overview && !connected.Contains(p.Id)).Select(p => p.Title).Take(MaxExamples).ToList();
        run.Stats.UnresolvedCitations = pages.SelectMany(p => WikiText.Citations(p.Content)).Count(c => c.DocumentId is { } id
            ? !scope.Documents.ContainsKey(id)
            : !keys.Contains($"repo:{c.RepositoryId}:{c.Path}"));
        var implemented = links.Where(l => l.Kind == WikiLinkKinds.Implements).Select(l => l.ToPageId).ToHashSet();
        if (pages.Any(p => WikiPageKinds.Technical.Contains(p.Kind)))
        {
            run.Stats.WithoutImplementation = pages.Where(p => p.Kind is WikiPageKinds.Feature or WikiPageKinds.Rule && !implemented.Contains(p.Id))
                .Select(p => p.Title).Take(MaxExamples).ToList();
        }

        (run.Stats.Pages, run.Stats.Links, run.Stats.ImplementationLinks) = (pages.Count, links.Count, links.Count(l => l.Kind == WikiLinkKinds.Implements));
    }

    private async Task<List<WikiPage>> OverviewAsync(Run run, List<WikiPage> pages, CancellationToken ct)
    {
        var overview = pages.FirstOrDefault(p => p.Kind == WikiPageKinds.Overview);
        var listed = pages.Where(p => p.Kind != WikiPageKinds.Overview).OrderBy(p => Array.IndexOf(KindOrder, p.Kind)).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Take(250).Select(p => new PageReference(p.Slug, p.Title, p.Kind, WikiText.Truncate(p.Summary, 200))).ToList();
        if (listed.Count == 0)
        {
            if (overview is not null) db.WikiPages.Remove(overview);
            await db.SaveChangesAsync(ct);
            return pages.Where(p => p != overview).ToList();
        }
        var hash = WikiText.Hash(JsonSerializer.Serialize(new { listed, run.Wiki.Language, run.Wiki.Guidelines }));
        if (overview?.InputHash == hash) return pages;
        var name = await db.Folders.Where(f => f.Id == run.Wiki.FolderId).Select(f => f.Name).SingleAsync(ct);
        WrittenPage written;
        try { written = await model.WriteOverviewAsync(name, listed, run.Wiki.Language, run.Wiki.Guidelines, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pages are usable without their home page; it is written at the next refresh.
            logger.LogWarning(ex, "Wiki {WikiId}: the overview could not be written", run.Wiki.Id);
            return pages;
        }
        if (overview is null)
        {
            db.WikiPages.Add(overview = new WikiPage { WikiId = run.Wiki.Id, Slug = OverviewSlug, Kind = WikiPageKinds.Overview });
            pages = [.. pages, overview];
        }
        var slugs = listed.Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);
        (overview.Title, overview.Summary) = (WikiText.Truncate(name, 200), WikiText.Truncate(written.Summary?.Trim(), 1000));
        overview.Content = WikiText.ReplaceLinks(written.Content?.Trim() ?? "", (slug, label) => slugs.Contains(slug) ? $"[[{slug}|{label}]]" : null);
        (overview.InputHash, overview.UpdatedUtc, overview.Embedding) = (hash, DateTime.UtcNow, null);
        await db.SaveChangesAsync(ct);
        return pages;
    }

    private static int RepositoryOf(string moduleKey) =>
        moduleKey.Split(':', 3) is [_, var repository, _] && int.TryParse(repository, CultureInfo.InvariantCulture, out var id) ? id : 0;

    /// <summary>Splits "path:line" as the model writes it; the line is optional.</summary>
    private static (string? Path, int? Line) CodeReference(string? reference)
    {
        var value = reference?.Trim().Trim('`').TrimStart('.', '/');
        if (string.IsNullOrEmpty(value)) return (null, null);
        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var line) && line > 0
            ? (value[..colon], line)
            : (value, null);
    }
}
