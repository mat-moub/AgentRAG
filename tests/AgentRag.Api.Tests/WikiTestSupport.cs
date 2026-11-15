using System.Text.Json;
using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal sealed class WikiTestDb(string name) : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Vector SQL is covered by the PostgreSQL translation tests. WikiPage.Embedding is already ignored off PostgreSQL.
        builder.Entity<DocumentChunk>().Ignore(c => c.Embedding);
    }
}

/// <summary>A deterministic stand-in for the language model that counts its calls.</summary>
internal sealed class FakeWikiModel : WikiModel
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> calls = [];
    private readonly List<string> titles = [], modules = [], written = [], overviews = [];

    public FakeWikiModel() : base(null!, Options.Create(new WikiOptions()), NullLogger<WikiModel>.Instance) { }

    public Dictionary<string, SourceExtraction> Documents { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SourceExtraction> Modules { get; } = new(StringComparer.Ordinal);
    /// <summary>Titles of documents whose extraction throws.</summary>
    public HashSet<string> FailingTitles { get; } = new(StringComparer.Ordinal);
    /// <summary>When set, every extraction throws this message.</summary>
    public string? FailEveryExtraction { get; set; }
    /// <summary>Runs at the start of every extraction call; it may cancel the run.</summary>
    public Action<CancellationToken>? OnExtract { get; set; }
    public Func<PageReference, IReadOnlyList<PageReference>, ImplementationLinks> Implementation { get; set; } = (_, _) => new(null, "nothing found");

    public int Total { get { lock (gate) return calls.Values.Sum(); } }
    public int Calls(string method) { lock (gate) return calls.GetValueOrDefault(method); }
    public IReadOnlyList<string> ExtractedTitles { get { lock (gate) return [.. titles]; } }
    public IReadOnlyList<string> ExtractedModules { get { lock (gate) return [.. modules]; } }
    public IReadOnlyList<string> WrittenTitles { get { lock (gate) return [.. written]; } }
    public IReadOnlyList<string> OverviewPages { get { lock (gate) return [.. overviews]; } }

    public void Reset()
    {
        lock (gate)
        {
            calls.Clear();
            titles.Clear();
            modules.Clear();
            written.Clear();
            overviews.Clear();
        }
    }

    private void Record(string method, List<string>? list = null, string? value = null)
    {
        lock (gate)
        {
            calls[method] = calls.GetValueOrDefault(method) + 1;
            if (list is not null && value is not null) list.Add(value);
        }
    }

    public override Task<SourceExtraction> ExtractDocumentAsync(string title, string passages, string language, string? guidelines, CancellationToken ct)
    {
        Record(nameof(ExtractDocumentAsync), titles, title);
        OnExtract?.Invoke(ct);
        if (FailEveryExtraction is { } message || FailingTitles.Contains(title))
            throw new InvalidOperationException(FailEveryExtraction ?? $"The model refused {title}");
        return Task.FromResult(Documents.GetValueOrDefault(title) ?? new SourceExtraction(null, $"Summary of {title}.", null, null, []));
    }

    public override Task<SourceExtraction> ExtractModuleAsync(string repository, string module, string digest, string language, string? guidelines, CancellationToken ct)
    {
        Record(nameof(ExtractModuleAsync), modules, module);
        OnExtract?.Invoke(ct);
        if (FailEveryExtraction is { } message) throw new InvalidOperationException(message);
        var configured = Modules.GetValueOrDefault(module) ?? new SourceExtraction($"{module} module", $"Code of {module}.", null, null, []);
        // The digest size stands for what the model read: a changed file changes what is said about its module.
        var facts = (configured.Facts ?? []).Append(new ExtractedFact($"The module has a digest of {digest.Length} characters.", null)).ToArray();
        return Task.FromResult(configured with { Facts = facts });
    }

    public override Task<string[][]> FindDuplicatesAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        Record(nameof(FindDuplicatesAsync));
        return Task.FromResult<string[][]>([]);
    }

    public override Task<WrittenPage> WritePageAsync(PageBrief brief, CancellationToken ct)
    {
        Record(nameof(WritePageAsync), written, brief.Title);
        var links = string.Join(' ', brief.LinkCandidates.Select(c => $"[[{c.Slug}|x]]"));
        var facts = string.Join('\n', brief.Facts.Select(f => $"- {f.Text} {f.Citation}"));
        return Task.FromResult(new WrittenPage($"About {brief.Title}.", $"{brief.Title} is described here. {links} [[ghost-page|Ghost]]\n{facts}"));
    }

    public override Task<WrittenPage> WriteOverviewAsync(string name, IReadOnlyList<PageReference> pages, string language, string? guidelines, CancellationToken ct)
    {
        Record(nameof(WriteOverviewAsync), overviews, name);
        var links = string.Join('\n', pages.Select(p => $"- [[{p.Slug}|{p.Title}]]"));
        return Task.FromResult(new WrittenPage($"Home of {name}.", $"Welcome.\n{links}\n[[ghost-page|Ghost]]"));
    }

    public override Task<ImplementationLinks> FindImplementationAsync(PageReference page, string content,
        IReadOnlyList<PageReference> candidates, IList<AITool> tools, CancellationToken ct)
    {
        Record(nameof(FindImplementationAsync));
        return Task.FromResult(Implementation(page, candidates));
    }
}

