using AgentRag.Api.Data;
using AgentRag.Api.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

public class PassageSamplingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(12)]
    public void SelectIds_ProducesBoundedPerDocumentQueryWithOwnerAndFilter(int? documentId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused", postgres => postgres.UseVector())
            .Options;
        using var db = new AppDbContext(options);

        var query = PassageSampling.SelectIds(db, "test-owner", documentId).ToQueryString();

        Assert.Contains("PARTITION BY c.\"DocumentId\"", query);
        Assert.Contains("ORDER BY c.\"Index\", c.\"Id\"", query);
        Assert.Contains("d.\"OwnerId\" =", query);
        Assert.Contains("test-owner", query);
        Assert.Contains("CAST(", query);
        Assert.Contains("c.\"DocumentId\" =", query);
        Assert.Contains("LIMIT", query);
        Assert.Contains("500", query);
        if (documentId.HasValue) Assert.Contains("12", query);
        Assert.DoesNotContain("Embedding", query);
    }

    [Fact]
    public void SelectIds_ComposesWithUserOwnedChunkQuery()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused", postgres => postgres.UseVector())
            .Options;
        using var db = new AppDbContext(options);
        var ids = PassageSampling.SelectIds(db, "test-owner", null);

        var query = db.Chunks.Where(c => c.Document!.OwnerId == "test-owner" && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Embedding }).ToQueryString();

        Assert.Contains("IN (", query);
        Assert.Contains("ROW_NUMBER() OVER", query);
        Assert.Contains("\"OwnerId\"", query);
    }
}
