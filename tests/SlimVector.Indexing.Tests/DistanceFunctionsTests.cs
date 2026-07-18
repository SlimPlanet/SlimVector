using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class DistanceFunctionsTests
{
    [Fact]
    public void CosineDistanceRecognizesSameAndOppositeVectors()
    {
        float same = DistanceFunctions.Calculate([1, 0], [1, 0], DistanceMetric.Cosine);
        float opposite = DistanceFunctions.Calculate([1, 0], [-1, 0], DistanceMetric.Cosine);

        Assert.Equal(0, same, 5);
        Assert.Equal(2, opposite, 5);
    }

    [Fact]
    public void EuclideanDistanceUsesAllDimensionsIncludingSimdTail()
    {
        float[] left = Enumerable.Range(0, 37).Select(static value => (float)value).ToArray();
        float[] right = new float[left.Length];

        float expected = MathF.Sqrt(left.Sum(static value => value * value));
        Assert.Equal(expected, DistanceFunctions.Euclidean(left, right), 4);
    }

    [Fact]
    public void DotProductDistanceOrdersLargerDotFirst()
    {
        FlatVectorIndex index = new(2, DistanceMetric.DotProduct);
        index.Upsert("small", [1, 0]);
        index.Upsert("large", [2, 0]);

        IReadOnlyList<RankedResult> result = index.Search([1, 0], 2);
        Assert.Equal(["large", "small"], result.Select(static item => item.Id));
    }
}
