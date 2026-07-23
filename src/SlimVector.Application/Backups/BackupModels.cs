using MemoryPack;
using SlimVector.Raft.Commands;

namespace SlimVector.Application.Backups;

public sealed record BackupDescriptor
{
    public required string BackupId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? ParentBackupId { get; init; }

    public required int CollectionCount { get; init; }

    public required long DocumentCount { get; init; }
}

public sealed record BackupMetricsSnapshot
{
    public required long SuccessfulBackups { get; init; }

    public required long FailedBackups { get; init; }

    public required long SuccessfulRestores { get; init; }

    public required long FailedRestores { get; init; }

    public required long DeduplicatedBlobs { get; init; }

    public DateTimeOffset? LastSuccessfulBackupAt { get; init; }

    public DateTimeOffset? LastSuccessfulRestoreAt { get; init; }
}

[MemoryPackable]
internal sealed partial class BackupManifest
{
    public int FormatVersion { get; set; } = 1;

    public string BackupId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string? ParentBackupId { get; set; }

    public BackupCollectionEntry[] Collections { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BackupCollectionEntry
{
    public Guid CollectionId { get; set; }

    public string CollectionName { get; set; } = string.Empty;

    public string BlobHash { get; set; } = string.Empty;

    public long DocumentCount { get; set; }
}

[MemoryPackable]
internal sealed partial class BackupCollectionPayload
{
    public int FormatVersion { get; set; } = 1;

    public RaftCollectionDefinition Collection { get; set; } = new();

    public RaftDocument[] Documents { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class BackupEnvelope
{
    public int FormatVersion { get; set; } = 1;

    public bool Encrypted { get; set; }

    public byte[] Nonce { get; set; } = [];

    public byte[] Tag { get; set; } = [];

    public byte[] Data { get; set; } = [];
}
