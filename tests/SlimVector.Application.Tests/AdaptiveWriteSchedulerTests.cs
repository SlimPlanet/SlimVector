using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class AdaptiveWriteSchedulerTests
{
    [Fact]
    public async Task ConcurrentLoadGrowsBatchesAndRotatesCollectionsFairly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RecordingConsensus consensus = new();
        await using AdaptiveWriteScheduler scheduler = Scheduler(
            consensus,
            new AdaptiveBatchingOptions
            {
                MinimumBatchSize = 1,
                MaximumBatchSize = 16,
                MinimumWindow = TimeSpan.Zero,
                MaximumWindow = TimeSpan.FromMilliseconds(10),
                MaximumBatchBytes = 1024 * 1024,
            },
            new BackpressureOptions());
        await scheduler.StartAsync(cancellationToken);
        CollectionDefinition[] collections = Enumerable.Range(0, 8)
            .Select(index => Collection($"collection-{index}"))
            .ToArray();

        Task[] writes = Enumerable.Range(0, 96).Select(index => scheduler.EnqueueAsync(
            collections[index % collections.Length],
            [StorageOperation.Upsert(Document($"document-{index}"))],
            $"client-{index % 4}",
            cancellationToken).AsTask()).ToArray();
        await Task.WhenAll(writes);

        WriteSchedulerSnapshot snapshot = scheduler.GetSnapshot();
        Assert.Equal(96, snapshot.TotalWrites);
        Assert.Equal(96, snapshot.CompletedWrites);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.True(snapshot.TotalBatches < snapshot.TotalWrites);
        Assert.Contains(consensus.Batches, static batch => batch.Length > 1);
        Assert.True(Assert.Single(snapshot.Shards).TargetBatchSize > 1);
        CollectionWrite[] fairBatch = consensus.Batches.First(static batch => batch.Length >= 4);
        Assert.True(fairBatch.Take(4).Select(static write => write.Collection.Id).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task BoundedQueuesRejectClientAndGlobalSaturation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource<bool> releaseReplication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingConsensus consensus = new(releaseReplication.Task);
        await using AdaptiveWriteScheduler scheduler = Scheduler(
            consensus,
            new AdaptiveBatchingOptions
            {
                Enabled = false,
                MaximumBatchBytes = 1024 * 1024,
            },
            new BackpressureOptions
            {
                GlobalQueueCapacity = 2,
                PerCollectionQueueCapacity = 2,
                PerShardQueueCapacity = 2,
                PerClientQueueCapacity = 1,
                MaximumConcurrentWrites = 1,
                EnqueueTimeout = TimeSpan.Zero,
            });
        await scheduler.StartAsync(cancellationToken);
        CollectionDefinition collection = Collection("bounded");
        Task first = scheduler.EnqueueAsync(
            collection,
            [StorageOperation.Upsert(Document("one"))],
            "client-a",
            cancellationToken).AsTask();
        await consensus.Entered.Task.WaitAsync(cancellationToken);

        DomainException clientFailure = await Assert.ThrowsAsync<DomainException>(() => scheduler.EnqueueAsync(
            collection,
            [StorageOperation.Upsert(Document("two"))],
            "client-a",
            cancellationToken).AsTask());
        Assert.Equal(ErrorCodes.QueueSaturated, clientFailure.Code);

        Task second = scheduler.EnqueueAsync(
            collection,
            [StorageOperation.Upsert(Document("three"))],
            "client-b",
            cancellationToken).AsTask();
        DomainException globalFailure = await Assert.ThrowsAsync<DomainException>(() => scheduler.EnqueueAsync(
            collection,
            [StorageOperation.Upsert(Document("four"))],
            "client-c",
            cancellationToken).AsTask());
        Assert.Equal(ErrorCodes.QueueSaturated, globalFailure.Code);

        releaseReplication.SetResult(true);
        await Task.WhenAll(first, second);
        WriteSchedulerSnapshot snapshot = scheduler.GetSnapshot();
        Assert.Equal(2, snapshot.CompletedWrites);
        Assert.Equal(2, snapshot.RejectedWrites);
        Assert.Equal(0, snapshot.QueueDepth);
    }

    [Fact]
    public async Task OversizedWriteIsRejectedBeforeAdmission()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RecordingConsensus consensus = new();
        await using AdaptiveWriteScheduler scheduler = Scheduler(
            consensus,
            new AdaptiveBatchingOptions { MaximumBatchBytes = 128 },
            new BackpressureOptions());
        await scheduler.StartAsync(cancellationToken);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => scheduler.EnqueueAsync(
            Collection("large"),
            [StorageOperation.Upsert(Document("large", new string('x', 1_024)))],
            "client",
            cancellationToken).AsTask());

        Assert.Equal(ErrorCodes.WriteTooLarge, exception.Code);
        Assert.Empty(consensus.Batches);
        Assert.Equal(1, scheduler.GetSnapshot().RejectedWrites);
    }

    private static AdaptiveWriteScheduler Scheduler(
        IConsensusCoordinator consensus,
        AdaptiveBatchingOptions batching,
        BackpressureOptions backpressure) => new(
            consensus,
            Options.Create(batching),
            Options.Create(backpressure),
            TimeProvider.System);

    private static CollectionDefinition Collection(string name) => CollectionDefinition.Create(name, 2, DistanceMetric.Cosine);

    private static DocumentRecord Document(string id, string? text = null) => new()
    {
        Id = id,
        Text = text ?? id,
        Vector = [1, 0],
        Metadata = [],
        Version = 1,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingConsensus(Task? replicationGate = null) : IConsensusCoordinator
    {
        private readonly ConcurrentQueue<CollectionWrite[]> _batches = new();

        public event Action<Guid?>? StateChanged
        {
            add { }
            remove { }
        }

        public ExecutionMode Mode => ExecutionMode.SingleNode;

        public bool IsReady => true;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CollectionWrite[]> Batches => _batches.ToArray();

        public IReadOnlyList<RaftGroupStatus> GetStatuses() => [];

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public string GetDataGroupId(Guid collectionId) => "data-0";

        public ValueTask UpsertCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeleteCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AppendAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            CancellationToken cancellationToken = default) =>
            AppendBatchAsync([new CollectionWrite(collection, operations)], cancellationToken);

        public async ValueTask AppendBatchAsync(
            IReadOnlyList<CollectionWrite> writes,
            CancellationToken cancellationToken = default)
        {
            _batches.Enqueue(writes.ToArray());
            Entered.TrySetResult(true);
            if (replicationGate is not null)
            {
                await replicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask ApplyReadBarrierAsync(
            Guid? collectionId,
            ReadConsistency consistency,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
