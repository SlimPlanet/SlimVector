using SlimVector.Domain;

namespace SlimVector.Storage;

internal sealed record CatalogFile
{
    public int FormatVersion { get; init; } = 1;

    public long Generation { get; init; }

    public List<CollectionDefinition> Collections { get; init; } = [];
}

internal sealed record CollectionManifest
{
    public int FormatVersion { get; init; } = 1;

    public required Guid CollectionId { get; init; }

    public long Generation { get; init; }

    public List<SegmentDescriptor> Segments { get; init; } = [];
}

internal sealed record SegmentDescriptor
{
    public required long Sequence { get; init; }

    public required string FileName { get; init; }

    public required string Checksum { get; init; }

    public required long Length { get; init; }
}

internal sealed record SegmentPayload
{
    public int FormatVersion { get; init; } = 1;

    public required Guid CollectionId { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required List<StorageOperation> Operations { get; init; }
}
