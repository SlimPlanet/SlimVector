using SlimVector.Domain;

namespace SlimVector.Application.Configuration;

public sealed class SlimVectorOptions
{
    public StorageOptions Storage { get; set; } = new();

    public VectorIndexOptions VectorIndex { get; set; } = new();

    public AutoIndexOptions AutoIndex { get; set; } = new();

    public HnswOptions Hnsw { get; set; } = new();

    public IvfOptions Ivf { get; set; } = new();

    public PqOptions Pq { get; set; } = new();

    public DiskAnnOptions DiskAnn { get; set; } = new();

    public RaftOptions Raft { get; set; } = new();

    public ClusterMembershipOptions ClusterMembership { get; set; } = new();

    public RebalancingOptions Rebalancing { get; set; } = new();

    public AdaptiveBatchingOptions AdaptiveBatching { get; set; } = new();

    public RateLimitOptions RateLimit { get; set; } = new();

    public BackpressureOptions Backpressure { get; set; } = new();

    public BackupOptions Backup { get; set; } = new();

    public GeoReplicationOptions Geo { get; set; } = new();

    public ObservabilityOptions Observability { get; set; } = new();
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Path { get; set; } = "data";

    public bool FlushToDisk { get; set; } = true;

    public int MaximumSegmentsBeforeCompaction { get; set; } = 64;
}

public sealed class CollectionsOptions
{
    public const string SectionName = "Collections";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan EvictionSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int MaximumOpenCollections { get; set; } = 128;
}

public sealed class VectorIndexOptions
{
    public const string SectionName = "VectorIndex";

    public int AutoHnswThreshold { get; set; } = 50_000;

    public int HybridCandidateMultiplier { get; set; } = 4;

    public int MaximumSearchLimit { get; set; } = 1_000;
}

public sealed class AutoIndexOptions
{
    public const string SectionName = "AutoIndex";

    public bool Enabled { get; set; } = true;

    public VectorIndexKind[] AllowedIndexes { get; set; } =
        [VectorIndexKind.Flat, VectorIndexKind.Hnsw, VectorIndexKind.IvfFlat, VectorIndexKind.IvfPq, VectorIndexKind.DiskAnn];

    public TimeSpan AssessmentInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MinimumMigrationInterval { get; set; } = TimeSpan.FromMinutes(15);

    public double HysteresisRatio { get; set; } = 0.20;

    public double MinimumRecall { get; set; } = 0.90;

    public double MinimumPerformanceGain { get; set; } = 0.10;

    public int ValidationSampleSize { get; set; } = 64;

    public int HnswMinimumVectors { get; set; } = 10_000;

    public int IvfMinimumVectors { get; set; } = 100_000;

    public int PqMinimumVectors { get; set; } = 500_000;

    public double DiskAnnMemoryRatio { get; set; } = 0.60;

    public double HighUpdateRatio { get; set; } = 0.10;
}

public sealed class HnswOptions
{
    public const string SectionName = "Hnsw";

    public int M { get; set; } = 16;

    public int EfConstruction { get; set; } = 200;

    public int EfSearch { get; set; } = 64;
}

public sealed class IvfOptions
{
    public const string SectionName = "Ivf";

    public int ListCount { get; set; } = 256;

    public int ProbeCount { get; set; } = 8;

    public int TrainingIterations { get; set; } = 20;
}

public sealed class PqOptions
{
    public const string SectionName = "PQ";

    public int SubvectorCount { get; set; } = 8;

    public int CentroidCount { get; set; } = 256;

    public int TrainingIterations { get; set; } = 20;

    public int RerankCandidateMultiplier { get; set; } = 4;
}

public sealed class DiskAnnOptions
{
    public const string SectionName = "DiskAnn";

    public string Path { get; set; } = "data/indexes/diskann";

    public int MaxDegree { get; set; } = 32;

    public int SearchListSize { get; set; } = 64;

    public int BeamWidth { get; set; } = 4;

    public int DeltaThreshold { get; set; } = 10_000;

    public int PageSize { get; set; } = 4_096;

    public int CachePages { get; set; } = 256;

    public int RetainedGenerations { get; set; } = 2;
}

public sealed class TextIndexOptions
{
    public const string SectionName = "TextIndex";

    public double Bm25K1 { get; set; } = 1.2;

    public double Bm25B { get; set; } = 0.75;

    public int MaximumTermsPerDocument { get; set; } = 100_000;
}

public sealed class MetadataIndexOptions
{
    public const string SectionName = "MetadataIndex";

    public bool IndexByDefault { get; set; } = true;

    public int MaximumFilterDepth { get; set; } = 32;
}

public sealed class RaftOptions
{
    public const string SectionName = "Raft";

    public ExecutionMode Mode { get; set; } = ExecutionMode.SingleNode;

    public string NodeId { get; set; } = "node-1";

    public string PublicEndpoint { get; set; } = "http://127.0.0.1:3262";

    public string PublicApiEndpoint { get; set; } = "http://127.0.0.1:8080";

    public string[] Members { get; set; } = [];

    public string[] MemberApiEndpoints { get; set; } = [];

