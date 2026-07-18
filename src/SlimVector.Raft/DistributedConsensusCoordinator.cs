using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft;

public sealed class DistributedConsensusCoordinator : IConsensusCoordinator
{
    private readonly MultiRaftNode _node;
    private readonly StorageRaftCommandApplier _applier;

    public DistributedConsensusCoordinator(MultiRaftNode node, StorageRaftCommandApplier applier)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(applier);
        _node = node;
        _applier = applier;
        _applier.StateChanged += OnStateChanged;
    }

    public event Action<Guid?>? StateChanged;

    public ExecutionMode Mode => ExecutionMode.Cluster;

    public bool IsReady => _node.GroupIds.All(groupId => _node.GetGroup(groupId).LeaderEndpoint is not null);

    public IReadOnlyList<RaftGroupStatus> GetStatuses() => _node.GroupIds
        .Order(StringComparer.Ordinal)
        .Select(groupId =>
        {
            RaftGroupNode group = _node.GetGroup(groupId);
            return new RaftGroupStatus
            {
                GroupId = groupId,
                LocalEndpoint = group.LocalEndpoint.ToString(),
                LeaderEndpoint = group.LeaderEndpoint?.ToString(),
                IsLeader = group.IsLeader,
                Term = group.Term,
                LastAppliedIndex = group.LastAppliedIndex,
                LastCommittedIndex = group.LastCommittedIndex,
                AppliedCommandCount = group.AppliedCommandCount,
            };
        })
        .ToArray();

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => _node.StartAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => _node.StopAsync(cancellationToken);

    public string GetDataGroupId(Guid collectionId) => _node.GetDataGroupId(collectionId);

    public ValueTask UpsertCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
        _node.ReplicateCatalogAsync(
            RaftCommandCodec.CatalogUpsert(
                Guid.NewGuid(),
                MultiRaftNode.CatalogGroupId,
                collection,
                GetDataGroupId(collection.Id)),
            cancellationToken);

    public ValueTask DeleteCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
        _node.ReplicateCatalogAsync(
            RaftCommandCodec.CatalogDelete(
                Guid.NewGuid(),
                MultiRaftNode.CatalogGroupId,
                collection.Id,
                collection.Name),
            cancellationToken);

    public ValueTask AppendAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default)
    {
        string groupId = GetDataGroupId(collection.Id);
        return _node.ReplicateDataAsync(
            collection.Id,
            RaftCommandCodec.DataBatch(Guid.NewGuid(), groupId, collection, operations),
            cancellationToken);
    }

    public ValueTask AppendBatchAsync(
        IReadOnlyList<CollectionWrite> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        string groupId = GetDataGroupId(writes[0].Collection.Id);
        if (writes.Any(write => !string.Equals(GetDataGroupId(write.Collection.Id), groupId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A replicated shard batch may target only one data group.", nameof(writes));
        }

        Guid routingCollectionId = writes[0].Collection.Id;
        return _node.ReplicateDataAsync(
            routingCollectionId,
            RaftCommandCodec.ShardBatch(Guid.NewGuid(), groupId, writes),
            cancellationToken);
    }

    public ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        _node.ApplyReadBarrierAsync(collectionId, consistency, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _applier.StateChanged -= OnStateChanged;
        await _node.DisposeAsync().ConfigureAwait(false);
    }

    private void OnStateChanged(Guid? collectionId) => StateChanged?.Invoke(collectionId);
}
