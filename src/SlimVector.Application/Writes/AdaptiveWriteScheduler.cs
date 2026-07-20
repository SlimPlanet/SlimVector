using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Writes;

public sealed class AdaptiveWriteScheduler : IWriteScheduler
{
    private const string AnonymousClientId = "anonymous";
    private readonly IConsensusCoordinator _consensus;
    private readonly AdaptiveBatchingOptions _batching;
    private readonly BackpressureOptions _backpressure;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _globalSlots;
    private readonly SemaphoreSlim _replicationSlots;
    private readonly ConcurrentDictionary<Guid, int> _collectionDepths = new();
    private readonly ConcurrentDictionary<string, int> _clientDepths = new(StringComparer.Ordinal);
    private readonly object _shardsLock = new();
    private readonly Dictionary<string, ShardQueue> _shards = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private long _queueDepth;
    private long _totalWrites;
    private long _completedWrites;
    private long _rejectedWrites;
    private long _totalBatches;
    private long _totalBatchItems;
    private int _started;
    private int _stopped;

    public AdaptiveWriteScheduler(
        IConsensusCoordinator consensus,
        IOptions<AdaptiveBatchingOptions> batching,
        IOptions<BackpressureOptions> backpressure,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(consensus);
        ArgumentNullException.ThrowIfNull(batching);
        ArgumentNullException.ThrowIfNull(backpressure);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _consensus = consensus;
        _batching = batching.Value;
        _backpressure = backpressure.Value;
        _timeProvider = timeProvider;
        _globalSlots = new SemaphoreSlim(_backpressure.GlobalQueueCapacity, _backpressure.GlobalQueueCapacity);
        _replicationSlots = new SemaphoreSlim(
            _backpressure.MaximumConcurrentWrites,
            _backpressure.MaximumConcurrentWrites);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopped) != 0)
        {
            throw new InvalidOperationException("The write scheduler has already stopped.");
        }

        Interlocked.Exchange(ref _started, 1);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        ShardQueue[] shards;
        lock (_shardsLock)
        {
            shards = _shards.Values.ToArray();
        }

        foreach (ShardQueue shard in shards)
        {
            shard.Channel.Writer.TryComplete();
        }

        await Task.WhenAll(shards.Select(static shard => shard.Worker)).WaitAsync(cancellationToken).ConfigureAwait(false);
        _shutdown.Cancel();
    }

    public ValueTask EnqueueAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        string? clientId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(collection, operations, clientId, atomic: true, cancellationToken);

    public async ValueTask EnqueueAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        string? clientId,
        bool atomic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return;
        }

        ShardPartition[] partitions = operations
            .Select(operation => (Operation: operation, Route: _consensus.GetShardRoute(collection, operation.Id)))
            .GroupBy(static item => (item.Route.DataGroupId, item.Route.RoutingEpoch))
            .Select(group =>
            {
                (StorageOperation Operation, ShardRoute Route)[] items = group.ToArray();
                int[] shardIds = items.Select(static item => item.Route.ShardId).Distinct().Take(2).ToArray();
                ShardRoute route = new(
                    shardIds.Length == 1 ? shardIds[0] : -1,
                    group.Key.DataGroupId,
                    group.Key.RoutingEpoch);
                return new ShardPartition(route, items.Select(static item => item.Operation).ToArray());
            })
            .ToArray();
        if (atomic && partitions.Select(static partition => partition.Route.DataGroupId)
                .Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            Interlocked.Increment(ref _rejectedWrites);
            throw new DomainException(
                ErrorCodes.CrossShardAtomicUnsupported,
                "Atomic writes may not span multiple physical data groups.");
        }

        await Task.WhenAll(partitions.Select(partition => EnqueuePartitionAsync(
            collection,
            partition.Operations,
            partition.Route,
            clientId,
            cancellationToken).AsTask())).ConfigureAwait(false);
    }

    private async ValueTask EnqueuePartitionAsync(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        ShardRoute route,
        string? clientId,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        long estimatedBytes = EstimateBytes(collection, operations);
        if (estimatedBytes > _batching.MaximumBatchBytes)
        {
            Interlocked.Increment(ref _rejectedWrites);
            throw new DomainException(
                ErrorCodes.WriteTooLarge,
                $"The estimated write payload of {estimatedBytes} bytes exceeds the {_batching.MaximumBatchBytes}-byte limit.");
        }

        string effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? AnonymousClientId : clientId;
        string groupId = route.DataGroupId;
        bool globalReserved = await _globalSlots
            .WaitAsync(_backpressure.EnqueueTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!globalReserved)
        {
            Reject("The global write queue is saturated.");
        }

        bool collectionReserved = false;
        bool clientReserved = false;
        bool shardReserved = false;
        ShardQueue? shard = null;
        try
        {
            collectionReserved = TryReserve(_collectionDepths, collection.Id, _backpressure.PerCollectionQueueCapacity);
            if (!collectionReserved)
            {
                Reject($"The write queue for collection '{collection.Id}' is saturated.");
            }

            clientReserved = TryReserve(_clientDepths, effectiveClientId, _backpressure.PerClientQueueCapacity);
            if (!clientReserved)
            {
                Reject("The write queue for this client is saturated.");
            }

            shard = GetOrCreateShard(groupId);
            shardReserved = TryReserve(ref shard.Depth, _backpressure.PerShardQueueCapacity);
            if (!shardReserved)
            {
                Reject($"The write queue for shard '{groupId}' is saturated.");
            }

            PendingWrite pending = new(collection, operations, route, effectiveClientId, estimatedBytes);
            if (!shard.Channel.Writer.TryWrite(pending))
            {
                throw new InvalidOperationException("The write scheduler is stopping.");
            }

            Interlocked.Increment(ref _queueDepth);
            Interlocked.Increment(ref _totalWrites);
            globalReserved = false;
            collectionReserved = false;
            clientReserved = false;
            shardReserved = false;
            await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (shardReserved && shard is not null)
            {
                Release(ref shard.Depth);
            }

            if (clientReserved)
            {
                Release(_clientDepths, effectiveClientId);
            }

            if (collectionReserved)
            {
                Release(_collectionDepths, collection.Id);
            }

            if (globalReserved)
            {
                _globalSlots.Release();
            }
        }
    }

    public WriteSchedulerSnapshot GetSnapshot()
    {
        ShardQueue[] shards;
        lock (_shardsLock)
        {
            shards = _shards.Values.OrderBy(static shard => shard.GroupId, StringComparer.Ordinal).ToArray();
        }

        return new WriteSchedulerSnapshot
        {
            QueueDepth = Volatile.Read(ref _queueDepth),
            TotalWrites = Volatile.Read(ref _totalWrites),
            CompletedWrites = Volatile.Read(ref _completedWrites),
            RejectedWrites = Volatile.Read(ref _rejectedWrites),
            TotalBatches = Volatile.Read(ref _totalBatches),
            TotalBatchItems = Volatile.Read(ref _totalBatchItems),
            Shards = shards.Select(static shard => new WriteShardSnapshot
            {
                GroupId = shard.GroupId,
                QueueDepth = Volatile.Read(ref shard.Depth),
                TargetBatchSize = Volatile.Read(ref shard.TargetBatchSize),
                CurrentWindow = TimeSpan.FromTicks(Volatile.Read(ref shard.WindowTicks)),
                LastBatchSize = Volatile.Read(ref shard.LastBatchSize),
                LastBatchBytes = Volatile.Read(ref shard.LastBatchBytes),
                LastReplicationMilliseconds = TimeSpan.FromTicks(Volatile.Read(ref shard.LastReplicationTicks)).TotalMilliseconds,
            }).ToArray(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _shutdown.Dispose();
        _globalSlots.Dispose();
        _replicationSlots.Dispose();
    }

    private ShardQueue GetOrCreateShard(string groupId)
    {
        lock (_shardsLock)
        {
            if (_shards.TryGetValue(groupId, out ShardQueue? existing))
            {
                return existing;
            }

            ShardQueue shard = new(
                groupId,
                _batching.Enabled ? _batching.MinimumBatchSize : 1,
                _batching.Enabled ? _batching.MinimumWindow : TimeSpan.Zero);
            _shards.Add(groupId, shard);
            shard.Worker = Task.Run(() => RunShardAsync(shard), CancellationToken.None);
            return shard;
        }
    }

    private async Task RunShardAsync(ShardQueue shard)
    {
        Dictionary<Guid, Queue<PendingWrite>> pendingByCollection = [];
        Queue<Guid> rotation = [];
        int pendingCount = 0;
        try
        {
            while (true)
            {
                if (pendingCount == 0)
                {
                    if (!await shard.Channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }

                DrainAvailable(shard, pendingByCollection, rotation, ref pendingCount);
                int targetBatchSize = Volatile.Read(ref shard.TargetBatchSize);
                TimeSpan window = TimeSpan.FromTicks(Volatile.Read(ref shard.WindowTicks));
                if (pendingCount < targetBatchSize && window > TimeSpan.Zero)
                {
                    Task<bool> available = shard.Channel.Reader.WaitToReadAsync(_shutdown.Token).AsTask();
                    Task delay = Task.Delay(window, _timeProvider, _shutdown.Token);
                    if (await Task.WhenAny(available, delay).ConfigureAwait(false) == available &&
                        await available.ConfigureAwait(false))
                    {
                        DrainAvailable(shard, pendingByCollection, rotation, ref pendingCount);
                    }
                }

                List<PendingWrite> batch = BuildFairBatch(
                    pendingByCollection,
                    rotation,
                    ref pendingCount,
                    targetBatchSize,
                    _batching.MaximumBatchBytes);
                if (batch.Count > 0)
                {
                    await ReplicateBatchAsync(shard, batch).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal fails any writes that were not durably handed to consensus.
        }
        catch (Exception exception)
        {
            shard.Channel.Writer.TryComplete(exception);
            FailPending(pendingByCollection.Values.SelectMany(static queue => queue), exception);
        }
        finally
        {
            while (shard.Channel.Reader.TryRead(out PendingWrite? pending))
            {
                CompletePending(shard, pending, new OperationCanceledException("The write scheduler stopped."));
            }

            FailPending(
                pendingByCollection.Values.SelectMany(static queue => queue),
                new OperationCanceledException("The write scheduler stopped."),
                shard);
        }
    }

    private async Task ReplicateBatchAsync(ShardQueue shard, List<PendingWrite> batch)
    {
        long startedAt = _timeProvider.GetTimestamp();
        long bytes = batch.Sum(static pending => pending.EstimatedBytes);
        Exception? failure = null;
        await _replicationSlots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            CollectionWrite[] writes = batch
                .Select(static pending => new CollectionWrite(pending.Collection, pending.Operations, pending.Route))
                .ToArray();
            await _consensus.AppendBatchAsync(writes, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _replicationSlots.Release();
        }

        TimeSpan latency = _timeProvider.GetElapsedTime(startedAt);
        Volatile.Write(ref shard.LastBatchSize, batch.Count);
        Volatile.Write(ref shard.LastBatchBytes, bytes);
        Volatile.Write(ref shard.LastReplicationTicks, latency.Ticks);
        Interlocked.Increment(ref _totalBatches);
        Interlocked.Add(ref _totalBatchItems, batch.Count);
        foreach (PendingWrite pending in batch)
        {
            CompletePending(shard, pending, failure);
        }

        Adapt(shard, latency);
    }

    private void Adapt(ShardQueue shard, TimeSpan latency)
    {
        if (!_batching.Enabled)
        {
            return;
        }

        int current = Volatile.Read(ref shard.TargetBatchSize);
        long depth = Volatile.Read(ref shard.Depth);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        double pressure = memory.HighMemoryLoadThresholdBytes <= 0
            ? 0
            : (double)memory.MemoryLoadBytes / memory.HighMemoryLoadThresholdBytes;
        bool shouldGrow = depth > current && latency < TimeSpan.FromMilliseconds(250) && pressure < 0.80;
        bool shouldShrink = depth == 0 || latency > TimeSpan.FromMilliseconds(500) || pressure >= 0.85;
        int next = shouldGrow
            ? Math.Min(_batching.MaximumBatchSize, Math.Max(current + 1, current * 2))
            : shouldShrink
                ? Math.Max(_batching.MinimumBatchSize, current / 2)
                : current;
        Volatile.Write(ref shard.TargetBatchSize, next);

        long minimumTicks = _batching.MinimumWindow.Ticks;
        long maximumTicks = _batching.MaximumWindow.Ticks;
        long currentTicks = Volatile.Read(ref shard.WindowTicks);
        long step = Math.Max(1, (maximumTicks - minimumTicks) / 8);
        long nextTicks = shouldGrow
            ? Math.Min(maximumTicks, currentTicks + step)
            : shouldShrink
                ? Math.Max(minimumTicks, currentTicks - step)
                : currentTicks;
        Volatile.Write(ref shard.WindowTicks, nextTicks);
    }

    private void CompletePending(ShardQueue shard, PendingWrite pending, Exception? failure)
    {
        if (failure is null)
        {
            pending.Completion.TrySetResult(true);
            Interlocked.Increment(ref _completedWrites);
        }
        else
        {
            pending.Completion.TrySetException(failure);
        }

        Release(ref shard.Depth);
        Release(_clientDepths, pending.ClientId);
        Release(_collectionDepths, pending.Collection.Id);
        Interlocked.Decrement(ref _queueDepth);
        _globalSlots.Release();
    }

    private static void DrainAvailable(
        ShardQueue shard,
        Dictionary<Guid, Queue<PendingWrite>> pendingByCollection,
        Queue<Guid> rotation,
        ref int pendingCount)
    {
        while (shard.Channel.Reader.TryRead(out PendingWrite? pending))
        {
            if (!pendingByCollection.TryGetValue(pending.Collection.Id, out Queue<PendingWrite>? collectionQueue))
            {
                collectionQueue = new Queue<PendingWrite>();
                pendingByCollection.Add(pending.Collection.Id, collectionQueue);
                rotation.Enqueue(pending.Collection.Id);
            }

            collectionQueue.Enqueue(pending);
            pendingCount++;
        }
    }

    private static List<PendingWrite> BuildFairBatch(
        Dictionary<Guid, Queue<PendingWrite>> pendingByCollection,
        Queue<Guid> rotation,
        ref int pendingCount,
        int maximumItems,
        long maximumBytes)
    {
        List<PendingWrite> result = new(Math.Min(maximumItems, pendingCount));
        long bytes = 0;
        int failedFits = 0;
        while (result.Count < maximumItems && rotation.Count > 0 && failedFits < rotation.Count)
        {
            Guid collectionId = rotation.Dequeue();
            Queue<PendingWrite> collectionQueue = pendingByCollection[collectionId];
            PendingWrite next = collectionQueue.Peek();
            if (result.Count > 0 && bytes + next.EstimatedBytes > maximumBytes)
            {
                rotation.Enqueue(collectionId);
                failedFits++;
                continue;
            }

            failedFits = 0;
            result.Add(collectionQueue.Dequeue());
            bytes += next.EstimatedBytes;
            pendingCount--;
            if (collectionQueue.Count == 0)
            {
                pendingByCollection.Remove(collectionId);
            }
            else
            {
                rotation.Enqueue(collectionId);
            }
        }

        return result;
    }

    private void FailPending(IEnumerable<PendingWrite> pendingWrites, Exception exception, ShardQueue? shard = null)
    {
        foreach (PendingWrite pending in pendingWrites)
        {
            if (shard is null)
            {
                pending.Completion.TrySetException(exception);
            }
            else
            {
                CompletePending(shard, pending, exception);
            }
        }
    }

    private static bool TryReserve<TKey>(ConcurrentDictionary<TKey, int> depths, TKey key, int capacity)
        where TKey : notnull
    {
        while (true)
        {
            int current = depths.GetOrAdd(key, 0);
            if (current >= capacity)
            {
                return false;
            }

            if (depths.TryUpdate(key, current + 1, current))
            {
                return true;
            }
        }
    }

    private static bool TryReserve(ref long depth, int capacity)
    {
        long next = Interlocked.Increment(ref depth);
        if (next <= capacity)
        {
            return true;
        }

        Interlocked.Decrement(ref depth);
        return false;
    }

    private static void Release<TKey>(ConcurrentDictionary<TKey, int> depths, TKey key)
        where TKey : notnull
    {
        while (depths.TryGetValue(key, out int current))
        {
            if (current == 1)
            {
                if (depths.TryRemove(new KeyValuePair<TKey, int>(key, current)))
                {
                    return;
                }
            }
            else if (depths.TryUpdate(key, current - 1, current))
            {
                return;
            }
        }
    }

    private static void Release(ref long depth) => Interlocked.Decrement(ref depth);

    private void Reject(string message)
    {
        Interlocked.Increment(ref _rejectedWrites);
        throw new DomainException(ErrorCodes.QueueSaturated, message);
    }

    private void EnsureRunning()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0)
        {
            throw new InvalidOperationException("The write scheduler is not running.");
        }
    }

    private static long EstimateBytes(CollectionDefinition collection, IReadOnlyList<StorageOperation> operations)
    {
        long bytes = 128 + collection.Name.Length * sizeof(char);
        foreach (StorageOperation operation in operations)
        {
            bytes += 48 + operation.Id.Length * sizeof(char);
            if (operation.Document is { } document)
            {
                bytes += document.Text.Length * sizeof(char) + document.Vector.LongLength * sizeof(float);
                bytes += document.Metadata.Count * 64L;
            }
        }

        return bytes;
    }

    private sealed class ShardQueue
    {
        public ShardQueue(string groupId, int targetBatchSize, TimeSpan window)
        {
            GroupId = groupId;
            TargetBatchSize = targetBatchSize;
            WindowTicks = window.Ticks;
            Channel = System.Threading.Channels.Channel.CreateUnbounded<PendingWrite>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        public string GroupId { get; }

        public Channel<PendingWrite> Channel { get; }

        public Task Worker { get; set; } = Task.CompletedTask;

        public long Depth;

        public int TargetBatchSize;

        public long WindowTicks;

        public int LastBatchSize;

        public long LastBatchBytes;

        public long LastReplicationTicks;
    }

    private sealed class PendingWrite(
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations,
        ShardRoute route,
        string clientId,
        long estimatedBytes)
    {
        public CollectionDefinition Collection { get; } = collection;

        public IReadOnlyList<StorageOperation> Operations { get; } = operations;

        public ShardRoute Route { get; } = route;

        public string ClientId { get; } = clientId;

        public long EstimatedBytes { get; } = estimatedBytes;

        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ShardPartition(ShardRoute Route, IReadOnlyList<StorageOperation> Operations);
}
