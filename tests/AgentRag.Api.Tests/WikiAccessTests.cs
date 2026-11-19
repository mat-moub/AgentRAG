using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;

public class WikiAccessTests
{
    private static async Task<T> Async<T>(WikiLibrary library, Func<WikiService, Task<T>> action)
    {
        await using var db = library.Open();
        return await action(library.Service(db));
    }

    private static async Task<int> StatusOfAsync(Func<Task> action) => (await Assert.ThrowsAsync<LibraryException>(action)).Status;

    private static async Task<WikiLibrary> BuiltAsync()
    {
        var library = new WikiLibrary();
        await library.BuildAsync();
        return library;
    }

    [Fact]
    public async Task Reader_CanReadPagesWithLinksAndSources_ButCannotStartARun()
    {
        using var library = await BuiltAsync();

        var wiki = await Async(library, s => s.GetAsync(WikiLibrary.Reader, library.WikiId, default));
        var pages = await Async(library, s => s.ListPagesAsync(WikiLibrary.Reader, library.WikiId, default));
        var checkout = await Async(library, s => s.ReadPageAsync(WikiLibrary.Reader, library.WikiId, "order-checkout", default));

        Assert.False(wiki.CanRefresh);
        Assert.False(wiki.CanDelete);
        Assert.Equal("Shop", wiki.Folder);
        Assert.Equal(pages.Count, wiki.Pages);
        Assert.Contains(pages, p => p.Slug == WikiBuilder.OverviewSlug);
        Assert.Equal("Order checkout", checkout.Title);
        Assert.Contains(checkout.Backlinks, l => l.Link == WikiLinkKinds.Implements && l.Slug.StartsWith("module-", StringComparison.Ordinal));
        Assert.Equal("Order spec", Assert.Single(checkout.Sources).Title);
        Assert.Equal(403, await StatusOfAsync(() => Async(library, s => s.StartRunAsync(WikiLibrary.Reader, library.WikiId, null, default))));
        Assert.Equal(403, await StatusOfAsync(() => Async(library, s => s.UpdateAsync(WikiLibrary.Reader, library.WikiId, "fr", null, default))));
        Assert.Equal(403, await StatusOfAsync(() => Async(library, s => s.CancelRunAsync(WikiLibrary.Reader, library.WikiId, 1, default))));
        await using var db = library.Open();
        Assert.Equal(1, await db.WikiRuns.CountAsync());
    }

    [Fact]
    public async Task Writer_CanStartARun_AndOwnerAloneCanDelete()
    {
        using var library = await BuiltAsync();

        var run = await Async(library, s => s.StartRunAsync(WikiLibrary.Writer, library.WikiId, "lint", default));

        Assert.Equal((WikiRunStatus.Queued, WikiRunModes.Lint), (run.Status, run.Mode));
        Assert.Equal(403, await StatusOfAsync(() => Async(library, async s => { await s.DeleteAsync(WikiLibrary.Writer, library.WikiId, default); return 0; })));
    }

