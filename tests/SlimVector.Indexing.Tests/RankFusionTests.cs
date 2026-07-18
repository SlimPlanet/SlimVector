namespace SlimVector.Indexing.Tests;

public sealed class RankFusionTests
{
    [Fact]
    public void WeightedReciprocalRankCombinesRanksWithoutAddingRawScores()
    {
        RankedResult[] vector = [new("a", 0.01), new("b", 0.02)];
        RankedResult[] text = [new("b", 500), new("c", 100)];

        IReadOnlyList<HybridRankedResult> result = RankFusion.WeightedReciprocalRank(vector, text, 0.5, 0.5, 3);

        Assert.Equal("b", result[0].Id);
        Assert.Equal(2, result[0].VectorRank);
        Assert.Equal(1, result[0].TextRank);
    }
}
