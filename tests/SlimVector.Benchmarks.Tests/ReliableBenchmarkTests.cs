using System.Text.Json;
namespace SlimVector.Benchmarks.Tests;

public sealed class ReliableBenchmarkTests
{
    [Fact]
    public void PercentilesRequireEnoughSamples()
    {
        Assert.Null(ReliableBenchmarkStatistics.QualifiedPercentile([1], 0.50));
        Assert.NotNull(ReliableBenchmarkStatistics.QualifiedPercentile([1, 2], 0.50));
        Assert.Null(ReliableBenchmarkStatistics.QualifiedPercentile(Enumerable.Range(0, 19).Select(static value => (double)value), 0.95));
        Assert.NotNull(ReliableBenchmarkStatistics.QualifiedPercentile(Enumerable.Range(0, 20).Select(static value => (double)value), 0.95));
        Assert.Null(ReliableBenchmarkStatistics.QualifiedPercentile(Enumerable.Range(0, 99).Select(static value => (double)value), 0.99));
        Assert.NotNull(ReliableBenchmarkStatistics.QualifiedPercentile(Enumerable.Range(0, 100).Select(static value => (double)value), 0.99));
    }

    [Fact]
    public void BootstrapAndRegressionDecisionAreDeterministic()
    {
        MetricDistribution first = ReliableBenchmarkStatistics.Distribution([10, 11, 12, 13, 14]);
        MetricDistribution second = ReliableBenchmarkStatistics.Distribution([10, 11, 12, 13, 14]);
        Assert.Equal(first, second);
        MetricDistribution slower = ReliableBenchmarkStatistics.Distribution([30, 31, 32, 33, 34]);
        Assert.True(ReliableBenchmarkStatistics.IsRegression(slower, first, 0.10, 5));
        Assert.True(ReliableBenchmarkStatistics.IsRegression(
            ReliableBenchmarkStatistics.Distribution([30, 31, 32]),
            ReliableBenchmarkStatistics.Distribution([10, 11, 12]),
            0.10,
            5));
        Assert.False(ReliableBenchmarkStatistics.IsRegression(
            ReliableBenchmarkStatistics.Distribution([12, 12, 12, 12, 12]),
            first,
            0.10,
            5));
        Assert.Equal("value", first.Unit);
        Assert.True(first.Available);
    }

    [Fact]
    public void CommandLineValueParsersUseStableDefaultsAndValidation()
    {
        ReliableBenchmarkProfile smoke = ReliableBenchmarkRunner.ParseProfile("Smoke");
        Assert.Equal(5, smoke.Repetitions);
        Assert.Equal(1, smoke.Warmups);
        Assert.Equal([BenchmarkStorageMode.Buffered, BenchmarkStorageMode.Durable], ReliableBenchmarkRunner.ParseStorageModes("both"));
        Assert.Equal([0.90, 0.95, 0.99], ReliableBenchmarkRunner.ParseRecallThresholds(".99,.90,.95"));
        Assert.Throws<ArgumentException>(() => ReliableBenchmarkRunner.ParseRecallThresholds("0"));
    }

