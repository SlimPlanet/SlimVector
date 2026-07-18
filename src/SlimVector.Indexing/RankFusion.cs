namespace SlimVector.Indexing;

public static class RankFusion
{
    private const double RankConstant = 60;

    public static IReadOnlyList<HybridRankedResult> WeightedReciprocalRank(
        IReadOnlyList<RankedResult> vectorResults,
        IReadOnlyList<RankedResult> textResults,
        double vectorWeight,
        double textWeight,
        int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vectorWeight);
        ArgumentOutOfRangeException.ThrowIfNegative(textWeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (vectorWeight + textWeight <= 0)
        {
            throw new ArgumentException("At least one rank weight must be positive.", nameof(vectorWeight));
        }

        Dictionary<string, MutableRank> ranks = new(StringComparer.Ordinal);
        Add(vectorResults, vectorWeight, isVector: true, ranks);
        Add(textResults, textWeight, isVector: false, ranks);

        return ranks
            .Select(static pair => new HybridRankedResult(pair.Key, pair.Value.Score, pair.Value.VectorRank, pair.Value.TextRank))
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static void Add(
        IReadOnlyList<RankedResult> results,
        double weight,
        bool isVector,
        Dictionary<string, MutableRank> ranks)
    {
        for (int index = 0; index < results.Count; index++)
        {
            RankedResult result = results[index];
            int rank = index + 1;
            if (!ranks.TryGetValue(result.Id, out MutableRank? combined))
            {
                combined = new MutableRank();
                ranks.Add(result.Id, combined);
            }

            combined.Score += weight / (RankConstant + rank);
            if (isVector)
            {
                combined.VectorRank = rank;
            }
            else
            {
                combined.TextRank = rank;
            }
        }
    }

    private sealed class MutableRank
    {
        public double Score { get; set; }

        public int? VectorRank { get; set; }

        public int? TextRank { get; set; }
    }
}
