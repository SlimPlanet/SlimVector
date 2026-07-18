using MemoryPack;
using SlimVector.Raft.Commands;

namespace SlimVector.Replication;

public enum GeoReplicationEventKind
{
    CollectionUpsert = 1,
    CollectionDelete = 2,
    DataWrites = 3,
}

[MemoryPackable]
public sealed partial class GeoReplicationEvent
{
    public int FormatVersion { get; set; } = 1;

    public Guid EventId { get; set; }

    public long Sequence { get; set; }

    public string OriginId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public GeoReplicationEventKind Kind { get; set; }

    public GeoCollectionWrite[] Writes { get; set; } = [];
}

[MemoryPackable]
public sealed partial class GeoCollectionWrite
{
    public RaftCollectionDefinition Collection { get; set; } = new();

    public RaftStorageOperation[] Operations { get; set; } = [];
}

public sealed record GeoReplicationMetrics
{
    public required long PendingEvents { get; init; }

    public required long SentEvents { get; init; }

    public required long FailedAttempts { get; init; }

    public required long ReceivedEvents { get; init; }

    public required long DuplicateEvents { get; init; }

    public required long DivergenceEvents { get; init; }

    public required TimeSpan ReplicationLag { get; init; }

    public string? LastError { get; init; }
}

[MemoryPackable]
internal sealed partial class GeoReplicationCheckpoint
{
    public int FormatVersion { get; set; } = 1;

    public GeoOriginCheckpoint[] Origins { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class GeoOriginCheckpoint
{
    public string OriginId { get; set; } = string.Empty;

    public long LastSequence { get; set; }

    public GeoEventReceipt[] Receipts { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class GeoEventReceipt
{
    public long Sequence { get; set; }

    public Guid EventId { get; set; }

    public byte[] PayloadHash { get; set; } = [];
}