internal sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0, 0 }))));
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// A seeded library: alice owns "Shop", which holds an uploaded spec, a README and a repository folder with code,
/// and a "Finance" subfolder shared with other people, which the wiki must leave out. "reader" can read "Shop".
/// </summary>
internal sealed class WikiLibrary : IDisposable
{
    public const string Owner = "alice", Reader = "reader", Writer = "writer", Stranger = "mallory", Carol = "carol";
    public const string OrderServicePath = "src/Orders/OrderService.cs", ControllerPath = "src/Orders/OrdersController.cs",
        InvoicePath = "src/Billing/InvoiceService.cs";

    public const string OrderServiceCode = """
        namespace Shop.Orders;

        public class OrderService
        {
            public decimal Total(decimal amount) => amount;
        }
        """;

    public const string ControllerCode = """
        namespace Shop.Orders;

        public class OrdersController
        {
            [HttpGet("orders")]
            public int List() => 0;
        }
        """;

    public const string InvoiceCode = """
        namespace Shop.Billing;

        public class InvoiceService
        {
            public string Number() => "INV-1";
        }
        """;

    private readonly string name = Guid.NewGuid().ToString();
    private readonly ServiceProvider provider;
    private readonly EmbeddingService embeddingService;

    public FakeWikiModel Model { get; } = new();
    public WikiOptions Options { get; } = new() { MaxParallelism = 2, ModuleCharacters = 300 };
    public WikiRunQueue Queue { get; } = new();
    public int ParentId, RepoFolderId, FinanceId, HiddenFolderId, RepositoryId, WikiId, SpecId, ReadmeId, SecretId;

    public WikiLibrary(bool seed = true)
    {
        embeddingService = new(new RecordingEmbeddingGenerator(), Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions()), NullLogger<EmbeddingService>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<WikiModel>(Model);
        services.AddSingleton(embeddingService);
        services.AddSingleton<IOptions<WikiOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddScoped<AppDbContext>(_ => new WikiTestDb(name));
        services.AddScoped<WikiBuilder>();
        provider = services.BuildServiceProvider();
        Model.Documents["Order spec"] = new SourceExtraction(null, "The specification of orders.", null, null,
        [
            new("Order checkout", "feature", ["Checkout"], "Customers place an order.",
                [new("An order is confirmed after payment.", "1")], ["Free shipping"]),
            new("Free shipping", "rule", null, "Orders above 100 euros ship free.",
                [new("Shipping is free above 100 euros.", "2")], [])
        ]);
        Model.Modules["src/Orders"] = new SourceExtraction("Orders module", "Places and lists orders.",
            [new("Totals are computed by the service.", OrderServicePath + ":3")], ["src/Billing"],
            [new("OrderService", "component", null, "Computes order totals.", [new("Total returns the amount.", OrderServicePath + ":5")], null)]);
        Model.Modules["src/Billing"] = new SourceExtraction("Billing module", "Issues invoices.",
            [new("Invoices are numbered.", InvoicePath + ":5")], null,
            [new("InvoiceService", "component", null, "Numbers invoices.", [new("Number returns an invoice number.", InvoicePath + ":5")], null)]);
        // The checkout is implemented by the whole orders module; the rule is found in a file, not among the candidates.
        Model.Implementation = (page, candidates) => page.Title switch
        {
            "Order checkout" => new([new(candidates.First(c => c.Kind == WikiPageKinds.Module && c.Slug.EndsWith("orders")).Slug, null, null, "Orders are created here.")], null),
            "Free shipping" => new([new(null, "/" + OrderServicePath, 12, "The total ignores shipping above the threshold.")], null),
            _ => new(null, "nothing found")
        };
        if (seed) Seed();
    }

    public WikiTestDb Open() => new(name);
    public WikiService Service(AppDbContext db) => new(db, embeddingService, Queue);
    public WikiRunWorker Worker => new(provider.GetRequiredService<IServiceScopeFactory>(), Queue, NullLogger<WikiRunWorker>.Instance);

    private static KnowledgeDocument Upload(string title, int folderId, params string[] passages) => new()
    {
        OwnerId = Owner, FolderId = folderId, Title = title, PassageOverlap = 0,
        Text = new DocumentText { Content = string.Join("\n", passages) },
        Chunks = [.. passages.Select((text, index) => new DocumentChunk { Index = index, Content = text })]
    };

