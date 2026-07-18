using SlimVector.Domain;
using SlimVector.Storage;

namespace SlimVector.Application.Writes;

public interface IWriteScheduler : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask EnqueueAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        string? clientId,
        CancellationToken cancellationToken = default);

    WriteSchedulerSnapshot GetSnapshot();
}

public sealed record WriteSchedulerSnapshot
{
    public required long QueueDepth { get; init; }

    public required long TotalWrites { get; init; }

    public required long CompletedWrites { get; init; }

    public required long RejectedWrites { get; init; }

    public required long TotalBatches { get; init; }

    public required long TotalBatchItems { get; init; }

    public required IReadOnlyList<WriteShardSnapshot> Shards { get; init; }
}

public sealed record WriteShardSnapshot
{
    public required string GroupId { get; init; }

    public required long QueueDepth { get; init; }

    public required int TargetBatchSize { get; init; }

    public required TimeSpan CurrentWindow { get; init; }

    public required int LastBatchSize { get; init; }

    public required long LastBatchBytes { get; init; }

    public required double LastReplicationMilliseconds { get; init; }
}
