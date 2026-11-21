using AgentRag.Api.Services;

public class WikiTextTests
{
    [Theory]
    [InlineData("Règles de gestion", "regles-de-gestion")]
    [InlineData("  État   civil -- (v2)!  ", "etat-civil-v2")]
    [InlineData("OrderService", "orderservice")]
    [InlineData("!!!", "page")]
    [InlineData("", "page")]
    public void Slug_Title_IsLowerCaseWithoutAccentsOrPunctuation(string title, string expected) =>
        Assert.Equal(expected, WikiText.Slug(title));

    [Fact]
    public void Slug_LongTitle_IsCutAtTheMaximumWithoutTrailingSeparator()
    {
        var slug = WikiText.Slug(string.Join(' ', Enumerable.Repeat("word", 50)), 20);
        Assert.True(slug.Length <= 20);
        Assert.False(slug.EndsWith('-'));
        Assert.StartsWith("word-word", slug);
    }

    [Fact]
    public void LinkedSlugs_PipeAndTitleLinks_AreSlugifiedInOrderWithoutDuplicates()
    {
        var content = "See [[order-checkout|the checkout]], [[Règles de gestion]] and [[order-checkout]] again; [[Order Checkout|same]]. Not [a link] nor [[ ]].";

        Assert.Equal(["order-checkout", "regles-de-gestion"], WikiText.LinkedSlugs(content));
    }

    [Theory]
    [InlineData("calculateLmnpSimulation", true)]
    [InlineData("OrderService", true)]
    [InlineData("TAX_RATE", true)]
    [InlineData("orders.total()", true)]
    [InlineData("Pagination", false)]
    [InlineData("LMNP", false)]
    [InlineData("Order checkout", false)]
    [InlineData("Réduction Censi-Bouvard", false)]
    public void IsCodeIdentifier_RecognisesNamesWrittenAsCode(string name, bool expected) =>
        Assert.Equal(expected, WikiText.IsCodeIdentifier(name));

    [Fact]
    public void LinkedSlugs_NoLinks_IsEmpty() => Assert.Empty(WikiText.LinkedSlugs("Plain text with [single] brackets."));

    [Fact]
    public void ReplaceLinks_ReplacementOrNull_RewritesOrKeepsLabel()
    {
        var content = "A [[known|Known page]], a [[Missing One]] and a [[known]].";

        var result = WikiText.ReplaceLinks(content, (slug, label) => slug == "known" ? $"[[{slug}|{label}]]" : null);

        Assert.Equal("A [[known|Known page]], a Missing One and a [[known|known]].", result);
    }

    [Fact]
    public void ReplaceLinks_ReceivesSlugAndTrimmedLabel()
    {
        var seen = new List<(string, string)>();
        WikiText.ReplaceLinks("[[ Règles de gestion | Les règles ]]", (slug, label) => { seen.Add((slug, label)); return "x"; });
        Assert.Equal([("regles-de-gestion", "Les règles")], seen);
    }

    [Fact]
    public void Citations_DocumentCitation_RoundTripsDocumentAndPassage()
    {
        var content = $"Free shipping {WikiText.DocumentCitation("Order [spec]", 42, 7)} and {WikiText.DocumentCitation("Order spec", 43, null)}.";

        Assert.Equal([new WikiCitation(42, 7, null, null, null), new WikiCitation(43, null, null, null, null)], WikiText.Citations(content));
    }

    [Theory]
    [InlineData("src/Orders/OrderService.cs", 12)]
    [InlineData("src/My Folder/Order (copy) #1.cs", 3)]
    [InlineData("docs/100% sure/é à.md", null)]
    [InlineData("README.md", null)]
    public void Citations_CodeCitation_RoundTripsRepositoryPathAndLine(string path, int? line)
    {
        var citation = WikiText.CodeCitation(5, path, line);

        var found = Assert.Single(WikiText.Citations($"The service computes totals {citation}."));
        Assert.Equal(new WikiCitation(null, null, 5, path, line), found);
    }

    [Fact]
    public void Citations_SeveralKinds_AreReturnedInOrder()
    {
        var content = WikiText.CodeCitation(1, "a b/c.cs", 9) + " then " + WikiText.DocumentCitation("Spec", 3, 2) + " then (not a citation)";

        var found = WikiText.Citations(content);

        Assert.Equal(2, found.Count);
        Assert.Equal("a b/c.cs", found[0].Path);
        Assert.Equal(3, found[1].DocumentId);
    }

    [Fact]
    public void Truncate_LongText_NeverSplitsASurrogatePair()
    {
        var text = "ab" + "🙂" + "cd";
        var cut = WikiText.Truncate(text, 4);

        Assert.True(cut.Length <= 4);
        Assert.False(char.IsHighSurrogate(cut[^2]));
        Assert.EndsWith("…", cut);
        Assert.Equal("short", WikiText.Truncate("short", 10));
        Assert.Equal("", WikiText.Truncate(null, 10));
    }
}
