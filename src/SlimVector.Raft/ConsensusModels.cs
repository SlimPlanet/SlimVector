namespace SlimVector.Raft;

using SlimVector.Domain;
using SlimVector.Storage;

public sealed record CollectionWrite(
    CollectionDefinition Collection,
    IReadOnlyList<StorageOperation> Operations,
    ShardRoute Route = default);

public sealed record RaftGroupStatus
{
    public required string GroupId { get; init; }

    public required string LocalEndpoint { get; init; }

    public string? LeaderEndpoint { get; init; }

    public required bool IsLeader { get; init; }

    public required long Term { get; init; }

    public required long LastAppliedIndex { get; init; }

    public required long LastCommittedIndex { get; init; }

    public required long AppliedCommandCount { get; init; }
}

public enum ConsensusFailureKind
{
    NotLeader,
    QuorumUnavailable,
}

public sealed class ConsensusUnavailableException : Exception
{
    public ConsensusUnavailableException(
        string groupId,
        ConsensusFailureKind kind,
        string? leaderEndpoint,
        Exception innerException)
        : base(CreateMessage(groupId, kind, leaderEndpoint), innerException)
    {
        GroupId = groupId;
        Kind = kind;
        LeaderEndpoint = leaderEndpoint;
    }

    public string GroupId { get; }

    public ConsensusFailureKind Kind { get; }

    public string? LeaderEndpoint { get; }

    private static string CreateMessage(string groupId, ConsensusFailureKind kind, string? leaderEndpoint) => kind switch
    {
        ConsensusFailureKind.NotLeader when leaderEndpoint is not null =>
            $"The local node is not leader of Raft group '{groupId}'. Current leader: {leaderEndpoint}.",
        ConsensusFailureKind.NotLeader => $"The local node is not leader of Raft group '{groupId}'.",
        ConsensusFailureKind.QuorumUnavailable => $"Raft group '{groupId}' cannot currently reach quorum.",
        _ => $"Raft group '{groupId}' is unavailable.",
    };
}