    public bool JoinExistingCluster { get; set; }

    public int DataGroupCount { get; set; } = 2;

    public TimeSpan ElectionTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int SnapshotEveryEntries { get; set; } = 10_000;

    public int TransmissionBlockSize { get; set; } = 64 * 1024;
}

public sealed class ClusterMembershipOptions
{
    public const string SectionName = "ClusterMembership";

    public bool Enabled { get; set; } = true;

    public int WarmupRounds { get; set; } = 10;

    public int MaximumCatchUpLagEntries { get; set; } = 100;

    public int MinimumVotingMembers { get; set; } = 3;

    public TimeSpan CatchUpTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public bool AutoPromote { get; set; }
}

public sealed class RebalancingOptions
{
    public const string SectionName = "Rebalancing";

    public bool Enabled { get; set; } = true;

    public bool ManualApproval { get; set; } = true;

    public int MaximumConcurrentMoves { get; set; } = 1;

    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    public double MinimumImprovementRatio { get; set; } = 0.10;
}

public sealed class GeoReplicationOptions
{
    public const string SectionName = "GeoReplication";

    public bool Enabled { get; set; }

    public bool AcceptIncoming { get; set; }

    public bool ReadOnlySecondary { get; set; } = true;

    public GeoReplicationMode Mode { get; set; } = GeoReplicationMode.Asynchronous;

    public string? SecondaryEndpoint { get; set; }

    public string OriginId { get; set; } = "primary";

    public string SharedSecret { get; set; } = string.Empty;

    public string StatePath { get; set; } = "data/geo-replication";

    public TimeSpan RetryMinimumDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaximumDelay { get; set; } = TimeSpan.FromMinutes(1);

    public int QueueCapacity { get; set; } = 10_000;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class AdaptiveBatchingOptions
{
    public const string SectionName = "AdaptiveBatching";

    public bool Enabled { get; set; } = true;

    public int MinimumBatchSize { get; set; } = 1;

    public int MaximumBatchSize { get; set; } = 256;

    public TimeSpan MinimumWindow { get; set; } = TimeSpan.Zero;

    public TimeSpan MaximumWindow { get; set; } = TimeSpan.FromMilliseconds(10);

    public long MaximumBatchBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class BackpressureOptions
{
    public const string SectionName = "Backpressure";

    public int GlobalQueueCapacity { get; set; } = 50_000;

    public int PerCollectionQueueCapacity { get; set; } = 5_000;

    public int PerShardQueueCapacity { get; set; } = 10_000;

    public int PerClientQueueCapacity { get; set; } = 2_000;

    public int MaximumConcurrentWrites { get; set; } = Environment.ProcessorCount;

    public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public bool Enabled { get; set; } = true;

    public TokenBucketOptions Global { get; set; } = new() { TokensPerSecond = 20_000, BurstCapacity = 40_000 };

    public TokenBucketOptions Client { get; set; } = new() { TokensPerSecond = 2_000, BurstCapacity = 4_000 };

    public TokenBucketOptions Collection { get; set; } = new() { TokensPerSecond = 5_000, BurstCapacity = 10_000 };

    public TokenBucketOptions Read { get; set; } = new() { TokensPerSecond = 15_000, BurstCapacity = 30_000 };

    public TokenBucketOptions Write { get; set; } = new() { TokensPerSecond = 5_000, BurstCapacity = 10_000 };

    public TokenBucketOptions Admin { get; set; } = new() { TokensPerSecond = 100, BurstCapacity = 200 };

    public double ReservedReadFraction { get; set; } = 0.20;

    public double ReservedWriteFraction { get; set; } = 0.10;

    public TimeSpan IdleBucketExpiration { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RecoveryWindow { get; set; } = TimeSpan.FromSeconds(30);

    public double MinimumAdaptiveRateRatio { get; set; } = 0.10;
}

public sealed class TokenBucketOptions
{
    public double TokensPerSecond { get; set; }

    public double BurstCapacity { get; set; }
}

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public bool Enabled { get; set; }

    public BackupProvider Provider { get; set; } = BackupProvider.FileSystem;

    public string Path { get; set; } = "backups";

    public string? S3Endpoint { get; set; }

    public string? S3Bucket { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    public int RetentionCount { get; set; } = 14;

    public bool Encrypt { get; set; }

    public string? EncryptionKey { get; set; }

    public string S3Region { get; set; } = "us-east-1";

    public string? S3AccessKey { get; set; }

    public string? S3SecretKey { get; set; }

    public string Prefix { get; set; } = "slimvector";
}

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string RoutePrefix { get; set; } = "/api/v1";

    public int MaximumBatchSize { get; set; } = 1_000;

    public long MaximumRequestBodyBytes { get; set; } = 16 * 1024 * 1024;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool AdminEndpointsEnabled { get; set; }

    public string AdminApiKey { get; set; } = string.Empty;
}

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public bool MetricsEnabled { get; set; } = true;

    public bool StructuredConsoleLogging { get; set; } = true;

    public string ServiceName { get; set; } = "SlimVector";

    public TimeSpan SlowQueryThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
}
