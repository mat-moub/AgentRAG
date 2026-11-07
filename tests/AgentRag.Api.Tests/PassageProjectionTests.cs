using AgentRag.Api.Services;

public class PassageProjectionTests
{
    [Fact]
    public void Project_Empty_ReturnsNoPoints() => Assert.Empty(PassageProjection.Project([]));

    [Fact]
    public void Project_IdenticalVectors_ReturnsFiniteOrigin()
    {
        var points = PassageProjection.Project([[1, 2, 3], [1, 2, 3]]);
        Assert.All(points.SelectMany(p => p), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Project_SeparatesDifferentVectorsAndCentersMap()
    {
        var points = PassageProjection.Project([[1, 0, 0], [0, 1, 0], [-1, 0, 0]]);
        Assert.All(points.SelectMany(p => p), value => Assert.True(double.IsFinite(value)));
        Assert.True(Math.Abs(points.Sum(p => p[0])) < 1e-8);
        Assert.True(Math.Abs(points.Sum(p => p[1])) < 1e-8);
        Assert.True(Math.Abs(points[0][0] - points[2][0]) > 0.5);
    }
}
