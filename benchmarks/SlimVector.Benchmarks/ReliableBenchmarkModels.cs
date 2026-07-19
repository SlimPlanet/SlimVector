using System.Text.Json.Serialization;
using SlimVector.Domain;

namespace SlimVector.Benchmarks;

internal enum BenchmarkJobKind
{
    Index,
    ServerCrud,
    ServerControl,
    Migration,
    Raft,
    ColdLoad,
}

internal enum BenchmarkStorageMode
{
    Buffered,
    Durable,
}

internal sealed record ReliableBenchmarkProfile(
    string Name,
    int VectorCount,
    int Dimension,
    int QueryCount,
    int TopK,
    int IvfLists,
    int PqSubvectors,
    int TrainingIterations,
    int HnswConstruction,
    int HnswDegree,
    int DiskAnnDegree,
    int OperationCount,
    int ServerDocumentCount,
    int Repetitions,
    int Warmups);

internal sealed record ReliableIndexScenario
{
    public required string Name { get; init; }

    public VectorIndexKind Kind { get; init; }

    public VectorQuantizationKind Quantization { get; init; }

    public int? SearchTuning { get; init; }
}

internal sealed record BenchmarkDatasetSpecification
{
    public required string Kind { get; init; }

    public string? VectorsPath { get; init; }

    public string? QueriesPath { get; init; }

    public string? TruthPath { get; init; }

    public required string Fingerprint { get; init; }
}

internal sealed record BenchmarkWorkerJob
{
    public BenchmarkJobKind Kind { get; init; }

    public required ReliableBenchmarkProfile Profile { get; init; }

    public ReliableIndexScenario? Scenario { get; init; }

    public required BenchmarkDatasetSpecification Dataset { get; init; }

    public BenchmarkStorageMode StorageMode { get; init; }

    public int Iteration { get; init; }

    public bool Warmup { get; init; }

    public required string Workspace { get; init; }

    public string? SnapshotPath { get; init; }
}

internal sealed record BenchmarkWorkerEnvelope
{
    public int ProcessId { get; init; }

    public int? ColdLoadProcessId { get; init; }

    public required BenchmarkIterationResult Result { get; init; }
}

internal sealed record BenchmarkIterationResult
{
    public required string Scenario { get; init; }

    public required string IndexKind { get; init; }

    public required string Quantization { get; init; }

    public int? SearchTuning { get; init; }

    public BenchmarkStorageMode? StorageMode { get; init; }

    public int Iteration { get; init; }

    public int ProcessId { get; init; }

    public int? ColdLoadProcessId { get; init; }

    public double RecallAtK { get; init; }

    public int SuccessCount { get; init; }

    public int ErrorCount { get; init; }

    public int ExpectedRejectionCount { get; init; }

    public int QueueSaturationRejections { get; init; }

    public int CongestionRejections { get; init; }

    public int ContractualRateLimitRejections { get; init; }

    public IReadOnlyList<OperationIterationResult> Operations { get; init; } = [];

    public string? Failure { get; init; }
}

internal sealed record OperationIterationResult
{
    public required string Operation { get; init; }

    public required string LatencyUnit { get; init; }

    public int BatchSize { get; init; }

    public int ItemCount { get; init; }

    public int SampleCount { get; init; }

    public double WallMilliseconds { get; init; }

    public double ThroughputPerSecond { get; init; }

    public double? P50Milliseconds { get; init; }

    public double? P95Milliseconds { get; init; }

    public double? P99Milliseconds { get; init; }

    public double CpuSeconds { get; init; }

    public double AverageCpuCoreEquivalent { get; init; }

    public double NormalizedCpuUtilization { get; init; }

    public double? PeakNormalizedCpuUtilization { get; init; }

    public long WorkingSetBytesBefore { get; init; }

    public long WorkingSetBytesAfter { get; init; }

    public long WorkingSetBytesDelta { get; init; }

    public long? AverageWorkingSetBytes { get; init; }

    public long? PeakWorkingSetBytes { get; init; }

    public long PrivateBytesBefore { get; init; }

    public long PrivateBytesAfter { get; init; }

    public long PrivateBytesDelta { get; init; }

    public long? ManagedBytesBefore { get; init; }

    public long? ManagedBytesAfter { get; init; }

    public long? ManagedBytesDelta { get; init; }

    public long? AllocatedBytes { get; init; }

    public int? Gen0Collections { get; init; }

    public int? Gen1Collections { get; init; }

    public int? Gen2Collections { get; init; }

    public long? LohBytesDelta { get; init; }

    public double? GcPauseMilliseconds { get; init; }

    public long ArtifactSizeDeltaBytes { get; init; }

    public long? StorageReadBytes { get; init; }

    public long? StorageWrittenBytes { get; init; }

    public long? StorageDurableFlushes { get; init; }

    public int ErrorCount { get; init; }

    public int ExpectedRejectionCount { get; init; }

    public IReadOnlyList<double> LatencySamplesMilliseconds { get; init; } = [];