    [Fact]
    public void FvecsAndIvecsDatasetIsLoadedWithoutNetworkAccess()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string vectors = Path.Combine(directory, "vectors.fvecs");
        string queries = Path.Combine(directory, "queries.fvecs");
        string truth = Path.Combine(directory, "truth.ivecs");
        try
        {
            WriteVectors(vectors, [new float[] { 1, 0 }, new float[] { 0, 1 }]);
            WriteVectors(queries, [new float[] { 1, 0 }]);
            using (BinaryWriter writer = new(File.Create(truth)))
            {
                writer.Write(1);
                writer.Write(0);
            }

            ReliableBenchmarkProfile profile = new("Test", 2, 2, 1, 1, 2, 1, 1, 8, 4, 4, 16, 2, 1, 0);
            BenchmarkDatasetSpecification specification = ReliableBenchmarkDatasetFactory.Specification(
                "fvecs",
                vectors,
                queries,
                truth);
            ReliableBenchmarkDataset dataset = ReliableBenchmarkDatasetFactory.Create(profile, specification);

            Assert.Equal(2, dataset.Documents.Length);
            Assert.Single(dataset.Queries);
            Assert.Contains("doc-0", Assert.Single(dataset.Truth));
            Assert.NotEmpty(specification.Fingerprint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AggregationSeparatesErrorsRejectionsAndUnavailableMetrics()
    {
        BenchmarkIterationResult iteration = new()
        {
            Scenario = "sample",
            IndexKind = "Flat",
            Quantization = "Float32",
            Iteration = 0,
            ProcessId = 42,
            ErrorCount = 1,
            ExpectedRejectionCount = 3,
            Operations =
            [
                new OperationIterationResult
                {
                    Operation = "SelectVector",
                    LatencyUnit = "operation",
                    BatchSize = 1,
                    ItemCount = 1,
                    WallMilliseconds = 1,
                    ThroughputPerSecond = 1,
                    CpuSeconds = 0.1,
                    AverageCpuCoreEquivalent = 0.1,
                    NormalizedCpuUtilization = 0.01,
                    ErrorCount = 1,
                    ExpectedRejectionCount = 3,
                },
            ],
        };

        BenchmarkScenarioAggregate aggregate = Assert.Single(ReliableBenchmarkRunner.Aggregate([iteration]));
        Assert.Equal(1, aggregate.ErrorCount);
        Assert.Equal(3, aggregate.ExpectedRejectionCount);
        OperationAggregate operation = Assert.Single(aggregate.Operations);
        Assert.Null(operation.P50Milliseconds);
        Assert.Null(operation.ManagedBytesDelta);
    }

    [Fact]
    public void SchemaV4BaselineIsReportedAsIncompatible()
    {
        string path = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks.Tests", Guid.NewGuid().ToString("N"), "baseline.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":4}");
            (BenchmarkRunV5? run, string status) = ReliableBenchmarkRunner.LoadBaseline(path, "fingerprint");
            Assert.Null(run);
            Assert.Contains("incompatible schema v4", status, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void MatchingSchemaV5BaselineIsCompatible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "baseline.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(MinimalRun(), ReliableBenchmarkJsonContext.Default.BenchmarkRunV5));
            (BenchmarkRunV5? run, string status) = ReliableBenchmarkRunner.LoadBaseline(path, "test");
            Assert.NotNull(run);
            Assert.Equal("compatible", status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReportRendersUnavailableValuesAsNotAvailable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks.Tests", Guid.NewGuid().ToString("N"));
        string runDirectory = Path.Combine(directory, "run");
        try
        {
            ReliableBenchmarkReportWriter.Write(runDirectory, directory, MinimalRun(), [0.90]);
            string markdown = File.ReadAllText(Path.Combine(runDirectory, "benchmark-summary.md"));
            Assert.Contains("n/a", markdown, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runDirectory, "benchmark-resource-samples.csv")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void SchemaV5SerializesNullableMetricsAsNull()
    {
        BenchmarkRunV5 run = MinimalRun();
        string json = JsonSerializer.Serialize(run, ReliableBenchmarkJsonContext.Default.BenchmarkRunV5);
        Assert.Contains("\"schemaVersion\": 5", json, StringComparison.Ordinal);
        Assert.Contains("\"available\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"unit\": \"value\"", json, StringComparison.Ordinal);
        Assert.Contains("\"p95Milliseconds\": null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"baseline\"", json, StringComparison.OrdinalIgnoreCase);
    }

    private static BenchmarkRunV5 MinimalRun() => new()
    {
        Environment = new BenchmarkEnvironmentV5
        {
            ProtocolVersion = "test",
            Version = "test",
            Profile = "Smoke",
            OperatingSystem = "test",
            Architecture = "test",
            Framework = "test",
            MachineName = "test",
            CpuModel = "test",
            Commit = "test",
            DiskFormat = "test",
            DatasetFingerprint = "test",
            BenchmarkFingerprint = "test",
            ProcessColdLoadCacheState = "uncontrolled",
            RecallThresholds = ["0.90"],
            ScenarioMatrix = ["Flat-Float32"],
        },
        Results =
        [
            new BenchmarkScenarioAggregate
            {
                Scenario = "Flat-Float32",
                IndexKind = "Flat",
                Quantization = "Float32",
                RecallAtK = new MetricDistribution(),
                Operations =
                [
                    new OperationAggregate
                    {
                        Operation = "SelectVector",
                        LatencyUnit = "operation",
                        WallMilliseconds = new MetricDistribution(),
                        ThroughputPerSecond = new MetricDistribution(),
                        CpuSeconds = new MetricDistribution(),
                        AverageCpuCoreEquivalent = new MetricDistribution(),
                        NormalizedCpuUtilization = new MetricDistribution(),
                        ArtifactSizeDeltaBytes = new MetricDistribution(),
                    },
                ],
            },
        ],
        BaselineStatus = "not supplied",
    };

    private static void WriteVectors(string path, IReadOnlyList<float[]> vectors)
    {
        using BinaryWriter writer = new(File.Create(path));
        foreach (float[] vector in vectors)
        {
            writer.Write(vector.Length);
            foreach (float value in vector)
            {
                writer.Write(value);
            }
        }
    }
}
