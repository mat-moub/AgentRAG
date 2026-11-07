namespace AgentRag.Api.Services;

public static class PassageProjection
{
    // Two principal components of centered, normalized embeddings, using power iteration.
    public static double[][] Project(float[][] vectors)
    {
        if (vectors.Length == 0) return [];
        var dimensions = vectors[0].Length;
        var rows = vectors.Select(v =>
        {
            var norm = Math.Sqrt(v.Sum(x => (double)x * x));
            return v.Select(x => norm == 0 ? 0 : x / norm).ToArray();
        }).ToArray();
        var mean = Enumerable.Range(0, dimensions).Select(j => rows.Average(r => r[j])).ToArray();
        foreach (var row in rows)
            for (var j = 0; j < dimensions; j++) row[j] -= mean[j];
        var axes = new List<double[]>();
        for (var axis = 0; axis < 2; axis++)
        {
            var direction = Enumerable.Range(0, dimensions).Select(j => Math.Sin((j + 1) * (axis + 1.7))).ToArray();
            for (var iteration = 0; iteration < 30; iteration++)
            {
                var next = new double[dimensions];
                foreach (var row in rows)
                {
                    var score = Dot(row, direction);
                    for (var j = 0; j < dimensions; j++) next[j] += row[j] * score;
                }
                foreach (var previous in axes)
                {
                    var overlap = Dot(next, previous);
                    for (var j = 0; j < dimensions; j++) next[j] -= overlap * previous[j];
                }
                var norm = Math.Sqrt(Dot(next, next));
                direction = next.Select(x => norm < 1e-12 ? 0 : x / norm).ToArray();
            }
            axes.Add(direction);
        }
        return rows.Select(row => axes.Select(axis => Dot(row, axis)).ToArray()).ToArray();
    }

    private static double Dot(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();
}
