using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Domain;

namespace SlimVector.Application.Tests;

public sealed class AutoIndexSelectorTests
{
    [Fact]
    public void SelectionUsesMemoryQueryAndUpdateSignalsWithHysteresis()
    {
        AutoIndexOptions options = new()
        {
            HnswMinimumVectors = 1_000,
            IvfMinimumVectors = 10_000,
            PqMinimumVectors = 50_000,
            DiskAnnMemoryRatio = 0.5,
            HysteresisRatio = 0.2,
        };
        AutoIndexSignals queryHeavy = Signals(
            vectorCount: 20_000,
            estimatedBytes: 100_000_000,
            availableBytes: 1_000_000_000,
            queries: 2_000,
            updates: 1);
        AutoIndexSignals memoryBound = Signals(
            vectorCount: 1_000_000,
            estimatedBytes: 8_000_000_000,
            availableBytes: 4_000_000_000,
            queries: 500,
            updates: 5);
        AutoIndexSignals updateHeavy = Signals(
            vectorCount: 20_000,
            estimatedBytes: 100_000_000,
            availableBytes: 1_000_000_000,
            queries: 5,
            updates: 2_000);

        AutoIndexDecision queryDecision = AutoIndexSelector.Select(queryHeavy, VectorIndexKind.Flat, options, 8);
        AutoIndexDecision memoryDecision = AutoIndexSelector.Select(memoryBound, VectorIndexKind.Hnsw, options, 8);
        AutoIndexDecision updateDecision = AutoIndexSelector.Select(updateHeavy, VectorIndexKind.Flat, options, 8);

        Assert.Equal(VectorIndexKind.IvfFlat, queryDecision.Selected);
        Assert.Equal(VectorIndexKind.DiskAnn, memoryDecision.Selected);
        Assert.Equal(VectorIndexKind.Flat, updateDecision.Selected);
        Assert.Contains("memoryRatio", memoryDecision.Reason, StringComparison.Ordinal);
        Assert.True(queryDecision.Scores.Count >= 3);
    }

    [Fact]
    public void AllowedSetAndPqDivisibilityAreEnforced()
    {
        AutoIndexOptions options = new()
        {
            AllowedIndexes = [VectorIndexKind.Flat, VectorIndexKind.IvfPq],
            HnswMinimumVectors = 10,
            IvfMinimumVectors = 20,
            PqMinimumVectors = 30,
            HysteresisRatio = 0,
        };
        AutoIndexSignals signals = Signals(
            vectorCount: 100_000,
            estimatedBytes: 900_000_000,
            availableBytes: 1_000_000_000,
            queries: 100,
            updates: 1) with
        {
            Dimension = 10,
        };

        AutoIndexDecision decision = AutoIndexSelector.Select(signals, VectorIndexKind.Flat, options, pqSubvectorCount: 8);

        Assert.Equal(VectorIndexKind.Flat, decision.Selected);
        Assert.DoesNotContain(VectorIndexKind.IvfPq, decision.Scores.Where(static pair => pair.Value > 0.05).Select(static pair => pair.Key));
    }

    private static AutoIndexSignals Signals(
        int vectorCount,
        long estimatedBytes,
        long availableBytes,
        double queries,
        double updates) => new()
        {
            VectorCount = vectorCount,
            Dimension = 128,
            EstimatedResidentBytes = estimatedBytes,
            AvailableMemoryBytes = availableBytes,
            QueriesPerSecond = queries,
            UpdatesPerSecond = updates,
            DeletionRatio = 0,
            AverageQueryMilliseconds = 25,
            DiskReadMilliseconds = 1,
            Quantization = VectorQuantizationKind.Float32,
        };
}