    private KnowledgeDocument RepositoryFile(string path, string content, string sha, bool documentation = false) => new()
    {
        OwnerId = Owner, FolderId = RepoFolderId, RepositoryId = RepositoryId, SourcePath = path, Title = path, SourceSha = sha,
        PassageOverlap = 0, Indexed = documentation, Text = new DocumentText { Content = content },
        Chunks = [new DocumentChunk { Index = 0, Content = content }]
    };

    private void Seed()
    {
        using var db = Open();
        var parent = new KnowledgeFolder { Name = "Shop", OwnerId = Owner };
        var hidden = new KnowledgeFolder { Name = "Private archive", OwnerId = Owner };
        db.Folders.AddRange(parent, hidden);
        db.SaveChanges();
        var repoFolder = new KnowledgeFolder { Name = "acme/shop", OwnerId = Owner, ParentId = parent.Id };
        var finance = new KnowledgeFolder { Name = "Finance", OwnerId = Owner, ParentId = parent.Id };
        db.Folders.AddRange(repoFolder, finance);
        db.SaveChanges();
        (ParentId, RepoFolderId, FinanceId, HiddenFolderId) = (parent.Id, repoFolder.Id, finance.Id, hidden.Id);
        db.FolderMembers.AddRange(
            new FolderMember { FolderId = parent.Id, UserId = Reader, Role = "read" },
            new FolderMember { FolderId = parent.Id, UserId = Writer, Role = "write" },
            new FolderMember { FolderId = finance.Id, UserId = Carol, Role = "read" });
        var repository = new RepositoryConnection { FolderId = repoFolder.Id, RepositoryOwner = "acme", RepositoryName = "shop", Branch = "main", ProtectedToken = "unused" };
        db.Repositories.Add(repository);
        db.SaveChanges();
        RepositoryId = repository.Id;

        var spec = Upload("Order spec", parent.Id, "Customers place an order and pay for it.", "Orders above 100 euros ship free of charge.");
        var secret = Upload("Payroll secrets", finance.Id, "Salaries are confidential.");
        var readme = RepositoryFile("README.md", "The shop sells goods.", "sha-readme", documentation: true);
        db.Documents.AddRange(spec, secret, readme,
            RepositoryFile(OrderServicePath, OrderServiceCode, "sha-order-service"),
            RepositoryFile(ControllerPath, ControllerCode, "sha-controller"),
            RepositoryFile(InvoicePath, InvoiceCode, "sha-invoice"));
        var wiki = new Wiki { FolderId = parent.Id };
        db.Wikis.Add(wiki);
        db.SaveChanges();
        (SpecId, SecretId, ReadmeId, WikiId) = (spec.Id, secret.Id, readme.Id, wiki.Id);
    }

    /// <summary>Starts a run as the user and executes it, as the background worker would.</summary>
    public async Task<(WikiRun Run, WikiRunStats Stats)> BuildAsync(string mode = WikiRunModes.Incremental, string user = Owner)
    {
        int id;
        await using (var db = Open()) id = (await Service(db).StartRunAsync(user, WikiId, mode, default)).Id;
        await Worker.RunAsync(id, default);
        await using var read = Open();
        var run = await read.WikiRuns.AsNoTracking().SingleAsync(r => r.Id == id);
        return (run, run.Stats is null ? new WikiRunStats() : JsonSerializer.Deserialize<WikiRunStats>(run.Stats, JsonSerializerOptions.Web)!);
    }

    public async Task<int> DocumentIdAsync(string path)
    {
        await using var db = Open();
        return await db.Documents.Where(d => d.RepositoryId == RepositoryId && d.SourcePath == path).Select(d => d.Id).SingleAsync();
    }

    /// <summary>A repository file that changed is deleted and added again under a new document id, as synchronisation does.</summary>
    public async Task ReplaceRepositoryFileAsync(string path, string content, string sha)
    {
        await using var db = Open();
        var old = await db.Documents.Include(d => d.Text).Include(d => d.Chunks).SingleAsync(d => d.RepositoryId == RepositoryId && d.SourcePath == path);
        db.Documents.Remove(old);
        db.Documents.Add(RepositoryFile(path, content, sha, documentation: path.EndsWith(".md")));
        await db.SaveChangesAsync();
    }

    public async Task DeleteDocumentAsync(int id)
    {
        await using var db = Open();
        db.Documents.Remove(await db.Documents.Include(d => d.Text).Include(d => d.Chunks).SingleAsync(d => d.Id == id));
        await db.SaveChangesAsync();
    }

    public async Task<WikiPage> PageAsync(string slug)
    {
        await using var db = Open();
        return await db.WikiPages.AsNoTracking().Include(p => p.Sources).SingleAsync(p => p.WikiId == WikiId && p.Slug == slug);
    }

    public void Dispose()
    {
        provider.Dispose();
        embeddingService.Dispose();
    }
}