    [Fact]
    public async Task Stranger_GetsNotFoundForEverythingAboutTheWiki()
    {
        using var library = await BuiltAsync();
        var id = library.WikiId;
        var who = WikiLibrary.Stranger;

        Assert.Empty(await Async(library, s => s.ListAsync(who, default)));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.GetAsync(who, id, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ReadPageAsync(who, id, "order-checkout", default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ListPagesAsync(who, id, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.SearchAsync(who, id, "order", 5, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.GraphAsync(who, id, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.TraceAsync(who, id, "order-checkout", null, null, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.StatusAsync(who, id, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.StartRunAsync(who, id, null, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ListRunsAsync(who, id, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ResolveRepositoryFileAsync(who, library.RepositoryId, "README.md", default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.GetAsync(WikiLibrary.Reader, 9999, default))));
    }

    [Fact]
    public async Task Member_OfTheFinanceFolderOnly_CannotSeeTheWikiOfItsParent()
    {
        using var library = await BuiltAsync();

        Assert.Empty(await Async(library, s => s.ListAsync(WikiLibrary.Carol, default)));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.GetAsync(WikiLibrary.Carol, library.WikiId, default))));
    }

    [Fact]
    public async Task Reader_LosesAccess_WhenTheFolderIsNoLongerSharedWithThem()
    {
        using var library = await BuiltAsync();
        await using (var db = library.Open())
        {
            db.FolderMembers.Remove(await db.FolderMembers.SingleAsync(m => m.FolderId == library.ParentId && m.UserId == WikiLibrary.Reader));
            await db.SaveChangesAsync();
        }

        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ReadPageAsync(WikiLibrary.Reader, library.WikiId, "order-checkout", default))));
    }

    [Fact]
    public async Task Page_IsHiddenOnceOneOfItsSourcesIsNoLongerReadable_AndTheOverviewWithIt()
    {
        using var library = await BuiltAsync();
        await using (var db = library.Open())
        {
            // The spec moves to a private folder of its owner: the reader keeps the folder but loses that document.
            (await db.Documents.SingleAsync(d => d.Id == library.SpecId)).FolderId = library.HiddenFolderId;
            await db.SaveChangesAsync();
        }

        var reader = await Async(library, s => s.ListPagesAsync(WikiLibrary.Reader, library.WikiId, default));
        var owner = await Async(library, s => s.ListPagesAsync(WikiLibrary.Owner, library.WikiId, default));

        var hidden = new[] { "order-checkout", "free-shipping", "source-order-spec", WikiBuilder.OverviewSlug };
        Assert.All(hidden, slug => Assert.Contains(owner, p => p.Slug == slug));
        Assert.All(hidden, slug => Assert.DoesNotContain(reader, p => p.Slug == slug));
        Assert.Contains(reader, p => p.Slug == "module-shop-src-orders");
        Assert.Equal(owner.Count - hidden.Length, reader.Count);
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ReadPageAsync(WikiLibrary.Reader, library.WikiId, "order-checkout", default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ReadPageAsync(WikiLibrary.Reader, library.WikiId, WikiBuilder.OverviewSlug, default))));
        Assert.Equal(reader.Count, (await Async(library, s => s.GetAsync(WikiLibrary.Reader, library.WikiId, default))).Pages);
        // The owner is unaffected, and so are the links the reader still sees.
        Assert.Equal("Order checkout", (await Async(library, s => s.ReadPageAsync(WikiLibrary.Owner, library.WikiId, "order-checkout", default))).Title);
        var orders = await Async(library, s => s.ReadPageAsync(WikiLibrary.Reader, library.WikiId, "module-shop-src-orders", default));
        Assert.DoesNotContain(orders.Links, l => l.Slug is "order-checkout" or "free-shipping");
    }

    [Fact]
    public async Task HiddenPages_AreAbsentFromSearchGraphAndTrace()
    {
        using var library = await BuiltAsync();
        await using (var db = library.Open())
        {
            (await db.Documents.SingleAsync(d => d.Id == library.SpecId)).FolderId = library.HiddenFolderId;
            await db.SaveChangesAsync();
        }
        var (id, who) = (library.WikiId, WikiLibrary.Reader);

        var hits = await Async(library, s => s.SearchAsync(who, id, "checkout", 10, default));
        var graph = await Async(library, s => s.GraphAsync(who, id, default, includeSources: true));
        var trace = await Async(library, s => s.TraceAsync(who, id, null, library.SpecId, null, default));
        var byFile = await Async(library, s => s.TraceAsync(who, id, null, null, WikiLibrary.OrderServicePath, default));

        Assert.DoesNotContain(hits, h => h.Slug is "order-checkout" or "source-order-spec");
        Assert.DoesNotContain(graph.Nodes, n => n.Id is "order-checkout" or "free-shipping" or WikiBuilder.OverviewSlug);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == $"doc:{library.SpecId}");
        Assert.Empty(trace.Pages);
        Assert.DoesNotContain(byFile.Functional, l => l.Slug is "order-checkout" or "free-shipping");
        Assert.DoesNotContain(trace.Sources, s => s.DocumentId == library.SpecId);
    }

    [Fact]
    public async Task StartRun_WhileAnotherIsQueuedOrRunning_IsRejectedWith409()
    {
        using var library = await BuiltAsync();

        var first = await Async(library, s => s.StartRunAsync(WikiLibrary.Owner, library.WikiId, "incremental", default));
        var queued = await StatusOfAsync(() => Async(library, s => s.StartRunAsync(WikiLibrary.Owner, library.WikiId, null, default)));
        await using (var db = library.Open())
        {
            (await db.WikiRuns.SingleAsync(r => r.Id == first.Id)).Status = WikiRunStatus.Running;
            await db.SaveChangesAsync();
        }
        var running = await StatusOfAsync(() => Async(library, s => s.StartRunAsync(WikiLibrary.Writer, library.WikiId, "full", default)));

        Assert.Equal((409, 409), (queued, running));
        await using (var db = library.Open())
        {
            (await db.WikiRuns.SingleAsync(r => r.Id == first.Id)).Status = WikiRunStatus.Failed;
            await db.SaveChangesAsync();
        }
        Assert.Equal(WikiRunStatus.Queued, (await Async(library, s => s.StartRunAsync(WikiLibrary.Owner, library.WikiId, null, default))).Status);
    }

    [Fact]
    public async Task StartRun_UnknownMode_IsRejectedWith400() =>
        Assert.Equal(400, await StatusOfAsync(async () =>
        {
            using var library = new WikiLibrary();
            await Async(library, s => s.StartRunAsync(WikiLibrary.Owner, library.WikiId, "everything", default));
        }));

    [Fact]
    public async Task Create_SecondWikiOnTheSameFolder_IsRejectedWith409AndRequiresWriteAccess()
    {
        using var library = await BuiltAsync();

        Assert.Equal(409, await StatusOfAsync(() => Async(library, s => s.CreateAsync(WikiLibrary.Owner, library.ParentId, null, null, default))));
        Assert.Equal(403, await StatusOfAsync(() => Async(library, s => s.CreateAsync(WikiLibrary.Reader, library.RepoFolderId, null, null, default))));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.CreateAsync(WikiLibrary.Stranger, library.RepoFolderId, null, null, default))));
        var created = await Async(library, s => s.CreateAsync(WikiLibrary.Owner, library.RepoFolderId, " fr ", "  Use French.  ", default));
        Assert.Equal(("fr", "Use French.", 0), (created.Language, created.Guidelines, created.Pages));
    }

    [Fact]
    public async Task ResolveRepositoryFile_ReturnsTheCurrentDocumentForReaders()
    {
        using var library = await BuiltAsync();
        var expected = await library.DocumentIdAsync(WikiLibrary.OrderServicePath);

        Assert.Equal(expected, await Async(library, s => s.ResolveRepositoryFileAsync(WikiLibrary.Reader, library.RepositoryId, WikiLibrary.OrderServicePath, default)));
        Assert.Equal(404, await StatusOfAsync(() => Async(library, s => s.ResolveRepositoryFileAsync(WikiLibrary.Reader, library.RepositoryId, "missing.cs", default))));
    }

    [Fact]
    public async Task Search_MatchesTitleSummaryAliasAndContent_RankingTitlesFirst()
    {
        using var library = await BuiltAsync();
        var (id, who) = (library.WikiId, WikiLibrary.Reader);

        var byTitle = await Async(library, s => s.SearchAsync(who, id, "ORDER checkout", 10, default));
        var byAlias = await Async(library, s => s.SearchAsync(who, id, "checkout", 10, default));
        var byContent = await Async(library, s => s.SearchAsync(who, id, "confirmed after payment", 10, default));
        var none = await Async(library, s => s.SearchAsync(who, id, "zebra", 10, default));

        Assert.Equal("order-checkout", byTitle[0].Slug);
        Assert.Equal("literal", byTitle[0].Match);
        Assert.Contains(byAlias, h => h.Slug == "order-checkout");
        var hit = Assert.Single(byContent, h => h.Slug == "order-checkout");
        Assert.Contains("confirmed after payment", hit.Excerpt);
        Assert.Empty(none);
        var filtered = await Async(library, s => s.ListPagesAsync(who, id, default, kind: WikiPageKinds.Rule, query: "shipping"));
        Assert.Equal(["free-shipping"], filtered.Select(p => p.Slug));
    }

    [Fact]
    public async Task Status_CountsPendingAndExcludedSources()
    {
        using var library = await BuiltAsync();

        var clean = await Async(library, s => s.StatusAsync(WikiLibrary.Reader, library.WikiId, default));
        await library.ReplaceRepositoryFileAsync(WikiLibrary.InvoicePath, "class Changed {}", "sha-new");
        await library.DeleteDocumentAsync(library.SpecId);
        var dirty = await Async(library, s => s.StatusAsync(WikiLibrary.Reader, library.WikiId, default));

        Assert.Equal((0, 1), (clean.PendingSources, clean.ExcludedSources));
        Assert.Equal(5, clean.Sources);
        Assert.Equal(2, dirty.PendingSources);
    }
}
