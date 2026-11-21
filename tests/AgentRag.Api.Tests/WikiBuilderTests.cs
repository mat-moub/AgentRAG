using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;

public class WikiBuilderTests
{
    private const string Orders = "module-shop-src-orders";
    private const string Billing = "module-shop-src-billing";

    private static async Task<List<WikiPage>> PagesAsync(WikiLibrary library)
    {
        await using var db = library.Open();
        return await db.WikiPages.AsNoTracking().Include(p => p.Sources).Where(p => p.WikiId == library.WikiId).OrderBy(p => p.Slug).ToListAsync();
    }

    [Fact]
    public void Record_EnteringAStage_NotesItsStartAndCount_AndAddsThePreviousStageDuration()
    {
        var row = new WikiRun { Stage = "queued" };
        var stats = new WikiRunStats();
        var start = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

        WikiBuilder.Record(row, stats, "listing", 0, 0, start);
        WikiBuilder.Record(row, stats, "extracting", 2, 14, start.AddSeconds(10));
        WikiBuilder.Record(row, stats, "extracting", 5, 14, start.AddSeconds(40));

        // Progress inside a stage keeps the stage's start, so its pace is 3 sources in 30 seconds.
        Assert.Equal((start.AddSeconds(10), 2, 5, 14), (row.StageStartedUtc, row.StageStartCompleted, row.Completed, row.Total));
        Assert.Equal(10, stats.StageSeconds["listing"]);

        WikiBuilder.Record(row, stats, "writing", 0, 120, start.AddSeconds(70));

        Assert.Equal((start.AddSeconds(70), 0), (row.StageStartedUtc, row.StageStartCompleted));
        Assert.Equal(60, stats.StageSeconds["extracting"]);
        Assert.DoesNotContain("queued", stats.StageSeconds.Keys);
    }

    [Fact]
    public async Task Run_Completed_RecordsTheDurationOfEveryStage()
    {
        using var library = new WikiLibrary();

        var (run, stats) = await library.BuildAsync();

        Assert.Equal(["extracting", "indexing", "linking", "listing", "resolving", "writing"], stats.StageSeconds.Keys.Order(StringComparer.Ordinal));
        Assert.All(stats.StageSeconds.Values, seconds => Assert.True(seconds >= 0));
        Assert.NotNull(run.StageStartedUtc);
        Assert.Equal("completed", run.Stage);
    }

    [Fact]
    public async Task Run_CodeItemNamedLikeAnIdentifier_IsAComponent_AndALinePastTheEndOfTheFileIsNotCited()
    {
        using var library = new WikiLibrary();
        library.Model.Modules["src/Orders"] = library.Model.Modules["src/Orders"] with
        {
            Items =
            [
                new("computeOrderTotal", "feature", null, "Adds the order lines.", [new("The total adds every line.", WikiLibrary.OrderServicePath + ":999")], null),
                new("Order discounts", "feature", null, "Discounts lower the total.", [new("A discount applies to the total.", WikiLibrary.OrderServicePath + ":5")], null),
            ]
        };

        await library.BuildAsync();

        var pages = await PagesAsync(library);
        Assert.Equal(WikiPageKinds.Component, pages.Single(p => p.Slug == "computeordertotal").Kind);
        Assert.Equal(WikiPageKinds.Feature, pages.Single(p => p.Slug == "order-discounts").Kind);
        var total = pages.Single(p => p.Slug == "computeordertotal").Content;
        Assert.Contains($"repo:{library.RepositoryId}/{WikiLibrary.OrderServicePath})", total);
        Assert.DoesNotContain("#L999", total);
        Assert.Contains("#L5)", pages.Single(p => p.Slug == "order-discounts").Content);
    }

