using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft;

public sealed class DistributedConsensusCoordinator : IConsensusCoordinator, IClusterMembershipCoordinator
{
    private readonly MultiRaftNode _node;
    private readonly StorageRaftCommandApplier _applier;
    private readonly object _membershipSync = new();
    private readonly Dictionary<string, (string State, string Endpoint)> _membershipChanges = new(StringComparer.Ordinal);

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

    public IReadOnlyList<ClusterMembershipStatus> GetMembershipStatuses() => _node.GetMembershipStatuses()
        .Select(status =>
        {
            lock (_membershipSync)
            {
                return _membershipChanges.TryGetValue(status.GroupId, out (string State, string Endpoint) change)
                    ? status with { ChangeState = change.State, ChangeEndpoint = change.Endpoint }
                    : status;
            }
        })
        .ToArray();

    public async ValueTask AddMemberAsync(
        string groupId,
        System.Net.IPEndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        RaftGroupNode group = GetRequiredGroup(groupId);
        SetMembershipChange(groupId, "catching-up", endpoint);
        try
        {
            bool added = await group.AddMemberAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!added)
            {
                throw Conflict($"Raft group '{groupId}' rejected member '{endpoint}'.");
            }
        }
        finally
        {
            ClearMembershipChange(groupId);
        }
    }

    public ValueTask PromoteMemberAsync(
        string groupId,
        System.Net.IPEndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RaftGroupNode group = GetRequiredGroup(groupId);
        if (!group.GetMemberStatuses().Any(status => string.Equals(status.Endpoint, endpoint.ToString(), StringComparison.Ordinal)))
        {
            throw new DomainException(
                ErrorCodes.MembershipMemberNotFound,
                $"Member '{endpoint}' has not completed DotNext warmup in group '{groupId}'.");
        }

        // DotNext commits a member as a voter only after its warmup/catch-up rounds complete.
        return ValueTask.CompletedTask;
    }

    public ValueTask DemoteMemberAsync(
        string groupId,
        System.Net.IPEndPoint endpoint,
        int minimumVotingMembers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RaftGroupNode group = GetRequiredGroup(groupId);
        ValidateSafeRemoval(group, endpoint, minimumVotingMembers);
        SetMembershipChange(groupId, "demoting", endpoint);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RemoveMemberAsync(
        string groupId,
        System.Net.IPEndPoint endpoint,
        int minimumVotingMembers,
        CancellationToken cancellationToken = default)
    {
        RaftGroupNode group = GetRequiredGroup(groupId);
        ValidateSafeRemoval(group, endpoint, minimumVotingMembers);
        RaftMemberStatus target = group.GetMemberStatuses().Single(status =>
            string.Equals(status.Endpoint, endpoint.ToString(), StringComparison.Ordinal));
        if (target.IsLeader)
        {
            throw Conflict("Transfer leadership before removing the current group leader.");
        }

        SetRemovalChange(groupId, endpoint);
        try
        {
            bool removed = await group.RemoveMemberAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!removed)
            {
                throw Conflict($"Raft group '{groupId}' rejected removal of member '{endpoint}'.");
            }
        }
        finally
        {
            ClearMembershipChange(groupId);
        }
    }

    public async ValueTask TransferLeadershipAsync(string groupId, CancellationToken cancellationToken = default)
    {
        RaftGroupNode group = GetRequiredGroup(groupId);
        if (!group.IsLeader)
        {
            throw new ConsensusUnavailableException(
                groupId,
                ConsensusFailureKind.NotLeader,
                group.LeaderEndpoint?.ToString(),
                new InvalidOperationException("Leadership transfer must be requested from the leader."));
        }

        bool transferred = await group.TransferLeadershipAsync(cancellationToken).ConfigureAwait(false);
        if (!transferred)
        {
            throw Conflict($"Raft group '{groupId}' could not transfer leadership.");
        }
    }

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

    private static void ValidateSafeRemoval(
        RaftGroupNode group,
        System.Net.IPEndPoint endpoint,
        int minimumVotingMembers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumVotingMembers, 1);
        IReadOnlyList<RaftMemberStatus> members = group.GetMemberStatuses();
        if (!members.Any(status => string.Equals(status.Endpoint, endpoint.ToString(), StringComparison.Ordinal)))
        {
            throw new DomainException(
                ErrorCodes.MembershipMemberNotFound,
                $"Member '{endpoint}' is not part of group '{group.GroupId}'.");
        }

        if (members.Count - 1 < minimumVotingMembers)
        {
            throw Conflict(
                $"Removing '{endpoint}' would leave {members.Count - 1} voters, below the required minimum of {minimumVotingMembers}.");
        }
    }

    private void SetMembershipChange(string groupId, string state, System.Net.IPEndPoint endpoint)
    {
        lock (_membershipSync)
        {
            if (_membershipChanges.ContainsKey(groupId))
            {
                throw Conflict($"Raft group '{groupId}' already has a membership change in progress.");
            }

            _membershipChanges.Add(groupId, (state, endpoint.ToString()));
        }
    }

    private void ClearMembershipChange(string groupId)
    {
        lock (_membershipSync)
        {
            _membershipChanges.Remove(groupId);
        }
    }

    private void SetRemovalChange(string groupId, System.Net.IPEndPoint endpoint)
    {
        lock (_membershipSync)
        {
            if (_membershipChanges.TryGetValue(groupId, out (string State, string Endpoint) change))
            {
                if (!string.Equals(change.State, "demoting", StringComparison.Ordinal) ||
                    !string.Equals(change.Endpoint, endpoint.ToString(), StringComparison.Ordinal))
                {
                    throw Conflict($"Raft group '{groupId}' already has a membership change in progress.");
                }

                _membershipChanges[groupId] = ("removing", endpoint.ToString());
                return;
            }

            _membershipChanges.Add(groupId, ("removing", endpoint.ToString()));
        }
    }

    private RaftGroupNode GetRequiredGroup(string groupId)
    {
        try
        {
            return _node.GetGroup(groupId);
        }
        catch (KeyNotFoundException exception)
        {
            throw new DomainException(ErrorCodes.MembershipMemberNotFound, exception.Message);
        }
    }

    private static DomainException Conflict(string message) => new(ErrorCodes.MembershipConflict, message);
}
