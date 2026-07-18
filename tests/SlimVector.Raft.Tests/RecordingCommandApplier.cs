using System.Collections.Concurrent;
using SlimVector.Raft.Commands;

namespace SlimVector.Raft.Tests;

internal sealed class RecordingCommandApplier : IRaftCommandApplier
{
    private readonly ConcurrentQueue<RaftCommandEnvelope> _commands = new();
    private readonly ConcurrentDictionary<Guid, byte> _appliedCommandIds = new();

    public IReadOnlyList<RaftCommandEnvelope> Commands => _commands.ToArray();

    public ValueTask ApplyAsync(RaftCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_appliedCommandIds.TryAdd(command.CommandId, 0))
        {
            _commands.Enqueue(command);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> CaptureSnapshotAsync(string groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Array.Empty<byte>());
    }

    public ValueTask RestoreSnapshotAsync(
        string groupId,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