    [Fact]
    public async Task Run_DocumentItemNamedLikeAnIdentifier_ReplacesItsFeaturePageWithAComponent()
    {
        using var library = new WikiLibrary();
        var spec = library.Model.Documents["Order spec"];
        library.Model.Documents["Order spec"] = spec with
        {
            Items = [.. spec.Items!, new("computeShipping", "feature", null, "Computes the shipping fee.", [new("Shipping is free above 100 euros.", "2")], null)]
        };
        // A page left by an earlier run that took the function for a feature.
        await using (var db = library.Open())
        {
            db.WikiPages.Add(new WikiPage { WikiId = library.WikiId, Slug = "computeshipping", Title = "computeShipping", Kind = WikiPageKinds.Feature });
            await db.SaveChangesAsync();
        }

        await library.BuildAsync();

        var page = Assert.Single(await PagesAsync(library), p => p.Slug == "computeshipping");
        Assert.Equal(WikiPageKinds.Component, page.Kind);
        Assert.Contains("Shipping is free above 100 euros.", page.Content);
    }

    [Fact]
    public async Task Run_FirstBuild_CreatesPagesOfEveryKindAndCompletes()
    {
        using var library = new WikiLibrary();

        var (run, stats) = await library.BuildAsync();

        Assert.Equal((WikiRunStatus.Completed, "completed", (string?)null), (run.Status, run.Stage, run.Error));
        Assert.NotNull(run.FinishedUtc);
        var pages = await PagesAsync(library);
        string[] Slugs(string kind) => pages.Where(p => p.Kind == kind).Select(p => p.Slug).ToArray();
        Assert.Equal(["order-checkout"], Slugs(WikiPageKinds.Feature));
        Assert.Equal(["free-shipping"], Slugs(WikiPageKinds.Rule));
        Assert.Equal([Billing, Orders], Slugs(WikiPageKinds.Module));
        Assert.Equal(["invoiceservice", "orderservice"], Slugs(WikiPageKinds.Component));
        Assert.Equal(["source-order-spec", "source-readme-md"], Slugs(WikiPageKinds.Source));
        Assert.Equal([WikiBuilder.OverviewSlug], Slugs(WikiPageKinds.Overview));
        Assert.Equal(pages.Count, stats.Pages);
        Assert.Equal(pages.Count - 1, stats.PagesCreated);
        Assert.Equal(5, stats.SourcesAdded);
        Assert.Equal(0, stats.SourcesFailed);
        Assert.All(pages, p => Assert.False(p.Stale));
        Assert.All(pages, p => Assert.NotEqual("", p.Summary));
    }

    [Fact]
    public async Task Run_GeneratedLinks_AreKeptOnlyWhenThePageExists()
    {
        using var library = new WikiLibrary();
        var (_, stats) = await library.BuildAsync();

        var pages = await PagesAsync(library);

        var slugs = pages.Select(p => p.Slug).ToHashSet();
        Assert.All(pages, p =>
        {
            Assert.DoesNotContain("ghost-page", p.Content);
            Assert.All(WikiText.LinkedSlugs(p.Content), s => Assert.Contains(s, slugs));
        });
        Assert.Contains("Ghost", pages.Single(p => p.Slug == "order-checkout").Content);
        Assert.Equal(0, stats.BrokenLinks);
        // The page text links to its candidates, which produces mention links when no stronger link exists.
        Assert.Contains("[[free-shipping|x]]", pages.Single(p => p.Slug == "order-checkout").Content);
    }

    [Fact]
    public async Task Run_Provenance_ListsTheDocumentsEachPageWasWrittenFrom()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        var service = await library.DocumentIdAsync(WikiLibrary.OrderServicePath);
        var controller = await library.DocumentIdAsync(WikiLibrary.ControllerPath);

        var checkout = await library.PageAsync("order-checkout");
        var orders = await library.PageAsync(Orders);
        var readme = await library.PageAsync("source-readme-md");

