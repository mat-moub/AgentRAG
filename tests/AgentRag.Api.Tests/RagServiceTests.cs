using AgentRag.Api.Services;

public class RagServiceTests
{
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
