using SlimVector.Application.Admission;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class AdaptiveAdmissionControllerTests
{
    [Fact]
    public void ContractualClientBucketReturnsRetryAfterAndRecovers()
    {
        ManualTimeProvider time = new();
        RateLimitOptions options = Options(globalRate: 100, clientRate: 1);
        AdaptiveAdmissionController controller = new(
            Microsoft.Extensions.Options.Options.Create(options),
            new StubScheduler(),
            new StubConsensus(isReady: true),
            new OperationalMetrics(),
            time);
        AdmissionRequest request = new("client-a", "collection-a", AdmissionOperation.Read);

        Assert.True(controller.TryAcquire(request).Allowed);
        AdmissionDecision rejected = controller.TryAcquire(request);

        Assert.False(rejected.Allowed);
        Assert.Equal(AdmissionRejectionKind.Contractual, rejected.RejectionKind);
        Assert.Equal("client", rejected.Scope);
        Assert.True(rejected.RetryAfter >= TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(controller.TryAcquire(request).Allowed);
    }

    [Fact]
    public void QuorumPressureProducesSeparateCongestionRejection()
    {
        ManualTimeProvider time = new();
        RateLimitOptions options = Options(globalRate: 10, clientRate: 100);
        AdaptiveAdmissionController controller = new(
            Microsoft.Extensions.Options.Options.Create(options),
            new StubScheduler(),
            new StubConsensus(isReady: false),
            new OperationalMetrics(),
            time);
        AdmissionRequest request = new("client-a", "collection-a", AdmissionOperation.Read);
        for (int index = 0; index < 9; index++)
        {
            Assert.True(controller.TryAcquire(request).Allowed);
        }

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(controller.TryAcquire(request).Allowed);
        AdmissionDecision rejected = controller.TryAcquire(request);

        Assert.False(rejected.Allowed);
        Assert.Equal(AdmissionRejectionKind.Congestion, rejected.RejectionKind);
        Assert.Equal("quorum", rejected.Scope);
        AdmissionMetricsSnapshot metrics = controller.GetSnapshot();
        Assert.Equal(1, metrics.CongestionRejections);
        Assert.Equal(0.1, metrics.AdaptiveRateRatio, precision: 5);
    }

    [Fact]
    public void WriteFloodCannotConsumeReadReserveAndAdminCapacity()
    {
        ManualTimeProvider time = new();
        RateLimitOptions options = Options(globalRate: 10, clientRate: 100);
        options.ReservedReadFraction = 0.2;
        options.ReservedWriteFraction = 0.1;
        AdaptiveAdmissionController controller = new(
            Microsoft.Extensions.Options.Options.Create(options),
            new StubScheduler(),
            new StubConsensus(isReady: true),
            new OperationalMetrics(),
            time);

        AdmissionRequest write = new("writer", "collection", AdmissionOperation.Write);
        for (int index = 0; index < 8; index++)
        {
            Assert.True(controller.TryAcquire(write).Allowed);
        }

        Assert.False(controller.TryAcquire(write).Allowed);
        AdmissionRequest read = new("reader", "collection", AdmissionOperation.Read);
        Assert.True(controller.TryAcquire(read).Allowed);
        Assert.True(controller.TryAcquire(read).Allowed);
        Assert.False(controller.TryAcquire(read).Allowed);
        Assert.True(controller.TryAcquire(new AdmissionRequest("operator", null, AdmissionOperation.Admin)).Allowed);
    }

    private static RateLimitOptions Options(double globalRate, double clientRate) => new()
    {
        Global = Bucket(globalRate),
        Client = Bucket(clientRate),
        Collection = Bucket(100),
        Read = Bucket(100),
        Write = Bucket(100),
        Admin = Bucket(100),
        MinimumAdaptiveRateRatio = 0.1,
    };

    private static TokenBucketOptions Bucket(double rate) => new()
    {
        TokensPerSecond = rate,
        BurstCapacity = rate,
    };

    private sealed class StubScheduler : IWriteScheduler
    {
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask EnqueueAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            string? clientId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public WriteSchedulerSnapshot GetSnapshot() => new()
        {
            QueueDepth = 0,
            TotalWrites = 0,
            CompletedWrites = 0,
            RejectedWrites = 0,
            TotalBatches = 0,
            TotalBatchItems = 0,
            Shards = [],
        };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConsensus(bool isReady) : IConsensusCoordinator
    {
        public event Action<Guid?>? StateChanged
        {
            add { }
            remove { }
        }

        public ExecutionMode Mode => ExecutionMode.SingleNode;

        public bool IsReady => isReady;

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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
