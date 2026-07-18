using SlimVector.Domain;

namespace SlimVector.Application.Configuration;

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

    public int DataGroupCount { get; set; } = 2;

    public TimeSpan ElectionTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int SnapshotEveryEntries { get; set; } = 10_000;

    public int TransmissionBlockSize { get; set; } = 64 * 1024;
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
