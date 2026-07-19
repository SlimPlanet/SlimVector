using System.Net;

namespace SlimVector.Raft;

public sealed class SingleNodeMembershipCoordinator : IClusterMembershipCoordinator
{
    public IReadOnlyList<ClusterMembershipStatus> GetMembershipStatuses() =>
    [
        new ClusterMembershipStatus(
            MultiRaftNode.CatalogGroupId,
            [new RaftMemberStatus(MultiRaftNode.CatalogGroupId, "local", true, false, "Available", 0, 0)],
            null,
            null),
        new ClusterMembershipStatus(
            "data-0",
            [new RaftMemberStatus("data-0", "local", true, false, "Available", 0, 0)],
            null,
            null),
    ];

    public ValueTask AddMemberAsync(string groupId, IPEndPoint endpoint, CancellationToken cancellationToken = default) =>
        Unsupported(cancellationToken);

    public ValueTask PromoteMemberAsync(string groupId, IPEndPoint endpoint, CancellationToken cancellationToken = default) =>
        Unsupported(cancellationToken);

    public ValueTask DemoteMemberAsync(
        string groupId,
        IPEndPoint endpoint,
        int minimumVotingMembers,
        CancellationToken cancellationToken = default) => Unsupported(cancellationToken);

    public ValueTask RemoveMemberAsync(
        string groupId,
        IPEndPoint endpoint,
        int minimumVotingMembers,
        CancellationToken cancellationToken = default) => Unsupported(cancellationToken);

    public ValueTask TransferLeadershipAsync(string groupId, CancellationToken cancellationToken = default) =>
        Unsupported(cancellationToken);

    private static ValueTask Unsupported(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new InvalidOperationException("Dynamic membership is available only in cluster mode."));
    }
}
