using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Pgvector;

public class RagServiceTests
{
    [Fact]
    public void SearchChunks_TranslatesCosineRankingAndOwnerFilterToPostgres()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options);
        var rag = new RagService(db, null!);

        var sql = rag.SearchChunks("owner-123", new HalfVector(new Half[] { (Half)1, (Half)0, (Half)0 }), 4).ToQueryString();

        Assert.Contains("<=>", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("\"OwnerId\" =", sql);
        Assert.Contains("owner-123", sql);
        Assert.Contains("\"DocumentId\"", sql);
        Assert.Contains("\"Index\" + 1", sql);
        Assert.Contains("\"Embedding\" IS NOT NULL", sql);
        Assert.Equal("halfvec(512)", db.Model.FindEntityType(typeof(DocumentChunk))!
            .FindProperty(nameof(DocumentChunk.Embedding))!.GetColumnType());
        Assert.NotNull(db.Model.FindEntityType(typeof(ApplicationUser)));
    }

    private static AppDbContext Postgres() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options);

    [Fact]
    public void SearchChunks_IndexedSearch_OrdersByDistanceOnlySoHnswIndexApplies()
    {
        using var db = Postgres();
        var rag = new RagService(db, null!);
        var query = new HalfVector(new Half[] { (Half)1, (Half)0, (Half)0 });

        // The search passes accessible document IDs as a plain list: with a subquery PostgreSQL sorts every passage instead.
        var indexed = rag.SearchChunks(new[] { 4, 5 }, query, 4).ToQueryString();
        var exact = rag.SearchChunks(new[] { 4, 5 }, query, 4, exact: true).ToQueryString();
        Assert.Contains("\"DocumentId\" = ANY (", indexed);
        Assert.DoesNotContain(" IN (", indexed);

        static string[] Rankings(string sql) => sql.Split('\n').Where(line => line.Contains("ORDER BY") && line.Contains("<=>")).ToArray();
        Assert.NotEmpty(Rankings(indexed));
        Assert.All(Rankings(indexed), line => Assert.DoesNotContain("\"Id\"", line));
        Assert.All(Rankings(exact), line => Assert.Contains("\"Id\"", line));
        var index = Assert.Single(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(DocumentChunk))!.GetIndexes(),
            i => i.Properties.Single().Name == nameof(DocumentChunk.Embedding));
        Assert.Equal("hnsw", index.GetMethod());
        Assert.Equal(new[] { "halfvec_cosine_ops" }, index.GetOperators());
    }

    [Fact]
    public void PassageOverlap_HasNoDatabaseDefault_SoTheZeroOfCodeFilesIsStored()
    {
        using var db = Postgres();

        var property = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(KnowledgeDocument))!
            .FindProperty(nameof(KnowledgeDocument.PassageOverlap))!;

        // With a database default, EF Core omits the CLR default 0 from INSERT and PostgreSQL writes the default instead.
        Assert.Null(property.FindAnnotation(RelationalAnnotationNames.DefaultValue));
        Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
    }

    [Theory]
    [InlineData(false, false, "ILIKE")]
    [InlineData(false, true, "LIKE")]
    [InlineData(true, false, "~*")]
    [InlineData(true, true, "~")]
    public void GrepChunks_TranslatesPatternAndAccessFilterToPostgres(bool regex, bool caseSensitive, string op)
    {
        using var db = Postgres();
        var rag = new RagService(db, null!);

        var sql = rag.GrepChunks(db.Documents.Where(d => d.OwnerId == "owner-123"), "SAVS", regex, caseSensitive).ToQueryString();

        Assert.Contains($"\"Content\" {op} ", sql);
        Assert.Contains("\"OwnerId\" =", sql);
        Assert.DoesNotContain("<=>", sql);
        var index = Assert.Single(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(DocumentChunk))!.GetIndexes(),
            i => i.Properties.Single().Name == nameof(DocumentChunk.Content));
        Assert.Equal("gin", index.GetMethod());
        Assert.Equal(new[] { "gin_trgm_ops" }, index.GetOperators());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrepChunks_Regex_AnchorsAtLineStartsInsideAPassage(bool caseSensitive)
    {
        using var db = Postgres();

        var sql = new RagService(db, null!).GrepChunks(db.Documents, "^export function", regex: true, caseSensitive).ToQueryString();

        // Newline-sensitive matching: ^ and $ match at every line of a passage, not only at its start.
        Assert.Contains("(?n", sql);
        Assert.Equal((13, 15), RagService.Locate("const a = 1;\nexport function b() {}", "^export function", true, caseSensitive));
    }

    [Fact]
    public void LineAt_ReturnsTheWholeLineAroundAnIndex()
    {
        const string text = "first\r\nexport function total() {\nlast";

        Assert.Equal("export function total() {", RagService.LineAt(text, text.IndexOf("total", StringComparison.Ordinal)));
        Assert.Equal("first", RagService.LineAt(text, 0));
        Assert.Equal("last", RagService.LineAt(text, text.Length));
    }

    [Fact]
    public void DefinitionsFirst_MovesDeclarationsAheadWithinTheWindowOnly()
    {
        var matches = Enumerable.Range(1, RagService.DefinitionWindow + 2)
            .Select(i => new GrepMatch(i, $"file{i}", 1, "x", 1, i is 3 or RagService.DefinitionWindow + 2 ? true : null)).ToList();

        var ordered = RagService.DefinitionsFirst(matches).Select(m => m.DocumentId).ToList();

        Assert.Equal([3, 1, 2, 4], ordered.Take(4));
        // A declaration beyond the window keeps its place, so later pages do not change.
        Assert.Equal(RagService.DefinitionWindow + 2, ordered[^1]);
    }

    [Fact]
    public void Headings_ListsMarkdownHeadingsWithOffsets_OutsideCodeFences()
    {
        const string text = "# Engine\nIntro\n```bash\n# not a heading\n```\n## Loans\n#hashtag\n";

        var headings = RagService.Headings(text, 10);

        Assert.Equal([(1, 0, "# Engine"), (6, text.IndexOf("## Loans", StringComparison.Ordinal), "## Loans")], headings);
    }

    [Fact]
    public void GrepChunks_LiteralPattern_EscapesLikeWildcards()
    {
        Assert.Equal(@"100\% \_done\_ C:\\temp", RagService.EscapeLike(@"100% _done_ C:\temp"));
        using var db = Postgres();
        var sql = new RagService(db, null!).GrepChunks(db.Documents, "50%", regex: false, caseSensitive: false).ToQueryString();
        Assert.Contains(@"%50\%%", sql);
        Assert.Contains("ESCAPE", sql);
    }

    [Fact]
    public void Excerpt_LongPassage_CentersOnFirstMatch()
    {
        var content = new string('a', 400) + " Les SAVS contribuent " + new string('b', 400);

        var excerpt = RagService.Excerpt(content, "savs", regex: false, caseSensitive: false, radius: 20);

        Assert.Equal("…" + new string('a', 15) + " Les SAVS contribuent " + new string('b', 7) + "…", excerpt);
    }

    [Fact]
    public void Excerpt_ShortPassageOrNoLocatedMatch_StartsAtPassageStartWithoutEllipsis()
    {
        Assert.Equal("Article L. 311-4", RagService.Excerpt("Article L. 311-4", @"L\. ?311-4", regex: true, caseSensitive: false));
        // An invalid .NET pattern must not fail a search PostgreSQL already answered.
        Assert.Equal("abc…", RagService.Excerpt("abc" + new string('d', 10), "(", regex: true, caseSensitive: false, radius: 3));
    }

    [Fact]
    public void Excerpt_NeverSplitsSurrogatePairs()
    {
        var excerpt = RagService.Excerpt("🙂🙂 match 🙂🙂", "match", regex: false, caseSensitive: true, radius: 2);

        Assert.Equal("\u2026 match \u2026", excerpt);
        Assert.All(Enumerable.Range(0, excerpt.Length), i =>
            Assert.Equal(char.IsHighSurrogate(excerpt[i]), i + 1 < excerpt.Length && char.IsLowSurrogate(excerpt[i + 1])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_NonPositiveTop_ReturnsEmptyWithoutGeneratingEmbedding(int top)
    {
        var rag = new RagService(null!, null!);
        Assert.Empty(await rag.SearchAsync("owner", "query", top));
    }

    [Fact]
    public void Cosine_IdenticalVectors_IsOne() =>
        Assert.Equal(1.0, RagService.Cosine([1f, 2f, 3f], [1f, 2f, 3f]), 5);

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero() =>
        Assert.Equal(0.0, RagService.Cosine([1f, 0f], [0f, 1f]), 5);

    [Fact]
    public void Chunk_SplitsLongText()
    {
        var chunks = RagService.Chunk(new string('a', 2000), 800, 100);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 800));
    }
}

public class ReconstructTextTests
{
    [Fact]
    public void ReconstructText_PagedChunks_RebuildOriginalText()
    {
        var original = string.Concat(Enumerable.Range(0, 400).Select(i => $"Sentence {i} of a long document. "));
        var chunks = RagService.Chunk(original);
        Assert.True(chunks.Count > 3);

        var paged = string.Concat(chunks.Chunk(3).Select((page, i) => RagService.ReconstructText(page, continuation: i > 0)));

        Assert.Equal(original.Trim(), paged);
    }

    [Fact]
    public void ReconstructText_NoChunks_IsEmpty() => Assert.Equal("", RagService.ReconstructText([]));
}
