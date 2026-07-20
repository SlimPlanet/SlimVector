using SlimVector.Raft.Commands;

namespace SlimVector.Raft;

public interface ILocalRaftCommandReplicator
{
    ValueTask ReplicateLocalAsync(
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default);
}
