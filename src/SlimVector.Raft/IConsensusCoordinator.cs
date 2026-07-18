using SlimVector.Domain;
using SlimVector.Storage;

namespace SlimVector.Raft;

public interface IConsensusCoordinator : IAsyncDisposable
{
    event Action<Guid?>? StateChanged;

    ExecutionMode Mode { get; }

    bool IsReady { get; }

    IReadOnlyList<RaftGroupStatus> GetStatuses();

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    string GetDataGroupId(Guid collectionId);

    ValueTask UpsertCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default);

    ValueTask DeleteCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default);

    ValueTask AppendAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default);

    ValueTask AppendBatchAsync(
        IReadOnlyList<CollectionWrite> writes,
        CancellationToken cancellationToken = default);

    ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);
}
