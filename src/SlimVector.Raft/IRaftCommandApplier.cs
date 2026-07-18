using SlimVector.Raft.Commands;

namespace SlimVector.Raft;

public interface IRaftCommandApplier
{
    ValueTask ApplyAsync(RaftCommandEnvelope command, CancellationToken cancellationToken);

    ValueTask<byte[]> CaptureSnapshotAsync(string groupId, CancellationToken cancellationToken);

    ValueTask RestoreSnapshotAsync(
        string groupId,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken);
}
