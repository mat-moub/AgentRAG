using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;

public class WikiGraphTests
{
    /// <summary>
    /// index -mentions-> order-checkout, index -mentions-> loose;
    /// order-checkout -related-> free-shipping -related-> pricing;
    /// orders-module -implements-> order-checkout; orders-service -part-of-> orders-module.
    /// </summary>
    private static async Task<WikiLibrary> GraphLibraryAsync()
    {
        var library = new WikiLibrary();
        await using var db = library.Open();
        WikiPage Page(string slug, string kind, params int[] documents) => new()
        {
            WikiId = library.WikiId, Slug = slug, Title = slug, Kind = kind, Summary = slug + " summary",
            Sources = [.. documents.Select(d => new WikiPageSource { DocumentId = d, SourceKey = "doc:" + d })]
        };
        var code = await library.DocumentIdAsync(WikiLibrary.OrderServicePath);
        var pages = new[]
        {
            Page("index", WikiPageKinds.Overview), Page("order-checkout", WikiPageKinds.Feature, library.SpecId),
            Page("free-shipping", WikiPageKinds.Rule, library.SpecId), Page("pricing", WikiPageKinds.Concept),
            Page("orders-module", WikiPageKinds.Module, code), Page("orders-service", WikiPageKinds.Component, code), Page("loose", WikiPageKinds.Concept)
        };
        db.WikiPages.AddRange(pages);
        await db.SaveChangesAsync();
        int Id(string slug) => pages.Single(p => p.Slug == slug).Id;
        void Link(string from, string to, string kind) => db.WikiLinks.Add(new WikiLink { WikiId = library.WikiId, FromPageId = Id(from), ToPageId = Id(to), Kind = kind });
        Link("index", "order-checkout", WikiLinkKinds.Mentions);
        Link("index", "loose", WikiLinkKinds.Mentions);
        Link("order-checkout", "free-shipping", WikiLinkKinds.Related);
        Link("free-shipping", "pricing", WikiLinkKinds.Related);
        Link("orders-module", "order-checkout", WikiLinkKinds.Implements);
        Link("orders-service", "orders-module", WikiLinkKinds.PartOf);
        await db.SaveChangesAsync();
        return library;
    }

    private static async Task<WikiGraph> GraphAsync(WikiLibrary library, string? center = null, int depth = 1, string[]? kinds = null, bool sources = false)
    {
        await using var db = library.Open();
        return await library.Service(db).GraphAsync(WikiLibrary.Reader, library.WikiId, default, center, depth, kinds, sources);
    }

