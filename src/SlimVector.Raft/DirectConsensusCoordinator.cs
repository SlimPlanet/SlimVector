using System.Collections.Concurrent;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft;

public sealed class DirectConsensusCoordinator : IConsensusCoordinator, ILocalRaftCommandReplicator
{
    private const string DataGroupId = "data-0";
    private readonly IRaftCommandApplier _applier;
    private readonly ConcurrentDictionary<Guid, byte> _appliedCommandIds = new();

    public DirectConsensusCoordinator(IRaftCommandApplier applier)
    {
        ArgumentNullException.ThrowIfNull(applier);
        _applier = applier;
        if (applier is StorageRaftCommandApplier storageApplier)
        {
            storageApplier.StateChanged += OnStateChanged;
        }
    }

    public event Action<Guid?>? StateChanged;

    public ExecutionMode Mode => ExecutionMode.SingleNode;

    public bool IsReady => true;

    public IReadOnlyList<RaftGroupStatus> GetStatuses() =>
    [
        Status(MultiRaftNode.CatalogGroupId),
        Status(DataGroupId),
    ];

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public string GetDataGroupId(Guid collectionId) => DataGroupId;

    public ValueTask UpsertCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
        ApplyAsync(
            RaftCommandCodec.CatalogUpsert(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, collection, DataGroupId),
            cancellationToken);

    public ValueTask DeleteCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
        ApplyAsync(
            RaftCommandCodec.CatalogDelete(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, collection.Id, collection.Name),
            cancellationToken);

    public ValueTask ReplaceTopologyAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(
            RaftCommandCodec.TopologyReplace(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, topology),
            cancellationToken);

    public ValueTask AppendAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(RaftCommandCodec.DataBatch(Guid.NewGuid(), DataGroupId, collection, operations), cancellationToken);

    public ValueTask AppendBatchAsync(
        IReadOnlyList<CollectionWrite> writes,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(RaftCommandCodec.ShardBatch(Guid.NewGuid(), DataGroupId, writes), cancellationToken);

    public ValueTask ReplicateLocalAsync(
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyAsync(command, cancellationToken);
    }

    public ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ApplyDataGroupReadBarrierAsync(
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataGroupId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_applier is StorageRaftCommandApplier storageApplier)
        {
            storageApplier.StateChanged -= OnStateChanged;
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask ApplyAsync(RaftCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (_appliedCommandIds.TryAdd(command.CommandId, 0))
        {
            await _applier.ApplyAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnStateChanged(Guid? collectionId) => StateChanged?.Invoke(collectionId);

    private static RaftGroupStatus Status(string groupId) => new()
    {
        GroupId = groupId,
        LocalEndpoint = "local",
        LeaderEndpoint = "local",
        IsLeader = true,
        Term = 0,
        LastAppliedIndex = 0,
        LastCommittedIndex = 0,
        AppliedCommandCount = 0,
    };
}
