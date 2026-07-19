using System.Net;

namespace SlimVector.Raft;

public enum ClusterMemberRole
{
    CatchingUp,
    Voter,
    Demoting,
    Removed,
}

public sealed record RaftMemberStatus(
    string GroupId,
    string Endpoint,
    bool IsLeader,
    bool IsRemote,
    string TransportStatus,
    long? MatchIndex,
    long? ReplicationLag);

public sealed record ClusterMembershipStatus(
    string GroupId,
    IReadOnlyList<RaftMemberStatus> Members,
    string? ChangeState,
    string? ChangeEndpoint);

public interface IClusterMembershipCoordinator
{
    IReadOnlyList<ClusterMembershipStatus> GetMembershipStatuses();

    ValueTask AddMemberAsync(string groupId, IPEndPoint endpoint, CancellationToken cancellationToken = default);

    ValueTask PromoteMemberAsync(string groupId, IPEndPoint endpoint, CancellationToken cancellationToken = default);

    ValueTask DemoteMemberAsync(string groupId, IPEndPoint endpoint, int minimumVotingMembers, CancellationToken cancellationToken = default);

    ValueTask RemoveMemberAsync(string groupId, IPEndPoint endpoint, int minimumVotingMembers, CancellationToken cancellationToken = default);

    ValueTask TransferLeadershipAsync(string groupId, CancellationToken cancellationToken = default);
}