    public IReadOnlyList<ResourceSample> ResourceSamples { get; init; } = [];
}

internal sealed record ResourceSample
{
    public double ElapsedMilliseconds { get; init; }

    public double CpuCoreEquivalent { get; init; }

    public double NormalizedCpuUtilization { get; init; }

    public long WorkingSetBytes { get; init; }

    public long PrivateBytes { get; init; }
}

internal sealed record MetricDistribution
{
    public bool Available => Count > 0;

    public string Unit { get; init; } = "value";

    public int Count { get; init; }

    public double Median { get; init; }

    public double Minimum { get; init; }

    public double Maximum { get; init; }

    public double Mean { get; init; }

    public double StandardDeviation { get; init; }

    public double ConfidenceLow95 { get; init; }

    public double ConfidenceHigh95 { get; init; }
}

internal sealed record OperationAggregate
{
    public required string Operation { get; init; }

    public required string LatencyUnit { get; init; }

    public int BatchSize { get; init; }

    public int IterationCount { get; init; }

    public int ItemCountPerIteration { get; init; }

    public int LatencySampleCount { get; init; }

    public required MetricDistribution WallMilliseconds { get; init; }

    public required MetricDistribution ThroughputPerSecond { get; init; }

    public double? P50Milliseconds { get; init; }

    public double? P95Milliseconds { get; init; }

    public double? P99Milliseconds { get; init; }

    public required MetricDistribution CpuSeconds { get; init; }

    public required MetricDistribution AverageCpuCoreEquivalent { get; init; }

    public required MetricDistribution NormalizedCpuUtilization { get; init; }

    public MetricDistribution? PeakWorkingSetBytes { get; init; }

    public MetricDistribution? ManagedBytesDelta { get; init; }

    public MetricDistribution? AllocatedBytes { get; init; }

    public MetricDistribution? GcPauseMilliseconds { get; init; }

    public required MetricDistribution ArtifactSizeDeltaBytes { get; init; }

    public MetricDistribution? StorageReadBytes { get; init; }

    public MetricDistribution? StorageWrittenBytes { get; init; }

    public MetricDistribution? StorageDurableFlushes { get; init; }

    public int ErrorCount { get; init; }

    public int ExpectedRejectionCount { get; init; }

    public IReadOnlyList<OperationIterationResult> Iterations { get; init; } = [];
}

internal sealed record BenchmarkScenarioAggregate
{
    public required string Scenario { get; init; }

    public required string IndexKind { get; init; }

    public required string Quantization { get; init; }

    public int? SearchTuning { get; init; }

    public BenchmarkStorageMode? StorageMode { get; init; }

    public required MetricDistribution RecallAtK { get; init; }

    public int SuccessCount { get; init; }

    public int ErrorCount { get; init; }

    public int ExpectedRejectionCount { get; init; }

    public int QueueSaturationRejections { get; init; }

    public int CongestionRejections { get; init; }

    public int ContractualRateLimitRejections { get; init; }

    public IReadOnlyList<OperationAggregate> Operations { get; init; } = [];

    public IReadOnlyList<BenchmarkIterationResult> Iterations { get; init; } = [];

    public string? Failure { get; init; }
}

internal sealed record BenchmarkEnvironmentV5
{
    public required string ProtocolVersion { get; init; }

    public required string Version { get; init; }

    public required string Profile { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required string Framework { get; init; }

    public int ProcessorCount { get; init; }

    public required string MachineName { get; init; }

    public required string CpuModel { get; init; }

    public bool ServerGc { get; init; }

    public required string Commit { get; init; }

    public required string DiskFormat { get; init; }

    public required string DatasetFingerprint { get; init; }

    public required string BenchmarkFingerprint { get; init; }

    public required string ProcessColdLoadCacheState { get; init; }

    public int VectorCount { get; init; }

    public int Dimension { get; init; }

    public int QueryCount { get; init; }

    public int TopK { get; init; }

    public int Repetitions { get; init; }

    public int Warmups { get; init; }

    public int MutationBatchSize { get; init; }

    public int PressureProbeRequests { get; init; }

    public int DocumentsPerPressureRequest { get; init; }

    public required string[] RecallThresholds { get; init; }

    public required string[] ScenarioMatrix { get; init; }
}

internal sealed record BenchmarkRunV5
{
    public int SchemaVersion { get; init; } = 5;

    public required BenchmarkEnvironmentV5 Environment { get; init; }

    public required IReadOnlyList<BenchmarkScenarioAggregate> Results { get; init; }

    public required string BaselineStatus { get; init; }

    public bool HasSignificantRegression { get; init; }

    public double DurationSeconds { get; init; }

    [JsonIgnore]
    public BenchmarkRunV5? Baseline { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BenchmarkRunV5))]
[JsonSerializable(typeof(BenchmarkWorkerJob))]
[JsonSerializable(typeof(BenchmarkWorkerEnvelope))]
internal sealed partial class ReliableBenchmarkJsonContext : JsonSerializerContext;
