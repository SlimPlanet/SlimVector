using System.Net;

namespace SlimVector.Raft;

public sealed record RaftGroupNodeOptions
{
    public required string GroupId { get; init; }

    public required IPEndPoint LocalEndpoint { get; init; }

    public required IReadOnlyList<IPEndPoint> Members { get; init; }

    public required string StoragePath { get; init; }

    public int LowerElectionTimeoutMilliseconds { get; init; } = 300;

    public int UpperElectionTimeoutMilliseconds { get; init; } = 600;

    public double HeartbeatThreshold { get; init; } = 0.5;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int SnapshotEveryEntries { get; init; } = 10_000;

    public int TransmissionBlockSize { get; init; } = 64 * 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(GroupId);
        ArgumentNullException.ThrowIfNull(LocalEndpoint);
        ArgumentNullException.ThrowIfNull(Members);
        ArgumentException.ThrowIfNullOrWhiteSpace(StoragePath);
        if (Members.Count == 0 || !Members.Contains(LocalEndpoint))
        {
            throw new ArgumentException("Raft members must contain the local endpoint.", nameof(Members));
        }

        if (Members.Distinct().Count() != Members.Count)
        {
            throw new ArgumentException("Raft members must be unique.", nameof(Members));
        }

        if (LowerElectionTimeoutMilliseconds < 50 || UpperElectionTimeoutMilliseconds <= LowerElectionTimeoutMilliseconds)
        {
            throw new ArgumentException("Raft election timeout bounds are inconsistent.", nameof(LowerElectionTimeoutMilliseconds));
        }

        if (HeartbeatThreshold is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatThreshold), "Raft heartbeat threshold must be between 0 and 1.");
        }

        if (RequestTimeout <= TimeSpan.Zero || SnapshotEveryEntries < 1 || TransmissionBlockSize < 1_024)
        {
            throw new ArgumentException("Raft request, snapshot, and transmission settings must be positive.");
        }
    }
}
