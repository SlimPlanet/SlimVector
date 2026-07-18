using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Raft.Commands;

public enum RaftCommandKind
{
    CatalogUpsert = 1,
    CatalogDelete = 2,
    DataBatch = 3,
    ShardBatch = 4,
}

[MemoryPackable]
public sealed partial class RaftCommandEnvelope
{
    public int FormatVersion { get; set; } = 1;

    public Guid CommandId { get; set; }

    public string GroupId { get; set; } = string.Empty;

    public RaftCommandKind Kind { get; set; }

    public CatalogUpsertCommand? CatalogUpsert { get; set; }

    public CatalogDeleteCommand? CatalogDelete { get; set; }

    public DataBatchCommand? DataBatch { get; set; }

    public ShardBatchCommand? ShardBatch { get; set; }
}

[MemoryPackable]
public sealed partial class CatalogUpsertCommand
{
    public RaftCollectionDefinition Collection { get; set; } = new();

    public string DataGroupId { get; set; } = string.Empty;
}

[MemoryPackable]
public sealed partial class CatalogDeleteCommand
{
    public Guid CollectionId { get; set; }

    public string CollectionName { get; set; } = string.Empty;
}

[MemoryPackable]
public sealed partial class DataBatchCommand
{
    public Guid CollectionId { get; set; }

    public RaftCollectionDefinition Collection { get; set; } = new();

    public RaftStorageOperation[] Operations { get; set; } = [];
}

[MemoryPackable]
public sealed partial class ShardBatchCommand
{
    public DataBatchCommand[] Batches { get; set; } = [];
}

[MemoryPackable]
public sealed partial class RaftCollectionDefinition
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public VectorIndexKind VectorIndexKind { get; set; }

    public int HnswM { get; set; }

    public int HnswEfConstruction { get; set; }

    public int HnswEfSearch { get; set; }

    public bool MetadataIndexed { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

[MemoryPackable]
public sealed partial class RaftStorageOperation
{
    public DocumentMutationKind Kind { get; set; }

    public string Id { get; set; } = string.Empty;

    public RaftDocument? Document { get; set; }

    public long Version { get; set; }
}

[MemoryPackable]
public sealed partial class RaftDocument
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public float[] Vector { get; set; } = [];

    public RaftMetadataEntry[] Metadata { get; set; } = [];

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

[MemoryPackable]
public sealed partial class RaftMetadataEntry
{
    public string Key { get; set; } = string.Empty;

    public RaftMetadataValue Value { get; set; } = new();
}

[MemoryPackable]
public sealed partial class RaftMetadataValue
{
    public MetadataValueKind Kind { get; set; }

    public string? StringValue { get; set; }

    public bool? BooleanValue { get; set; }

    public long? IntegerValue { get; set; }

    public double? NumberValue { get; set; }

    public DateTimeOffset? DateTimeValue { get; set; }

    public Guid? GuidValue { get; set; }

    public string[]? StringArrayValue { get; set; }

    public bool[]? BooleanArrayValue { get; set; }

    public long[]? IntegerArrayValue { get; set; }

    public double[]? NumberArrayValue { get; set; }
}

[MemoryPackable]
public sealed partial class RaftApplicationSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public string GroupId { get; set; } = string.Empty;

    public RaftCollectionSnapshot[] Collections { get; set; } = [];
}

[MemoryPackable]
public sealed partial class RaftCollectionSnapshot
{
    public RaftCollectionDefinition Definition { get; set; } = new();

    public RaftDocument[] Documents { get; set; } = [];
}
