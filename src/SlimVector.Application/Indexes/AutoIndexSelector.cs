using SlimVector.Application.Configuration;
using SlimVector.Domain;

namespace SlimVector.Application.Indexes;

public sealed record AutoIndexSignals
{
    public required int VectorCount { get; init; }

    public required int Dimension { get; init; }

    public required long EstimatedResidentBytes { get; init; }

    public required long AvailableMemoryBytes { get; init; }

    public required double QueriesPerSecond { get; init; }

    public required double UpdatesPerSecond { get; init; }

    public required double DeletionRatio { get; init; }

    public required double AverageQueryMilliseconds { get; init; }

    public double DiskReadMilliseconds { get; init; }

    public required VectorQuantizationKind Quantization { get; init; }
}

public sealed record AutoIndexDecision
{
    public required VectorIndexKind Current { get; init; }

    public required VectorIndexKind Selected { get; init; }

    public required IReadOnlyDictionary<VectorIndexKind, double> Scores { get; init; }

    public required string Reason { get; init; }

    public bool MigrationRecommended => Current != Selected;
}

public static class AutoIndexSelector
{
    public static AutoIndexDecision Select(
        AutoIndexSignals signals,
        VectorIndexKind current,
        AutoIndexOptions options,
        int pqSubvectorCount)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(options);
        HashSet<VectorIndexKind> allowed = options.AllowedIndexes.ToHashSet();
        Dictionary<VectorIndexKind, double> scores = [];
        double updateRatio = signals.UpdatesPerSecond /
            Math.Max(1, signals.QueriesPerSecond + signals.UpdatesPerSecond);
        double memoryRatio = signals.AvailableMemoryBytes <= 0
            ? 1
            : (double)signals.EstimatedResidentBytes / signals.AvailableMemoryBytes;
        double queryPressure = Math.Log2(2 + Math.Max(0, signals.QueriesPerSecond));
        double latencyPressure = 1 + Math.Min(4, Math.Max(0, signals.AverageQueryMilliseconds) / 25);
        double churnRatio = Math.Max(updateRatio, Math.Clamp(signals.DeletionRatio, 0, 1));
        double highChurnPenalty = churnRatio >= options.HighUpdateRatio ? 8 : 0;

        AddScore(VectorIndexKind.Flat,
            4 / (1 + (double)signals.VectorCount / Math.Max(1, options.HnswMinimumVectors)) + updateRatio * 4);
        AddScore(VectorIndexKind.Hnsw,
            signals.VectorCount >= options.HnswMinimumVectors
                ? 3 + queryPressure + latencyPressure - updateRatio * 5 - memoryRatio
                : -1);
        AddScore(VectorIndexKind.IvfFlat,
            signals.VectorCount >= options.IvfMinimumVectors
                ? 3 + queryPressure * 0.8 + signals.Dimension / 512D +
                    Math.Log2(1 + (double)signals.VectorCount / options.IvfMinimumVectors) * 3 -
                    updateRatio * 3 - memoryRatio * 5 - highChurnPenalty
                : -1);
        AddScore(VectorIndexKind.IvfPq,
            signals.VectorCount >= options.PqMinimumVectors && signals.Dimension % pqSubvectorCount == 0
                ? 3 + queryPressure * 0.6 + memoryRatio * 5 +
                    (signals.Quantization == VectorQuantizationKind.Float32 ? 0.5 : 0) - updateRatio * 2 - highChurnPenalty
                : -1);
        AddScore(VectorIndexKind.DiskAnn,
            memoryRatio >= options.DiskAnnMemoryRatio
                ? 8 + memoryRatio * 12 + queryPressure * 0.5 - updateRatio * 2 - highChurnPenalty -
                    Math.Max(0, signals.DiskReadMilliseconds - 2)
                : -1);

        VectorIndexKind selected = scores
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .First().Key;
        if (scores.TryGetValue(current, out double currentScore) &&
            currentScore >= scores[selected] * (1 - options.HysteresisRatio))
        {
            selected = current;
        }

        string reason = FormattableString.Invariant(
            $"vectors={signals.VectorCount}, dimension={signals.Dimension}, memoryRatio={memoryRatio:F3}, queryRate={signals.QueriesPerSecond:F2}, updateRatio={updateRatio:F3}, deletionRatio={signals.DeletionRatio:F3}, queryLatencyMs={signals.AverageQueryMilliseconds:F2}");
        return new AutoIndexDecision
        {
            Current = current,
            Selected = selected,
            Scores = scores,
            Reason = reason,
        };

        void AddScore(VectorIndexKind kind, double score)
        {
            if (allowed.Contains(kind))
            {
                scores.Add(kind, double.IsFinite(score) ? score : 0);
            }
        }
    }
}
