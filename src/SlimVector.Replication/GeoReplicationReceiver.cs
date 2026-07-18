using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using SlimVector.Raft;
using SlimVector.Raft.Commands;

namespace SlimVector.Replication;

public sealed class GeoReplicationReceiver : IGeoReplicationReceiver, IDisposable
{
    private const int MaximumReceiptsPerOrigin = 10_000;
    private readonly GeoReplicationSettings _settings;
    private readonly IConsensusCoordinator _localConsensus;
    private readonly byte[] _sharedSecret;
    private readonly string _checkpointPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GeoReplicationCheckpoint? _checkpoint;
    private long _receivedEvents;
    private long _duplicateEvents;
    private long _divergenceEvents;

    public GeoReplicationReceiver(
        GeoReplicationSettings settings,
        IConsensusCoordinator localConsensus)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localConsensus);
        _settings = settings;
        _localConsensus = localConsensus;
        _sharedSecret = Encoding.UTF8.GetBytes(settings.SharedSecret);
        _checkpointPath = Path.GetFullPath(Path.Combine(settings.StatePath, "inbox.checkpoint"));
    }

    public bool AcceptIncoming => _settings.AcceptIncoming;

    public async ValueTask<GeoReplicationReceiveResult> ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        if (!AcceptIncoming)
        {
            throw new InvalidOperationException("Incoming geographic replication is disabled.");
        }

        ValidateSignature(payload.Span, signature);
        GeoReplicationEvent replicationEvent = MemoryPackSerializer.Deserialize<GeoReplicationEvent>(payload.Span)
            ?? throw new InvalidDataException("The geographic replication payload is empty.");
        if (replicationEvent.FormatVersion != 1 || replicationEvent.EventId == Guid.Empty ||
            replicationEvent.Sequence < 1 || string.IsNullOrWhiteSpace(replicationEvent.OriginId) ||
            replicationEvent.Writes.Length == 0 || !Enum.IsDefined(replicationEvent.Kind))
        {
            throw new InvalidDataException("The geographic replication payload is malformed.");
        }

        byte[] payloadHash = SHA256.HashData(payload.Span);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GeoReplicationCheckpoint checkpoint = await LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            GeoOriginCheckpoint? origin = checkpoint.Origins.FirstOrDefault(item =>
                string.Equals(item.OriginId, replicationEvent.OriginId, StringComparison.Ordinal));
            if (origin is null)
            {
                origin = new GeoOriginCheckpoint { OriginId = replicationEvent.OriginId };
                checkpoint.Origins = [.. checkpoint.Origins, origin];
            }

            if (replicationEvent.Sequence <= origin.LastSequence)
            {
                GeoEventReceipt? receipt = origin.Receipts.FirstOrDefault(item => item.Sequence == replicationEvent.Sequence);
                if (receipt is not null && receipt.EventId == replicationEvent.EventId &&
                    CryptographicOperations.FixedTimeEquals(receipt.PayloadHash, payloadHash))
                {
                    Interlocked.Increment(ref _duplicateEvents);
                    return GeoReplicationReceiveResult.Duplicate;
                }

                Interlocked.Increment(ref _divergenceEvents);
                throw new GeoReplicationDivergenceException(
                    $"Origin '{replicationEvent.OriginId}' reused sequence {replicationEvent.Sequence} with different content.");
            }

            if (replicationEvent.Sequence != origin.LastSequence + 1)
            {
                Interlocked.Increment(ref _divergenceEvents);
                throw new GeoReplicationDivergenceException(
                    $"Origin '{replicationEvent.OriginId}' sent sequence {replicationEvent.Sequence}; expected {origin.LastSequence + 1}.");
            }

            await ApplyAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
            origin.LastSequence = replicationEvent.Sequence;
            origin.Receipts = [
                .. origin.Receipts
                    .Append(new GeoEventReceipt
                    {
                        Sequence = replicationEvent.Sequence,
                        EventId = replicationEvent.EventId,
                        PayloadHash = payloadHash,
                    })
                    .TakeLast(MaximumReceiptsPerOrigin),
            ];
            await PersistCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _receivedEvents);
            return GeoReplicationReceiveResult.Applied;
        }
        finally
        {
            _lock.Release();
        }
    }

    public GeoReplicationMetrics GetMetrics() => new()
    {
        PendingEvents = 0,
        SentEvents = 0,
        FailedAttempts = 0,
        ReceivedEvents = Volatile.Read(ref _receivedEvents),
        DuplicateEvents = Volatile.Read(ref _duplicateEvents),
        DivergenceEvents = Volatile.Read(ref _divergenceEvents),
        ReplicationLag = TimeSpan.Zero,
        LastError = null,
    };

    public void Dispose() => _lock.Dispose();

    private async ValueTask ApplyAsync(GeoReplicationEvent replicationEvent, CancellationToken cancellationToken)
    {
        CollectionWrite[] writes = replicationEvent.Writes.Select(static write => new CollectionWrite(
            RaftCommandCodec.ToDomain(write.Collection),
            write.Operations.Select(RaftCommandCodec.ToStorage).ToArray())).ToArray();
        switch (replicationEvent.Kind)
        {
            case GeoReplicationEventKind.CollectionUpsert:
                foreach (CollectionWrite write in writes)
                {
                    await _localConsensus.UpsertCollectionAsync(write.Collection, cancellationToken).ConfigureAwait(false);
                }

                break;
            case GeoReplicationEventKind.CollectionDelete:
                foreach (CollectionWrite write in writes)
                {
                    await _localConsensus.DeleteCollectionAsync(write.Collection, cancellationToken).ConfigureAwait(false);
                }

                break;
            case GeoReplicationEventKind.DataWrites:
                foreach (IGrouping<string, CollectionWrite> group in writes.GroupBy(
                             write => _localConsensus.GetDataGroupId(write.Collection.Id),
                             StringComparer.Ordinal))
                {
                    await _localConsensus.AppendBatchAsync(group.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                throw new InvalidDataException($"Unknown geographic replication event kind '{replicationEvent.Kind}'.");
        }
    }

    private void ValidateSignature(ReadOnlySpan<byte> payload, string signature)
    {
        byte[] expected = HMACSHA256.HashData(_sharedSecret, payload);
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            throw new UnauthorizedAccessException("The geographic replication signature is invalid.");
        }

        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new UnauthorizedAccessException("The geographic replication signature is invalid.");
        }
    }

    private async ValueTask<GeoReplicationCheckpoint> LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpoint is not null)
        {
            return _checkpoint;
        }

        if (!File.Exists(_checkpointPath))
        {
            return _checkpoint = new GeoReplicationCheckpoint();
        }

        byte[] bytes = await File.ReadAllBytesAsync(_checkpointPath, cancellationToken).ConfigureAwait(false);
        GeoReplicationCheckpoint checkpoint = MemoryPackSerializer.Deserialize<GeoReplicationCheckpoint>(bytes)
            ?? throw new InvalidDataException("The geographic replication checkpoint is empty.");
        if (checkpoint.FormatVersion != 1)
        {
            throw new InvalidDataException($"Replication checkpoint version '{checkpoint.FormatVersion}' is unsupported.");
        }

        return _checkpoint = checkpoint;
    }

    private async ValueTask PersistCheckpointAsync(
        GeoReplicationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_checkpointPath)!);
        string temporary = _checkpointPath + $".{Guid.NewGuid():N}.partial";
        byte[] bytes = MemoryPackSerializer.Serialize(checkpoint);
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _checkpointPath, overwrite: true);
        _checkpoint = checkpoint;
    }
}
