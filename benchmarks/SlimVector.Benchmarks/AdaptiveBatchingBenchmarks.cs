using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class AdaptiveBatchingBenchmarks : IAsyncDisposable
{
    private readonly CollectionDefinition _collection = CollectionDefinition.Create("batching", 8, DistanceMetric.Cosine);
    private readonly StorageOperation[] _operation =
    [
        StorageOperation.Upsert(new DocumentRecord
        {
            Id = "one",
            Text = "batched",
            Vector = new float[8],
            Metadata = [],
            Version = 1,
        }),
    ];
    private AdaptiveWriteScheduler _scheduler = null!;

    [Params(1, 32, 128)]
    public int ConcurrentProducers { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        NoOpConsensus consensus = new();
        _scheduler = new AdaptiveWriteScheduler(
            consensus,
            Options.Create(new AdaptiveBatchingOptions
            {
                MinimumBatchSize = 1,
                MaximumBatchSize = 256,
                MaximumWindow = TimeSpan.FromMilliseconds(2),
            }),
            Options.Create(new BackpressureOptions()),
            TimeProvider.System);
        await _scheduler.StartAsync();
    }

    [Benchmark]
    public Task EnqueueConcurrentWrites() => Task.WhenAll(Enumerable.Range(0, ConcurrentProducers)
        .Select(index => _scheduler.EnqueueAsync(_collection, _operation, $"client-{index % 8}").AsTask()));

    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private sealed class NoOpConsensus : IConsensusCoordinator
    {
        public event Action<Guid?>? StateChanged
        {
            add { }
            remove { }
        }

        public ExecutionMode Mode => ExecutionMode.SingleNode;

        public bool IsReady => true;

        public IReadOnlyList<RaftGroupStatus> GetStatuses() => [];

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public string GetDataGroupId(Guid collectionId) => "data-0";

        public ValueTask UpsertCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteCollectionAsync(CollectionDefinition collection, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask AppendAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AppendBatchAsync(
            IReadOnlyList<CollectionWrite> writes,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ApplyReadBarrierAsync(
            Guid? collectionId,
            ReadConsistency consistency,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
