using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Replication;

public sealed class GeoReplicationService : IGeoReplicationService
{
    private const string EndpointPath = "/internal/geo/replicate";
    private readonly GeoReplicationSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly string _outboxPath;
    private readonly string _senderCheckpointPath;
    private readonly byte[] _sharedSecret;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _synchronousCompletions = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _worker = Task.CompletedTask;
    private long _nextSequence;
    private long _sentEvents;
    private long _failedAttempts;
    private string? _lastError;
    private int _started;
    private int _stopped;

    public GeoReplicationService(
        GeoReplicationSettings settings,
        HttpClient httpClient,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _settings = settings;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _sharedSecret = Encoding.UTF8.GetBytes(settings.SharedSecret);
        _outboxPath = Path.GetFullPath(Path.Combine(settings.StatePath, "outbox"));
        _senderCheckpointPath = Path.GetFullPath(Path.Combine(settings.StatePath, "sender.sequence"));
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.Enabled || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Directory.CreateDirectory(_outboxPath);
        long pendingSequence = Directory.EnumerateFiles(_outboxPath, "*.event")
            .Select(static path => TryParseSequence(Path.GetFileName(path)))
            .DefaultIfEmpty(0)
            .Max();
        long sentSequence = ReadSentSequence();
        _nextSequence = Math.Max(pendingSequence, sentSequence);
        _worker = Task.Run(RunAsync, CancellationToken.None);
        Signal();
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        Signal();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach ((Guid eventId, TaskCompletionSource<bool> completion) in _synchronousCompletions)
        {
            if (_synchronousCompletions.TryRemove(eventId, out _))
            {
                completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    public async ValueTask PublishAsync(
        GeoReplicationEvent replicationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replicationEvent);
        if (!_settings.Enabled)
        {
            return;
        }

        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0)
        {
            throw new InvalidOperationException("Geographic replication is not running.");
        }

        if (Directory.EnumerateFiles(_outboxPath, "*.event").Take(_settings.QueueCapacity).Count() >= _settings.QueueCapacity)
        {
            throw new DomainException(ErrorCodes.QueueSaturated, "The geographic replication outbox is saturated.");
        }

        replicationEvent.EventId = replicationEvent.EventId == Guid.Empty ? Guid.NewGuid() : replicationEvent.EventId;
        replicationEvent.Sequence = Interlocked.Increment(ref _nextSequence);
        replicationEvent.OriginId = _settings.OriginId;
        replicationEvent.CreatedAt = _timeProvider.GetUtcNow();
        ValidateEvent(replicationEvent);
        byte[] payload = MemoryPackSerializer.Serialize(replicationEvent);
        string path = EventPath(replicationEvent.Sequence, replicationEvent.EventId);
        await WriteAtomicAsync(path, payload, cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<bool>? completion = null;
        if (_settings.Mode == GeoReplicationMode.Synchronous)
        {
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_synchronousCompletions.TryAdd(replicationEvent.EventId, completion))
            {
                throw new InvalidOperationException($"Replication event '{replicationEvent.EventId}' is already pending.");
            }
        }

        Signal();
        if (completion is not null)
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public GeoReplicationMetrics GetMetrics()
    {
        string[] pending = Directory.Exists(_outboxPath)
            ? Directory.EnumerateFiles(_outboxPath, "*.event").Order(StringComparer.Ordinal).ToArray()
            : [];
        TimeSpan lag = TimeSpan.Zero;
        if (pending.Length > 0)
        {
            try
            {
                byte[] payload = File.ReadAllBytes(pending[0]);
                GeoReplicationEvent? oldest = MemoryPackSerializer.Deserialize<GeoReplicationEvent>(payload);
                if (oldest is not null)
                {
                    lag = _timeProvider.GetUtcNow() - oldest.CreatedAt;
                }
            }
            catch (IOException)
            {
                // A concurrent sender may have removed the oldest file.
            }
        }

        return new GeoReplicationMetrics
        {
            PendingEvents = pending.LongLength,
            SentEvents = Volatile.Read(ref _sentEvents),
            FailedAttempts = Volatile.Read(ref _failedAttempts),
            ReceivedEvents = 0,
            DuplicateEvents = 0,
            DivergenceEvents = 0,
            ReplicationLag = lag < TimeSpan.Zero ? TimeSpan.Zero : lag,
            LastError = Volatile.Read(ref _lastError),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync()
    {
        TimeSpan retryDelay = _settings.RetryMinimumDelay;
        while (!_shutdown.IsCancellationRequested)
        {
            string? path = Directory.Exists(_outboxPath)
                ? Directory.EnumerateFiles(_outboxPath, "*.event").Order(StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (path is null)
            {
                try
                {
                    await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                byte[] payload = await File.ReadAllBytesAsync(path, _shutdown.Token).ConfigureAwait(false);
                GeoReplicationEvent replicationEvent = MemoryPackSerializer.Deserialize<GeoReplicationEvent>(payload)
                    ?? throw new InvalidDataException($"Geographic replication event '{path}' is empty.");
                ValidateEvent(replicationEvent);
                await SendAsync(payload, _shutdown.Token).ConfigureAwait(false);
                await PersistSentSequenceAsync(replicationEvent.Sequence, _shutdown.Token).ConfigureAwait(false);
                File.Delete(path);
                Interlocked.Increment(ref _sentEvents);
                Volatile.Write(ref _lastError, null);
                retryDelay = _settings.RetryMinimumDelay;
                if (_synchronousCompletions.TryRemove(replicationEvent.EventId, out TaskCompletionSource<bool>? completion))
                {
                    completion.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedAttempts);
                Volatile.Write(ref _lastError, exception.Message);
                try
                {
                    await Task.Delay(retryDelay, _timeProvider, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }

                long doubledTicks = retryDelay.Ticks > long.MaxValue / 2 ? long.MaxValue : retryDelay.Ticks * 2;
                retryDelay = TimeSpan.FromTicks(Math.Min(doubledTicks, _settings.RetryMaximumDelay.Ticks));
            }
        }
    }

    private async ValueTask SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        Uri endpoint = new(_settings.SecondaryEndpoint!, EndpointPath);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-memorypack");
        request.Headers.TryAddWithoutValidation("X-SlimVector-Signature", Signature(payload));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.RequestTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            string detail = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            throw new GeoReplicationDivergenceException(
                string.IsNullOrWhiteSpace(detail) ? "The secondary reported geographic replication divergence." : detail);
        }

        response.EnsureSuccessStatusCode();
    }

    private string Signature(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_sharedSecret, payload));

    private string EventPath(long sequence, Guid eventId) =>
        Path.Combine(_outboxPath, $"{sequence:D20}-{eventId:N}.event");

    private void Signal()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another publisher signaled concurrently.
            }
        }
    }

    private static long TryParseSequence(string fileName) =>
        fileName.Length >= 20 && long.TryParse(fileName.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence)
            ? sequence
            : 0;

    private long ReadSentSequence()
    {
        if (!File.Exists(_senderCheckpointPath))
        {
            return 0;
        }

        string value = File.ReadAllText(_senderCheckpointPath, Encoding.ASCII);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) ? sequence : 0;
    }

    private async ValueTask PersistSentSequenceAsync(long sequence, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_senderCheckpointPath)!);
        string temporary = _senderCheckpointPath + $".{Guid.NewGuid():N}.partial";
        await File.WriteAllTextAsync(
            temporary,
            sequence.ToString(CultureInfo.InvariantCulture),
            Encoding.ASCII,
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _senderCheckpointPath, overwrite: true);
    }

    private static void ValidateEvent(GeoReplicationEvent replicationEvent)
    {
        if (replicationEvent.FormatVersion != 1 || replicationEvent.EventId == Guid.Empty ||
            replicationEvent.Sequence < 1 || string.IsNullOrWhiteSpace(replicationEvent.OriginId) ||
            replicationEvent.Writes.Length == 0 || !Enum.IsDefined(replicationEvent.Kind))
        {
            throw new InvalidDataException("The geographic replication event is malformed.");
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        string temporary = path + $".{Guid.NewGuid():N}.partial";
        await using FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        File.Move(temporary, path, overwrite: false);
    }
}
