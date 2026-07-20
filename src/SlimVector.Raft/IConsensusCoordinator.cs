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

    CollectionPlacement CreateInitialPlacement(
        Guid collectionId,
        int virtualShardCount = CollectionPlacement.DefaultVirtualShardCount) =>
        CollectionPlacement.Create(collectionId, [GetDataGroupId(collectionId)], virtualShardCount);

    ShardRoute GetShardRoute(CollectionDefinition collection, string documentId) =>
        collection.Placement?.Resolve(collection.Id, documentId) ??
        new ShardRoute(0, GetDataGroupId(collection.Id), 0);

    IReadOnlyList<ShardRoute> GetReadRoutes(CollectionDefinition collection) =>
        collection.Placement?.ReadRoutes() ?? [new ShardRoute(0, GetDataGroupId(collection.Id), 0)];

    ValueTask UpsertCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default);

    ValueTask DeleteCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceTopologyAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Cluster topology updates are not supported by this coordinator."));

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

    ValueTask ApplyDataGroupReadBarrierAsync(
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        ApplyReadBarrierAsync(collectionId: null, consistency, cancellationToken);

    ValueTask ApplyReadBarriersAsync(
        CollectionDefinition collection,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        ApplyReadBarrierAsync(collection.Id, consistency, cancellationToken);
}
