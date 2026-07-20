using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Replication;

public sealed class GeoReplicatingConsensusCoordinator : IConsensusCoordinator
{
    private readonly IConsensusCoordinator _local;
    private readonly IGeoReplicationService _replication;

    public GeoReplicatingConsensusCoordinator(
        IConsensusCoordinator local,
        IGeoReplicationService replication)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(replication);
        _local = local;
        _replication = replication;
    }

    public event Action<Guid?>? StateChanged
    {
        add => _local.StateChanged += value;
        remove => _local.StateChanged -= value;
    }

    public ExecutionMode Mode => _local.Mode;

    public bool IsReady => _local.IsReady;

    public IReadOnlyList<RaftGroupStatus> GetStatuses() => _local.GetStatuses();

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _local.StartAsync(cancellationToken).ConfigureAwait(false);
        await _replication.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _replication.StopAsync(cancellationToken).ConfigureAwait(false);
        await _local.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public string GetDataGroupId(Guid collectionId) => _local.GetDataGroupId(collectionId);

    public CollectionPlacement CreateInitialPlacement(
        Guid collectionId,
        int virtualShardCount = CollectionPlacement.DefaultVirtualShardCount) =>
        _local.CreateInitialPlacement(collectionId, virtualShardCount);

    public ShardRoute GetShardRoute(CollectionDefinition collection, string documentId) =>
        _local.GetShardRoute(collection, documentId);

    public IReadOnlyList<ShardRoute> GetReadRoutes(CollectionDefinition collection) =>
        _local.GetReadRoutes(collection);

    public async ValueTask UpsertCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default)
    {
        await _local.UpsertCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        await _replication
            .PublishAsync(GeoReplicationEventFactory.CollectionUpsert(collection), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DeleteCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default)
    {
        await _local.DeleteCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        await _replication
            .PublishAsync(GeoReplicationEventFactory.CollectionDelete(collection), cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask AppendAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default) =>
        AppendBatchAsync([new CollectionWrite(collection, operations)], cancellationToken);

    public async ValueTask AppendBatchAsync(
        IReadOnlyList<CollectionWrite> writes,
        CancellationToken cancellationToken = default)
    {
        await _local.AppendBatchAsync(writes, cancellationToken).ConfigureAwait(false);
        await _replication
            .PublishAsync(GeoReplicationEventFactory.DataWrites(writes), cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        _local.ApplyReadBarrierAsync(collectionId, consistency, cancellationToken);

    public ValueTask ApplyReadBarriersAsync(
        CollectionDefinition collection,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        _local.ApplyReadBarriersAsync(collection, consistency, cancellationToken);

    public ValueTask DisposeAsync() => _local.DisposeAsync();
}
