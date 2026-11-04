using AgentRag.Api.Services;
using AgentRag.Api.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

public class RagServiceTests
{
    [Fact]
    public void SearchChunks_TranslatesCosineRankingAndOwnerFilterToPostgres()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", o => o.UseVector()).Options);
        var rag = new RagService(db, null!);

        var sql = rag.SearchChunks("owner-123", new Vector(new float[] { 1, 0, 0 }), 4).ToQueryString();

        Assert.Contains("<=>", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("WHERE", sql);
        Assert.Contains("\"OwnerId\" =", sql);
        Assert.Contains("owner-123", sql);
        Assert.Equal("vector", db.Model.FindEntityType(typeof(DocumentChunk))!
            .FindProperty(nameof(DocumentChunk.Embedding))!.GetColumnType());
        Assert.NotNull(db.Model.FindEntityType(typeof(ApplicationUser)));
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
