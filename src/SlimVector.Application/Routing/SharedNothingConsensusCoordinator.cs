using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Routing;

public sealed class SharedNothingConsensusCoordinator : IConsensusCoordinator
{
    private readonly IConsensusCoordinator _local;
    private readonly ILocalRaftGroupManager _localGroups;
    private readonly IClusterTopologyStore _topologyStore;
    private readonly IDataNodeRpcClient _rpc;
    private readonly ILocalRaftCommandReplicator _localReplicator;
    private readonly ICatalogCacheSynchronizer _catalogCache;
    private readonly string _localNodeId;

    public SharedNothingConsensusCoordinator(
        IConsensusCoordinator local,
        ILocalRaftGroupManager localGroups,
        IClusterTopologyStore topologyStore,
        IDataNodeRpcClient rpc,
        ILocalRaftCommandReplicator localReplicator,
        ICatalogCacheSynchronizer catalogCache,
        Microsoft.Extensions.Options.IOptions<RaftOptions> raftOptions)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(localGroups);
        ArgumentNullException.ThrowIfNull(topologyStore);
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(localReplicator);
        ArgumentNullException.ThrowIfNull(catalogCache);
        ArgumentNullException.ThrowIfNull(raftOptions);
        _local = local;
        _localGroups = localGroups;
        _topologyStore = topologyStore;
        _rpc = rpc;
        _localReplicator = localReplicator;
        _catalogCache = catalogCache;
        _localNodeId = raftOptions.Value.NodeId;
    }

    public event Action<Guid?>? StateChanged
    {
        add => _local.StateChanged += value;
        remove => _local.StateChanged -= value;
    }

    public ExecutionMode Mode => _local.Mode;

    public bool IsReady
    {
        get
        {
            if (_local.Mode == ExecutionMode.SingleNode)
            {
                return _local.IsReady;
            }

            ClusterTopology topology;
            try
            {
                topology = _topologyStore.GetSnapshot();
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (topology.Nodes.Length == 0)
            {
                return false;
            }

            bool localCatalogMember = GetCatalogNodeIds(topology).Contains(_localNodeId, StringComparer.Ordinal);
            return _local.GetStatuses().All(status =>
                (!localCatalogMember && string.Equals(status.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal)) ||
                status.LeaderEndpoint is not null);
        }
    }

    public IReadOnlyList<RaftGroupStatus> GetStatuses() => _local.GetStatuses();

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        _local.StartAsync(cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _local.StopAsync(cancellationToken);

    public string GetDataGroupId(Guid collectionId) =>
        RaftGroupAssignment.GetDataGroupId(collectionId, GetActiveDataGroupIds());

    public CollectionPlacement CreateInitialPlacement(
        Guid collectionId,
        int virtualShardCount = CollectionPlacement.DefaultVirtualShardCount) =>
        CollectionPlacement.Create(collectionId, GetActiveDataGroupIds(), virtualShardCount);

    public ShardRoute GetShardRoute(CollectionDefinition collection, string documentId) =>
        collection.Placement?.Resolve(collection.Id, documentId) ??
        new ShardRoute(0, GetDataGroupId(collection.Id), 0);

    public IReadOnlyList<ShardRoute> GetReadRoutes(CollectionDefinition collection) =>
        collection.Placement?.ReadRoutes() ?? [new ShardRoute(0, GetDataGroupId(collection.Id), 0)];

    public ValueTask UpsertCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default) => ReplicateCatalogAsync(
            RaftCommandCodec.CatalogUpsert(
                Guid.NewGuid(),
                MultiRaftNode.CatalogGroupId,
                collection,
                collection.Placement is { } placement
                    ? placement.ReadRoutes()[0].DataGroupId
                    : GetDataGroupId(collection.Id)),
            cancellationToken);

    public ValueTask DeleteCollectionAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken = default) => ReplicateCatalogAsync(
            RaftCommandCodec.CatalogDelete(
                Guid.NewGuid(),
                MultiRaftNode.CatalogGroupId,
                collection.Id,
                collection.Name),
            cancellationToken);

    public ValueTask ReplaceTopologyAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken = default) => ReplicateCatalogAsync(
            RaftCommandCodec.TopologyReplace(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, topology),
            cancellationToken);

    public ValueTask AppendAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default) =>
        AppendBatchAsync([new CollectionWrite(collection, operations)], cancellationToken);

    public async ValueTask AppendBatchAsync(
        IReadOnlyList<CollectionWrite> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return;
        }

        CollectionWrite[] routed = writes.Select(Normalize).ToArray();
        string[] groupIds = routed.Select(static write => write.Route.DataGroupId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (groupIds.Length != 1)
        {
            throw new DomainException(
                ErrorCodes.CrossShardAtomicUnsupported,
                "A consensus batch may target only one physical data group.");
        }

        string groupId = groupIds[0];
        if (_localGroups.GetHostedDataGroupIds().Contains(groupId, StringComparer.Ordinal))
        {
            await _local.AppendBatchAsync(routed, cancellationToken).ConfigureAwait(false);
            return;
        }

        RaftCommandEnvelope command = RaftCommandCodec.ShardBatch(Guid.NewGuid(), groupId, routed);
        await _rpc.ReplicateAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        if (_local.Mode == ExecutionMode.SingleNode || collectionId.HasValue)
        {
            if (_local.Mode == ExecutionMode.SingleNode)
            {
                await _local.ApplyReadBarrierAsync(collectionId, consistency, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ClusterTopology topology = _topologyStore.GetSnapshot();
        if (GetCatalogNodeIds(topology).Contains(_localNodeId, StringComparer.Ordinal))
        {
            await _local.ApplyReadBarrierAsync(null, consistency, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _catalogCache.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask ApplyDataGroupReadBarrierAsync(
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default) =>
        _localGroups.GetHostedDataGroupIds().Contains(dataGroupId, StringComparer.Ordinal)
            ? _local.ApplyDataGroupReadBarrierAsync(dataGroupId, consistency, cancellationToken)
            : ValueTask.CompletedTask;

    public async ValueTask ApplyReadBarriersAsync(
        CollectionDefinition collection,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> hosted = _localGroups.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
        foreach (string groupId in GetReadRoutes(collection)
                     .Select(static route => route.DataGroupId)
                     .Distinct(StringComparer.Ordinal)
                     .Where(hosted.Contains))
        {
            await _local.ApplyDataGroupReadBarrierAsync(groupId, consistency, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _local.DisposeAsync();

    private async ValueTask ReplicateCatalogAsync(
        RaftCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        if (_local.Mode == ExecutionMode.SingleNode)
        {
            await _localReplicator.ReplicateLocalAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        ClusterTopology topology = _topologyStore.GetSnapshot();
        bool localCatalogMember = GetCatalogNodeIds(topology).Contains(_localNodeId, StringComparer.Ordinal) ||
            topology.Nodes.Length == 0 &&
            _local.GetStatuses().Any(status =>
                string.Equals(status.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal));
        if (localCatalogMember)
        {
            try
            {
                await _localReplicator.ReplicateLocalAsync(command, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ConsensusUnavailableException exception) when (exception.Kind == ConsensusFailureKind.NotLeader)
            {
                // Retry the exact idempotent command through another catalog voter.
            }
        }

        await _rpc.ReplicateAsync(command, cancellationToken).ConfigureAwait(false);
        await _catalogCache.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private CollectionWrite Normalize(CollectionWrite write)
    {
        if (write.Operations.Count == 0)
        {
            throw new ArgumentException("A collection write must contain at least one operation.", nameof(write));
        }

        ShardRoute first = write.Route.RoutingEpoch > 0
            ? write.Route
            : GetShardRoute(write.Collection, write.Operations[0].Id);
        if (write.Operations.Any(operation =>
                !string.Equals(GetShardRoute(write.Collection, operation.Id).DataGroupId, first.DataGroupId, StringComparison.Ordinal)))
        {
            throw new DomainException(
                ErrorCodes.CrossShardAtomicUnsupported,
                "A collection write may target only one physical data group.");
        }

        return write with { Route = first };
    }

    private IReadOnlyList<string> GetActiveDataGroupIds()
    {
        ClusterTopology topology;
        try
        {
            topology = _topologyStore.GetSnapshot();
        }
        catch (NotSupportedException)
        {
            return _localGroups.GetHostedDataGroupIds();
        }

        string[] groups = topology.DataGroups
            .Where(static group => group.State is DataGroupState.Active or DataGroupState.Creating or DataGroupState.Relocating)
            .Select(static group => group.GroupId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return groups.Length == 0 ? _localGroups.GetHostedDataGroupIds() : groups;
    }

    private static string[] GetCatalogNodeIds(ClusterTopology topology) => topology.CatalogNodeIds.Length > 0
        ? topology.CatalogNodeIds
        : topology.Nodes
            .Where(static node => node.State != ClusterNodeState.Removed)
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .Take(3)
            .Select(static node => node.NodeId)
            .ToArray();
}
