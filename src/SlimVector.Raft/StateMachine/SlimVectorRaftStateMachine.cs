using System.Buffers;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using MemoryPack;
using SlimVector.Raft.Commands;

namespace SlimVector.Raft.StateMachine;

internal sealed class SlimVectorRaftStateMachine : SimpleStateMachine
{
    private const int MaximumRememberedCommandIds = 100_000;
    private readonly string _groupId;
    private readonly IRaftCommandApplier _applier;
    private readonly int _snapshotEveryEntries;
    private readonly HashSet<Guid> _appliedCommandIds = [];
    private readonly Queue<Guid> _appliedCommandOrder = [];

    public SlimVectorRaftStateMachine(
        string groupId,
        DirectoryInfo snapshotLocation,
        IRaftCommandApplier applier,
        int snapshotEveryEntries)
        : base(snapshotLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotEveryEntries, 1);
        _groupId = groupId;
        _applier = applier;
        _snapshotEveryEntries = snapshotEveryEntries;
    }

    public long AppliedCommandCount { get; private set; }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        byte[] bytes = await File.ReadAllBytesAsync(snapshotFile.FullName, token).ConfigureAwait(false);
        RaftStateSnapshot snapshot = MemoryPackSerializer.Deserialize<RaftStateSnapshot>(bytes)
            ?? throw new InvalidDataException($"Raft snapshot '{snapshotFile.FullName}' is empty.");
        if (snapshot.FormatVersion != 1 || !string.Equals(snapshot.GroupId, _groupId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raft snapshot '{snapshotFile.FullName}' has an incompatible format or group id.");
        }

        _appliedCommandIds.Clear();
        _appliedCommandOrder.Clear();
        foreach (Guid commandId in snapshot.AppliedCommandIds)
        {
            Remember(commandId);
        }

        AppliedCommandCount = snapshot.AppliedCommandCount;
        await _applier.RestoreSnapshotAsync(_groupId, snapshot.ApplicationSnapshot, token).ConfigureAwait(false);
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        byte[] applicationSnapshot = await _applier.CaptureSnapshotAsync(_groupId, token).ConfigureAwait(false);
        RaftStateSnapshot snapshot = new()
        {
            FormatVersion = 1,
            GroupId = _groupId,
            AppliedCommandIds = _appliedCommandOrder.ToArray(),
            AppliedCommandCount = AppliedCommandCount,
            ApplicationSnapshot = applicationSnapshot,
        };
        byte[] bytes = MemoryPackSerializer.Serialize(snapshot);
        await writer.Invoke(bytes, token).ConfigureAwait(false);
    }

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        // Membership transitions append both a no-op barrier and a configuration entry.
        // They advance the replicated log but are consumed by DotNext's configuration
        // storage rather than by the SlimVector application command codec.
        if (entry.IsConfiguration || entry.Length.GetValueOrDefault() == 0)
        {
            return false;
        }

        if (!entry.TryGetPayload(out ReadOnlySequence<byte> payload))
        {
            throw new InvalidDataException($"Raft log entry {entry.Index} in group '{_groupId}' has no payload.");
        }

        byte[] bytes = payload.IsSingleSegment ? payload.FirstSpan.ToArray() : payload.ToArray();
        RaftCommandEnvelope command = RaftCommandCodec.Deserialize(bytes);
        if (!string.Equals(command.GroupId, _groupId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Raft command '{command.CommandId}' targets group '{command.GroupId}' instead of '{_groupId}'.");
        }

        if (!_appliedCommandIds.Contains(command.CommandId))
        {
            await _applier.ApplyAsync(command, token).ConfigureAwait(false);
            Remember(command.CommandId);
            AppliedCommandCount++;
        }

        return entry.Index % _snapshotEveryEntries == 0;
    }

    private void Remember(Guid commandId)
    {
        if (_appliedCommandIds.Add(commandId))
        {
            _appliedCommandOrder.Enqueue(commandId);
            TrimRememberedCommands();
        }
    }

    private void TrimRememberedCommands()
    {
        while (_appliedCommandOrder.Count > MaximumRememberedCommandIds)
        {
            _appliedCommandIds.Remove(_appliedCommandOrder.Dequeue());
        }
    }
}

[MemoryPackable]
internal sealed partial class RaftStateSnapshot
{
    public int FormatVersion { get; set; }

    public string GroupId { get; set; } = string.Empty;

    public Guid[] AppliedCommandIds { get; set; } = [];

    public long AppliedCommandCount { get; set; }

    public byte[] ApplicationSnapshot { get; set; } = [];
}
