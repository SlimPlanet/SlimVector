using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SlimVector.Domain;

public enum ShardPlacementState
{
    Active,
    Copying,
    CatchingUp,
    Switching,
    Draining,
}

public sealed record ShardPlacement
{
    public required int ShardId { get; init; }

    public required string DataGroupId { get; init; }

    public string[] ReplicaSet { get; init; } = [];

    public ShardPlacementState State { get; init; } = ShardPlacementState.Active;

    public string? SourceDataGroupId { get; init; }

    public string? TargetDataGroupId { get; init; }

    public Guid? OperationId { get; init; }

    public long SnapshotVersion { get; init; }

    public long ReplayedThroughVersion { get; init; }
}

public sealed record CollectionPlacement
{
    public const int DefaultVirtualShardCount = 256;

    public long Epoch { get; init; } = 1;

    public int VirtualShardCount { get; init; } = DefaultVirtualShardCount;

    public string? ShardKey { get; init; }

    public required ShardPlacement[] Shards { get; init; }

    public static CollectionPlacement Create(
        Guid collectionId,
        IReadOnlyList<string> dataGroupIds,
        int virtualShardCount = DefaultVirtualShardCount)
    {
        ArgumentNullException.ThrowIfNull(dataGroupIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(virtualShardCount, 1);
        string[] groups = dataGroupIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (groups.Length == 0)
        {
            throw new ArgumentException("At least one data group is required.", nameof(dataGroupIds));
        }

        int offset = StableShard(collectionId, collectionId.ToString("N"), groups.Length);
        return new CollectionPlacement
        {
            VirtualShardCount = virtualShardCount,
            Shards = Enumerable.Range(0, virtualShardCount)
                .Select(shard => new ShardPlacement
                {
                    ShardId = shard,
                    DataGroupId = groups[(offset + shard) % groups.Length],
                })
                .ToArray(),
        };
    }

    public ShardRoute Resolve(Guid collectionId, string documentId)
    {
        DomainValidation.ValidateDocumentId(documentId);
        Validate();
        int shardId = StableShard(collectionId, documentId, VirtualShardCount);
        ShardPlacement placement = Shards[shardId];
        string groupId = placement.State is ShardPlacementState.Switching or ShardPlacementState.Draining
            ? placement.TargetDataGroupId ?? placement.DataGroupId
            : placement.DataGroupId;
        return new ShardRoute(shardId, groupId, Epoch);
    }

    public IReadOnlyList<ShardRoute> ReadRoutes() => Shards
        .Select(shard => new ShardRoute(
            shard.ShardId,
            shard.State is ShardPlacementState.Switching or ShardPlacementState.Draining
                ? shard.TargetDataGroupId ?? shard.DataGroupId
                : shard.DataGroupId,
            Epoch))
        .Distinct()
        .OrderBy(static route => route.ShardId)
        .ToArray();

    public void Validate()
    {
        if (Epoch < 1 || VirtualShardCount < 1 || Shards.Length != VirtualShardCount ||
            Shards.Select(static shard => shard.ShardId).Distinct().Count() != VirtualShardCount ||
            Shards.Any(shard => shard.ShardId < 0 || shard.ShardId >= VirtualShardCount ||
                string.IsNullOrWhiteSpace(shard.DataGroupId)))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The collection placement is malformed.");
        }
    }

    private static int StableShard(Guid collectionId, string value, int count)
    {
        byte[] text = Encoding.UTF8.GetBytes(value);
        byte[] input = new byte[16 + text.Length];
        collectionId.TryWriteBytes(input);
        text.CopyTo(input, 16);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % (uint)count);
    }
}

public readonly record struct ShardRoute(int ShardId, string DataGroupId, long RoutingEpoch);
