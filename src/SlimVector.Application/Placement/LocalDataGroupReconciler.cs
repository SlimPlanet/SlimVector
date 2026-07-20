using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Placement;

public interface ILocalDataGroupProvisioner
{
    ValueTask PrepareJoiningReplicaAsync(
        string groupId,
        string localRaftEndpoint,
        CancellationToken cancellationToken = default);
}

internal sealed class SingleNodeLocalRaftGroupManager : ILocalRaftGroupManager
{
    public IReadOnlyList<string> GetHostedDataGroupIds() => ["data-0"];

    public ValueTask AddLocalDataGroupAsync(
        RaftGroupNodeOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveLocalDataGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class LocalDataGroupReconciler(
    IClusterTopologyStore topologyStore,
    ILocalRaftGroupManager groupManager,
    IDataGroupStorage dataGroupStorage,
    IOptions<RaftOptions> raftOptions,
    IOptions<ClusterMembershipOptions> membershipOptions,
    IOptions<StorageOptions> storageOptions,
    IOptions<RebalancingOptions> rebalancingOptions) : BackgroundService, ILocalDataGroupProvisioner
{
    public async ValueTask PrepareJoiningReplicaAsync(
        string groupId,
        string localRaftEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (!string.Equals(groupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal) &&
            !groupManager.GetHostedDataGroupIds().Contains(groupId, StringComparer.Ordinal))
        {
            await dataGroupStorage.EnsureDataGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
            await groupManager.AddLocalDataGroupAsync(
                CreateJoiningOptions(groupId, Parse(localRaftEndpoint)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (raftOptions.Value.Mode == ExecutionMode.SingleNode)
        {
            await dataGroupStorage.EnsureDataGroupAsync("data-0", stoppingToken).ConfigureAwait(false);
            return;
        }

        using PeriodicTimer timer = new(rebalancingOptions.Value.ReconcileInterval);
        do
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async ValueTask ReconcileAsync(CancellationToken cancellationToken)
    {
        ClusterTopology topology = await topologyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        string localNodeId = raftOptions.Value.NodeId;
        Dictionary<string, DataGroupDescriptor> desired = topology.DataGroups
            .Where(group => group.State != DataGroupState.Removed && group.Replicas.Any(replica =>
                string.Equals(replica.NodeId, localNodeId, StringComparison.Ordinal)))
            .ToDictionary(static group => group.GroupId, StringComparer.Ordinal);
        HashSet<string> hosted = groupManager.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
        foreach (DataGroupDescriptor group in desired.Values.Where(group => !hosted.Contains(group.GroupId)))
        {
            await dataGroupStorage.EnsureDataGroupAsync(group.GroupId, cancellationToken).ConfigureAwait(false);
            await groupManager.AddLocalDataGroupAsync(CreateOptions(group), cancellationToken).ConfigureAwait(false);
        }

        foreach (string obsolete in hosted.Where(groupId => !desired.ContainsKey(groupId)))
        {
            await groupManager.RemoveLocalDataGroupAsync(obsolete, cancellationToken).ConfigureAwait(false);
            await dataGroupStorage.RemoveDataGroupAsync(obsolete, cancellationToken).ConfigureAwait(false);
        }
    }

    private RaftGroupNodeOptions CreateOptions(DataGroupDescriptor group)
    {
        RaftOptions raft = raftOptions.Value;
        ClusterMembershipOptions membership = membershipOptions.Value;
        DataGroupReplica local = group.Replicas.Single(replica =>
            string.Equals(replica.NodeId, raft.NodeId, StringComparison.Ordinal));
        IPEndPoint localEndpoint = Parse(local.RaftEndpoint);
        int upperElectionTimeout = checked((int)Math.Ceiling(raft.ElectionTimeout.TotalMilliseconds));
        int lowerElectionTimeout = Math.Max(50, upperElectionTimeout / 2);
        return new RaftGroupNodeOptions
        {
            GroupId = group.GroupId,
            LocalEndpoint = localEndpoint,
            Members = group.Replicas.Select(static replica => Parse(replica.RaftEndpoint)).ToArray(),
            StoragePath = Path.Combine(Path.GetFullPath(storageOptions.Value.Path), "raft", raft.NodeId),
            LowerElectionTimeoutMilliseconds = lowerElectionTimeout,
            UpperElectionTimeoutMilliseconds = upperElectionTimeout,
            HeartbeatThreshold = raft.HeartbeatInterval.TotalMilliseconds / lowerElectionTimeout,
            RequestTimeout = raft.ElectionTimeout * 2,
            SnapshotEveryEntries = raft.SnapshotEveryEntries,
            TransmissionBlockSize = raft.TransmissionBlockSize,
            WarmupRounds = membership.WarmupRounds,
            MaximumReplicationLag = membership.MaximumCatchUpLagEntries,
            StartAsJoiningMember = false,
        };
    }

    private RaftGroupNodeOptions CreateJoiningOptions(string groupId, IPEndPoint localEndpoint)
    {
        RaftOptions raft = raftOptions.Value;
        ClusterMembershipOptions membership = membershipOptions.Value;
        int upperElectionTimeout = checked((int)Math.Ceiling(raft.ElectionTimeout.TotalMilliseconds));
        int lowerElectionTimeout = Math.Max(50, upperElectionTimeout / 2);
        return new RaftGroupNodeOptions
        {
            GroupId = groupId,
            LocalEndpoint = localEndpoint,
            Members = [],
            StoragePath = Path.Combine(Path.GetFullPath(storageOptions.Value.Path), "raft", raft.NodeId),
            LowerElectionTimeoutMilliseconds = lowerElectionTimeout,
            UpperElectionTimeoutMilliseconds = upperElectionTimeout,
            HeartbeatThreshold = raft.HeartbeatInterval.TotalMilliseconds / lowerElectionTimeout,
            RequestTimeout = raft.ElectionTimeout * 2,
            SnapshotEveryEntries = raft.SnapshotEveryEntries,
            TransmissionBlockSize = raft.TransmissionBlockSize,
            WarmupRounds = membership.WarmupRounds,
            MaximumReplicationLag = membership.MaximumCatchUpLagEntries,
            StartAsJoiningMember = true,
        };
    }

    private static IPEndPoint Parse(string endpoint) =>
        RaftOptionsValidator.TryParseEndpoint(endpoint, out IPEndPoint? parsed)
            ? parsed!
            : throw new DomainException(ErrorCodes.InvalidPlacement, $"Raft endpoint '{endpoint}' is invalid.");
}