    private static string[] Slugs(WikiGraph graph) => graph.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal).ToArray();
    private static string[] Edges(WikiGraph graph) => graph.Edges.Select(e => $"{e.Source}>{e.Target}:{e.Kind}").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Graph_WholeWiki_ReturnsEveryPageAndLinkWithDegrees()
    {
        using var library = await GraphLibraryAsync();

        var graph = await GraphAsync(library);

        Assert.Equal(["free-shipping", "index", "loose", "order-checkout", "orders-module", "orders-service", "pricing"], Slugs(graph));
        Assert.Equal(["free-shipping>pricing:related", "index>loose:mentions", "index>order-checkout:mentions", "order-checkout>free-shipping:related",
            "orders-module>order-checkout:implements", "orders-service>orders-module:part-of"], Edges(graph));
        Assert.False(graph.Truncated);
        Assert.Equal(3, graph.Nodes.Single(n => n.Id == "order-checkout").Degree);
        Assert.Equal("feature", graph.Nodes.Single(n => n.Id == "order-checkout").Kind);
    }

    [Fact]
    public async Task Graph_CenterWithDepthOne_ReturnsDirectNeighboursInBothDirections()
    {
        using var library = await GraphLibraryAsync();

        var graph = await GraphAsync(library, "order-checkout", depth: 1);

        Assert.Equal(["free-shipping", "index", "order-checkout", "orders-module"], Slugs(graph));
        Assert.Equal(["index>order-checkout:mentions", "order-checkout>free-shipping:related", "orders-module>order-checkout:implements"], Edges(graph));
    }

    [Fact]
    public async Task Graph_CenterWithDepthTwo_WalksFurtherButNeverThroughTheOverview()
    {
        using var library = await GraphLibraryAsync();

        var graph = await GraphAsync(library, "Order Checkout", depth: 2);

        Assert.Equal(["free-shipping", "index", "order-checkout", "orders-module", "orders-service", "pricing"], Slugs(graph));
        Assert.DoesNotContain("loose", Slugs(graph));
        Assert.Equal(["free-shipping>pricing:related", "index>order-checkout:mentions", "order-checkout>free-shipping:related",
            "orders-module>order-checkout:implements", "orders-service>orders-module:part-of"], Edges(graph));
    }

    [Fact]
    public async Task Graph_DepthBelowOne_IsTreatedAsOne_AndUnknownCenterIsNotFound()
    {
        using var library = await GraphLibraryAsync();

        Assert.Equal(Slugs(await GraphAsync(library, "order-checkout", depth: 1)), Slugs(await GraphAsync(library, "order-checkout", depth: 0)));
        var error = await Assert.ThrowsAsync<LibraryException>(() => GraphAsync(library, "unknown-page"));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Graph_KindsFilter_KeepsOnlyThoseKindsAndTheLinksBetweenThem()
    {
        using var library = await GraphLibraryAsync();

        var graph = await GraphAsync(library, kinds: [WikiPageKinds.Feature, WikiPageKinds.Rule]);

        Assert.Equal(["free-shipping", "order-checkout"], Slugs(graph));
        Assert.Equal(["order-checkout>free-shipping:related"], Edges(graph));
        var technical = await GraphAsync(library, "order-checkout", depth: 2, kinds: [WikiPageKinds.Module, WikiPageKinds.Component]);
        Assert.Equal(["orders-module", "orders-service"], Slugs(technical));
    }

    [Fact]
    public async Task Graph_IncludeSources_AddsDocumentNodesWithSourceEdges()
    {
        using var library = await GraphLibraryAsync();
        var code = await library.DocumentIdAsync(WikiLibrary.OrderServicePath);

        var graph = await GraphAsync(library, sources: true);

        var spec = graph.Nodes.Single(n => n.Id == $"doc:{library.SpecId}");
        Assert.Equal(("Order spec", "document", 2), (spec.Label, spec.Kind, spec.Degree));
        Assert.Equal(("code-file", 2), (graph.Nodes.Single(n => n.Id == $"doc:{code}").Kind, graph.Nodes.Single(n => n.Id == $"doc:{code}").Degree));
        Assert.Equal(["free-shipping>doc:" + library.SpecId + ":source", "order-checkout>doc:" + library.SpecId + ":source",
            "orders-module>doc:" + code + ":source", "orders-service>doc:" + code + ":source"], Edges(graph).Where(e => e.EndsWith(":source")));
        Assert.DoesNotContain((await GraphAsync(library)).Nodes, n => n.Id.StartsWith("doc:"));
    }

    [Fact]
    public async Task Trace_ByFilePath_ReturnsTheModulePageAndTheFeaturesItImplements()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        await using var db = library.Open();

        var trace = await library.Service(db).TraceAsync(WikiLibrary.Reader, library.WikiId, null, null, "\\" + WikiLibrary.OrderServicePath.Replace('/', '\\'), default);

        Assert.Contains(trace.Pages, p => p.Slug == "module-shop-src-orders");
        Assert.Contains(trace.Pages, p => p.Slug == "orderservice");
        var functional = trace.Functional.ToDictionary(l => l.Slug);
        Assert.Equal(["free-shipping", "order-checkout"], functional.Keys.Order(StringComparer.Ordinal));
        Assert.All(functional.Values, l => Assert.Equal(WikiLinkKinds.Implements, l.Link));
        Assert.Contains("repo:", functional["free-shipping"].Note);
        Assert.Contains(trace.Sources, s => s.Path == WikiLibrary.OrderServicePath);
    }

    [Fact]
    public async Task Trace_ByFeature_ReturnsTheCodeThatImplementsIt()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        await using var db = library.Open();

        var trace = await library.Service(db).TraceAsync(WikiLibrary.Reader, library.WikiId, "Order checkout", null, null, default);

        Assert.Equal("order-checkout", Assert.Single(trace.Pages).Slug);
        var module = Assert.Single(trace.Code, l => l.Link == WikiLinkKinds.Implements);
        Assert.Equal(("module-shop-src-orders", WikiPageKinds.Module, "Orders are created here."), (module.Slug, module.Kind, module.Note));
        Assert.Contains(trace.Functional, l => l.Slug == "free-shipping");
    }

    [Fact]
    public async Task Trace_ByDocumentOrWithoutAnyStart_ReturnsItsPagesOrIsRejected()
    {
        using var library = new WikiLibrary();
        await library.BuildAsync();
        await using var db = library.Open();
        var service = library.Service(db);

        var bySpec = await service.TraceAsync(WikiLibrary.Reader, library.WikiId, null, library.SpecId, null, default);
        var error = await Assert.ThrowsAsync<LibraryException>(() => service.TraceAsync(WikiLibrary.Reader, library.WikiId, null, null, null, default));
        var other = await service.TraceAsync(WikiLibrary.Reader, library.WikiId, null, null, "Service.cs", default);

        Assert.Equal(new[] { "order-checkout", "free-shipping", "source-order-spec" }.Order(StringComparer.Ordinal),bySpec.Pages.Select(p => p.Slug).Order(StringComparer.Ordinal));
        Assert.Equal(400, error.Status);
        Assert.Empty(other.Pages);
    }
}
