using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

public class WikiQueryTranslationTests
{
    private static AppDbContext Postgres() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options);

    private static LibrarySnapshot Snapshot()
    {
        var folders = new[]
        {
            new KnowledgeFolder { Id = 1, Name = "Shop", OwnerId = "owner" },
            new KnowledgeFolder { Id = 2, Name = "Code", OwnerId = "owner", ParentId = 1 }
        };
        return new("reader", folders, [new FolderMember { FolderId = 1, UserId = "reader", Role = "read" }]);
    }

    [Fact]
    public void VisiblePages_TranslatesToPostgres_HidingPagesWithUnreadableSources()
    {
        using var db = Postgres();
        var service = new WikiService(db, null!, new WikiRunQueue());

        var sql = service.VisiblePages(Snapshot(), 7).ToQueryString();

        Assert.Contains("FROM \"WikiPages\"", sql);
        Assert.Contains("-- @wikiId='7'", sql);
        Assert.Contains("\"WikiId\" = @wikiId", sql);
        Assert.Contains("\"WikiPageSources\"", sql);
        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("NOT IN (", sql);
        Assert.Contains("\"DocumentId\"", sql);
        Assert.Contains("= ANY (", sql);
        // The overview is shown only when no page is hidden.
        Assert.Contains("count(*)", sql);
    }

    [Fact]
    public void Containing_TranslatesAliasSearchOnTextArrayToPostgres()
    {
        using var db = Postgres();
        var service = new WikiService(db, null!, new WikiRunQueue());

        var sql = service.Containing(db.WikiPages, "50%_off", content: true).ToQueryString();

        Assert.Contains("ILIKE", sql);
        Assert.Contains("\"Title\" ILIKE", sql);
        Assert.Contains("\"Content\" ILIKE", sql);
        Assert.Contains("\"Aliases\"", sql);
        Assert.Contains("unnest", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ESCAPE", sql);
        // Wildcards typed by the user are escaped, not interpreted.
        Assert.Contains("50\\%\\_off", sql);
    }

    [Fact]
    public void Containing_WithoutContent_LeavesThePageBodyOutOfThePredicate()
    {
        using var db = Postgres();
        var service = new WikiService(db, null!, new WikiRunQueue());

        var sql = service.Containing(db.WikiPages, "order", content: false).ToQueryString();

        Assert.DoesNotContain("\"Content\" ILIKE", sql);
        Assert.Contains("\"Summary\" ILIKE", sql);
    }

    [Fact]
    public void TraceAndGraphQueries_TranslateToPostgres()
    {
        using var db = Postgres();
        var service = new WikiService(db, null!, new WikiRunQueue());
        var visible = service.VisiblePages(Snapshot(), 7);
        var suffix = ":src/Orders/OrderService.cs";
        var ids = new[] { 1, 2 };

#pragma warning disable CA1310 // Same query as WikiService.TraceAsync, which EF Core translates to SQL.
        var byPath = visible.Where(p => p.Sources.Any(s => s.SourceKey.EndsWith(suffix))).OrderBy(p => p.Kind).ThenBy(p => p.Title).Take(50).ToQueryString();
#pragma warning restore CA1310
        var links = db.WikiLinks.Where(l => (ids.Contains(l.FromPageId) && visible.Any(p => p.Id == l.ToPageId)) || (ids.Contains(l.ToPageId) && visible.Any(p => p.Id == l.FromPageId)))
            .Select(l => new { l.Kind, l.Note, From = l.FromPage!.Slug, To = l.ToPage!.Slug }).ToQueryString();
        var vector = visible.Where(p => p.Embedding != null).OrderBy(p => p.Embedding!.CosineDistance(new Pgvector.Vector(new float[] { 1, 0, 0 }))).Take(5).ToQueryString();

        Assert.Contains("LIKE", byPath);
        Assert.Contains("\"WikiLinks\"", links);
        Assert.Contains("<=>", vector);
    }

    [Fact]
    public void Model_WikiPageEmbeddingIsAVectorColumnWithAnIndex_OnPostgres()
    {
        using var db = Postgres();

        var page = db.Model.FindEntityType(typeof(WikiPage))!;

        Assert.Equal("vector(1536)", page.FindProperty(nameof(WikiPage.Embedding))!.GetColumnType());
        Assert.Equal("text[]", page.FindProperty(nameof(WikiPage.Aliases))!.GetColumnType());
        Assert.Contains(page.GetIndexes(), i => i.Properties.Any(p => p.Name == nameof(WikiPage.Embedding)));
    }
}