        Assert.Equal([$"doc:{library.SpecId}"], checkout.Sources.Select(s => s.SourceKey));
        Assert.Equal(library.SpecId, Assert.Single(checkout.Sources).DocumentId);
        // Every file of a module is a source of the module's page, so any file path leads to it.
        Assert.Equal(
            new[] { ($"repo:{library.RepositoryId}:{WikiLibrary.OrderServicePath}", (int?)service), ($"repo:{library.RepositoryId}:{WikiLibrary.ControllerPath}", controller) },
            orders.Sources.OrderBy(s => s.SourceKey, StringComparer.Ordinal).Select(s => (s.SourceKey, s.DocumentId)));
        Assert.Equal(library.ReadmeId, Assert.Single(readme.Sources).DocumentId);
        Assert.Empty((await library.PageAsync(WikiBuilder.OverviewSlug)).Sources);
    }

    [Fact]
    public async Task Run_CodeAndFunctionalPages_AreConnectedWithImplementsLinks()
    {
        using var library = new WikiLibrary();
        var (_, stats) = await library.BuildAsync();

        await using var db = library.Open();
        var links = await db.WikiLinks.AsNoTracking().Include(l => l.FromPage).Include(l => l.ToPage).ToListAsync();
        var implements = links.Where(l => l.Kind == WikiLinkKinds.Implements).ToList();

        Assert.Equal([("order-checkout", Orders), ("free-shipping", Orders)], implements.OrderByDescending(l => l.ToPage!.Slug).Select(l => (l.ToPage!.Slug, l.FromPage!.Slug)));
        Assert.Equal(2, stats.ImplementationLinks);
        Assert.Equal("Orders are created here.", implements.Single(l => l.ToPage!.Slug == "order-checkout").Note);
        // A file path found by the model is cited, so the link can be followed to the code.
        var shipping = implements.Single(l => l.ToPage!.Slug == "free-shipping").Note!;
        Assert.Contains("The total ignores shipping", shipping);
        Assert.Equal(new WikiCitation(null, null, library.RepositoryId, WikiLibrary.OrderServicePath, 12), Assert.Single(WikiText.Citations(shipping)));
        Assert.Equal(2, stats.PagesLinkedToCode);
        // Structure links: components belong to their module, the module depends on another one.
        Assert.Contains(links, l => (l.Kind, l.FromPage!.Slug, l.ToPage!.Slug) == (WikiLinkKinds.PartOf, "orderservice", Orders));
        Assert.Contains(links, l => (l.Kind, l.FromPage!.Slug, l.ToPage!.Slug) == (WikiLinkKinds.DependsOn, Orders, Billing));
        Assert.Contains(links, l => (l.Kind, l.FromPage!.Slug, l.ToPage!.Slug) == (WikiLinkKinds.Related, "order-checkout", "free-shipping"));
        Assert.Equal(links.Count, links.Select(l => (l.FromPageId, l.ToPageId, l.Kind)).Distinct().Count());
    }

    [Fact]
    public async Task Run_Overview_IsTheIndexPageListingTheOtherPages()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();

        var overview = await library.PageAsync(WikiBuilder.OverviewSlug);

        Assert.Equal((WikiPageKinds.Overview, "Shop"), (overview.Kind, overview.Title));
        var linked = WikiText.LinkedSlugs(overview.Content);
        Assert.Contains("order-checkout", linked);
        Assert.Contains(Orders, linked);
        Assert.DoesNotContain("ghost-page", overview.Content);
        Assert.DoesNotContain(WikiBuilder.OverviewSlug, linked);
        Assert.Equal(1, library.Model.Calls(nameof(WikiModel.WriteOverviewAsync)));
    }

    [Fact]
    public async Task Run_SubfolderSharedWithAnotherAudience_IsLeftOut()
    {
        using var library = new WikiLibrary();

        var (_, stats) = await library.BuildAsync();

        await using var db = library.Open();
        Assert.Equal(1, stats.SourcesExcluded);
        Assert.DoesNotContain("Payroll secrets", library.Model.ExtractedTitles);
        Assert.DoesNotContain(await db.WikiPageSources.AsNoTracking().ToListAsync(), s => s.DocumentId == library.SecretId);
        Assert.DoesNotContain(await db.WikiSources.AsNoTracking().ToListAsync(), s => s.DocumentId == library.SecretId);
        Assert.DoesNotContain(await db.WikiPages.AsNoTracking().ToListAsync(), p => p.Content.Contains("confidential") || p.Title.Contains("Payroll"));
    }

    [Fact]
    public async Task Run_NothingChanged_MakesNoModelCall()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        var before = (await PagesAsync(library)).ToDictionary(p => p.Slug, p => p.UpdatedUtc);
        library.Model.Reset();

        var (run, stats) = await library.BuildAsync();

        Assert.Equal(WikiRunStatus.Completed, run.Status);
        Assert.Equal(0, library.Model.Total);
        Assert.Equal((0, 0, 0, 0, 0), (stats.SourcesAdded, stats.SourcesChanged, stats.SourcesRemoved, stats.PagesCreated, stats.PagesUpdated));
        Assert.Equal(before, (await PagesAsync(library)).ToDictionary(p => p.Slug, p => p.UpdatedUtc));
    }

    [Fact]
    public async Task Run_OneRepositoryFileChanged_ExtractsOnlyItsModuleAndRewritesOnlyItsPages()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        var orders = await PagesAsync(library);
        library.Model.Reset();
        await library.ReplaceRepositoryFileAsync(WikiLibrary.InvoicePath, WikiLibrary.InvoiceCode + "\n// A new comment that makes the file longer.\n", "sha-invoice-2");
        var newId = await library.DocumentIdAsync(WikiLibrary.InvoicePath);

        var (run, stats) = await library.BuildAsync();

        Assert.Equal(WikiRunStatus.Completed, run.Status);
        Assert.Equal((1, 0, 0), (stats.SourcesChanged, stats.SourcesRemoved, stats.SourcesAdded));
        Assert.Equal(["src/Billing"], library.Model.ExtractedModules);
        Assert.Empty(library.Model.ExtractedTitles);
        // The component is described by the same facts as before, so only the module page is written again.
        Assert.Equal(["Billing module"], library.Model.WrittenTitles);
        Assert.Equal(0, library.Model.Calls(nameof(WikiModel.FindImplementationAsync)));
        var billing = await library.PageAsync(Billing);
        Assert.Equal(newId, Assert.Single(billing.Sources).DocumentId);
        var pages = await PagesAsync(library);
        Assert.Equal(orders.Count, pages.Count);
        Assert.All(pages, p => Assert.False(p.Stale));
        foreach (var page in pages.Where(p => p.Slug is Orders or "order-checkout" or "free-shipping"))
            Assert.Equal(orders.Single(o => o.Slug == page.Slug).UpdatedUtc, page.UpdatedUtc);
    }

    [Fact]
    public async Task Run_RepositoryFileWithSameShaUnderNewId_IsRelinkedWithoutAnyModelCall()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        library.Model.Reset();
        await library.ReplaceRepositoryFileAsync(WikiLibrary.OrderServicePath, WikiLibrary.OrderServiceCode, "sha-order-service");
        var newId = await library.DocumentIdAsync(WikiLibrary.OrderServicePath);

        var (_, stats) = await library.BuildAsync();

        Assert.Equal(0, library.Model.Total);
        Assert.Equal((0, 0, 0), (stats.SourcesAdded, stats.SourcesChanged, stats.SourcesRemoved));
        var orders = await library.PageAsync(Orders);
        Assert.Contains(orders.Sources, s => s.DocumentId == newId);
        Assert.Equal(2, orders.Sources.Count);
    }

    [Fact]
    public async Task Run_SpecificationRemoved_RemovesThePagesThatOnlyCameFromIt()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        library.Model.Reset();
        await library.DeleteDocumentAsync(library.SpecId);

        var (run, stats) = await library.BuildAsync();

        Assert.Equal(WikiRunStatus.Completed, run.Status);
        var slugs = (await PagesAsync(library)).Select(p => p.Slug).ToArray();
        Assert.DoesNotContain("order-checkout", slugs);
        Assert.DoesNotContain("free-shipping", slugs);
        Assert.DoesNotContain("source-order-spec", slugs);
        Assert.Contains(Orders, slugs);
        Assert.Contains("source-readme-md", slugs);
        Assert.Equal((1, 3), (stats.SourcesRemoved, stats.PagesRemoved));
        Assert.Empty(library.Model.ExtractedTitles);
        await using var db = library.Open();
        Assert.DoesNotContain(await db.WikiSources.AsNoTracking().ToListAsync(), s => s.SourceKey == $"doc:{library.SpecId}");
        Assert.DoesNotContain(await db.WikiPageSources.AsNoTracking().ToListAsync(), s => s.DocumentId == library.SpecId);
        // The home page no longer lists them.
        Assert.DoesNotContain("order-checkout", WikiText.LinkedSlugs((await library.PageAsync(WikiBuilder.OverviewSlug)).Content));
    }

    [Fact]
    public async Task Run_NewDocumentAdded_ExtractsOnlyItAndKeepsExistingPages()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        var checkout = await library.PageAsync("order-checkout");
        await using (var db = library.Open())
        {
            db.Documents.Add(new KnowledgeDocument
            {
                OwnerId = WikiLibrary.Owner, FolderId = library.ParentId, Title = "Pricing guide", Text = new DocumentText { Content = "Pricing." },
                Chunks = [new DocumentChunk { Index = 0, Content = "Pricing." }]
            });
            await db.SaveChangesAsync();
        }
        library.Model.Reset();

        var (_, stats) = await library.BuildAsync();

        Assert.Equal(1, stats.SourcesAdded);
        Assert.Equal(["Pricing guide"], library.Model.ExtractedTitles);
        Assert.Equal(checkout.Id, (await library.PageAsync("order-checkout")).Id);
        Assert.Equal(["source-pricing-guide"], (await PagesAsync(library)).Select(p => p.Slug).Where(s => s.Contains("pricing")));
    }

    [Fact]
    public async Task Run_OneExtractionFails_CompletesAndTriesThatSourceAgainNextTime()
    {
        using var library = new WikiLibrary();
        library.Model.FailingTitles.Add("README.md");

        var (first, firstStats) = await library.BuildAsync();

        Assert.Equal(WikiRunStatus.Completed, first.Status);
        Assert.Equal(1, firstStats.SourcesFailed);
        var slugs = (await PagesAsync(library)).Select(p => p.Slug).ToArray();
        Assert.Contains("order-checkout", slugs);
        Assert.DoesNotContain("source-readme-md", slugs);

        library.Model.FailingTitles.Clear();
        library.Model.Reset();
        var (_, secondStats) = await library.BuildAsync();

        Assert.Equal(["README.md"], library.Model.ExtractedTitles);
        Assert.Equal(0, library.Model.Calls(nameof(WikiModel.ExtractModuleAsync)));
        Assert.Equal(0, secondStats.SourcesFailed);
        Assert.Contains("source-readme-md", (await PagesAsync(library)).Select(p => p.Slug));
    }

    [Fact]
    public async Task Run_FullMode_ExtractsEverythingAgain()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        library.Model.Reset();

        var (run, stats) = await library.BuildAsync(WikiRunModes.Full);

        Assert.Equal(WikiRunStatus.Completed, run.Status);
        Assert.Equal(["Order spec", "README.md"], library.Model.ExtractedTitles.Order(StringComparer.Ordinal));
        Assert.Equal(["src/Billing", "src/Orders"], library.Model.ExtractedModules.Order(StringComparer.Ordinal));
        Assert.Equal(4, stats.SourcesExtracted);
        Assert.Equal(2, library.Model.Calls(nameof(WikiModel.FindImplementationAsync)));
    }

    [Fact]
    public async Task Run_LintMode_ChecksLinksWithoutAnyModelCall()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        library.Model.Reset();
        await using (var db = library.Open())
        {
            var page = await db.WikiPages.SingleAsync(p => p.Slug == "free-shipping");
            page.Content += " See [[nowhere]] and (doc:999999).";
            await db.SaveChangesAsync();
        }

        var (run, stats) = await library.BuildAsync(WikiRunModes.Lint);

        Assert.Equal(WikiRunStatus.Completed, run.Status);
        Assert.Equal(0, library.Model.Total);
        Assert.Equal((1, 1), (stats.BrokenLinks, stats.UnresolvedCitations));
    }
}
