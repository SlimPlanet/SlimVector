using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Replication.Tests;

public sealed class GeoReplicationTests
{
    private const string SharedSecret = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 45_000)]
    public async Task AsyncOutboxSurvivesOutageCatchesUpAndContinuesSequenceAfterRestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using StorageNode primaryNode = await StorageNode.CreateAsync(Path.Combine(directory.Path, "primary"), cancellationToken);
        await using StorageNode secondaryNode = await StorageNode.CreateAsync(Path.Combine(directory.Path, "secondary"), cancellationToken);
        GeoReplicationSettings secondarySettings = Settings(
            Path.Combine(directory.Path, "secondary-geo"),
            enabled: false,
            acceptIncoming: true);
        using GeoReplicationReceiver receiver = new(secondarySettings, secondaryNode.Consensus);
        ReceiverHandler handler = new(receiver) { Offline = true };
        GeoReplicationSettings primarySettings = Settings(
            Path.Combine(directory.Path, "primary-geo"),
            enabled: true,
            acceptIncoming: false);
        CollectionDefinition collection = CollectionDefinition.Create("geo-articles", 2, DistanceMetric.Cosine);
        DocumentRecord document = new()
        {
            Id = "one",
            Text = "cross region vector",
            Vector = [1, 0],
            Metadata = [],
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await using (GeoReplicationService service = Service(primarySettings, handler))
        {
            GeoReplicatingConsensusCoordinator primary = new(primaryNode.Consensus, service);
            await primary.StartAsync(cancellationToken);
            await primary.UpsertCollectionAsync(collection, cancellationToken);
            await primary.AppendAsync(collection, [StorageOperation.Upsert(document)], cancellationToken);
            await WaitUntilAsync(() => service.GetMetrics().FailedAttempts > 0, cancellationToken);
            Assert.Equal(2, service.GetMetrics().PendingEvents);
            Assert.Null(await secondaryNode.Storage.GetCollectionAsync(collection.Name, cancellationToken));

            handler.Offline = false;
            await WaitUntilAsync(
                () => SecondaryHasDocumentAsync(secondaryNode.Storage, collection, cancellationToken),
                cancellationToken);
            await WaitUntilAsync(
                () => service.GetMetrics() is { PendingEvents: 0, SentEvents: 2 } &&
                    receiver.GetMetrics().ReceivedEvents == 2,
                cancellationToken);
            Assert.Equal(0, service.GetMetrics().PendingEvents);
            Assert.Equal(2, service.GetMetrics().SentEvents);
            Assert.Equal(2, receiver.GetMetrics().ReceivedEvents);
            await primary.StopAsync(cancellationToken);
        }

        await using (GeoReplicationService restarted = Service(primarySettings, handler))
        {
            await restarted.StartAsync(cancellationToken);
            CollectionDefinition updated = collection with { UpdatedAt = collection.UpdatedAt.AddMinutes(1) };
            await restarted.PublishAsync(GeoReplicationEventFactory.CollectionUpsert(updated), cancellationToken);
            await WaitUntilAsync(() => receiver.GetMetrics().ReceivedEvents == 3, cancellationToken);
            Assert.Equal(1, restarted.GetMetrics().SentEvents);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task SynchronousModeWaitsForSecondaryAcknowledgement()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using StorageNode secondaryNode = await StorageNode.CreateAsync(Path.Combine(directory.Path, "secondary"), cancellationToken);
        using GeoReplicationReceiver receiver = new(
            Settings(Path.Combine(directory.Path, "secondary-geo"), enabled: false, acceptIncoming: true),
            secondaryNode.Consensus);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReceiverHandler handler = new(receiver, release.Task);
        GeoReplicationSettings settings = Settings(
            Path.Combine(directory.Path, "primary-geo"),
            enabled: true,
            acceptIncoming: false) with
        { Mode = GeoReplicationMode.Synchronous };
        await using GeoReplicationService service = Service(settings, handler);
        await service.StartAsync(cancellationToken);

        Task publish = service.PublishAsync(
            GeoReplicationEventFactory.CollectionUpsert(
                CollectionDefinition.Create("synchronous", 2, DistanceMetric.Cosine)),
            cancellationToken).AsTask();
        await handler.Entered.Task.WaitAsync(cancellationToken);
        Assert.False(publish.IsCompleted);
        release.SetResult(true);
        await publish;
        Assert.Equal(1, service.GetMetrics().SentEvents);
    }

    [Fact]
    public async Task ReceiverRejectsInvalidSignatureGapAndDivergentReplay()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using StorageNode secondaryNode = await StorageNode.CreateAsync(Path.Combine(directory.Path, "secondary"), cancellationToken);
        using GeoReplicationReceiver receiver = new(
            Settings(Path.Combine(directory.Path, "secondary-geo"), enabled: false, acceptIncoming: true),
            secondaryNode.Consensus);
        GeoReplicationEvent first = Event(1, Guid.Parse("60000000-0000-0000-0000-000000000001"), "first");
        byte[] firstPayload = MemoryPackSerializer.Serialize(first);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => receiver
            .ReceiveAsync(firstPayload, new string('0', 64), cancellationToken).AsTask());
        Assert.Equal(
            GeoReplicationReceiveResult.Applied,
            await receiver.ReceiveAsync(firstPayload, Signature(firstPayload), cancellationToken));
        Assert.Equal(
            GeoReplicationReceiveResult.Duplicate,
            await receiver.ReceiveAsync(firstPayload, Signature(firstPayload), cancellationToken));

        GeoReplicationEvent gap = Event(3, Guid.NewGuid(), "gap");
        byte[] gapPayload = MemoryPackSerializer.Serialize(gap);
        await Assert.ThrowsAsync<GeoReplicationDivergenceException>(() => receiver
            .ReceiveAsync(gapPayload, Signature(gapPayload), cancellationToken).AsTask());
        GeoReplicationEvent divergent = Event(1, Guid.NewGuid(), "different");
        byte[] divergentPayload = MemoryPackSerializer.Serialize(divergent);
        await Assert.ThrowsAsync<GeoReplicationDivergenceException>(() => receiver
            .ReceiveAsync(divergentPayload, Signature(divergentPayload), cancellationToken).AsTask());
        Assert.Equal(2, receiver.GetMetrics().DivergenceEvents);
        Assert.Equal(1, receiver.GetMetrics().DuplicateEvents);
    }

    private static GeoReplicationService Service(GeoReplicationSettings settings, HttpMessageHandler handler) => new(
        settings,
        new HttpClient(handler, disposeHandler: false),
        TimeProvider.System);

    private static GeoReplicationSettings Settings(string statePath, bool enabled, bool acceptIncoming) => new()
    {
        Enabled = enabled,
        AcceptIncoming = acceptIncoming,
        OriginId = "primary-eu",
        SecondaryEndpoint = new Uri("http://secondary.test"),
        SharedSecret = SharedSecret,
        StatePath = statePath,
        QueueCapacity = 100,
        RetryMinimumDelay = TimeSpan.FromMilliseconds(10),
        RetryMaximumDelay = TimeSpan.FromMilliseconds(50),
        RequestTimeout = TimeSpan.FromSeconds(2),
    };

    private static GeoReplicationEvent Event(long sequence, Guid eventId, string name)
    {
        GeoReplicationEvent replicationEvent = GeoReplicationEventFactory.CollectionUpsert(
            CollectionDefinition.Create(name, 2, DistanceMetric.Cosine));
        replicationEvent.EventId = eventId;
        replicationEvent.Sequence = sequence;
        replicationEvent.OriginId = "primary-eu";
        replicationEvent.CreatedAt = DateTimeOffset.UtcNow;
        return replicationEvent;
    }

    private static string Signature(ReadOnlySpan<byte> payload) => Convert.ToHexStringLower(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(SharedSecret), payload));

    private static async Task<bool> SecondaryHasDocumentAsync(
        FileSystemStorageEngine storage,
        CollectionDefinition collection,
        CancellationToken cancellationToken)
    {
        if (await storage.GetCollectionAsync(collection.Name, cancellationToken) is null)
        {
            return false;
        }

        IReadOnlyDictionary<string, DocumentRecord> documents = await storage
            .LoadDocumentsAsync(collection.Id, cancellationToken);
        return documents.ContainsKey("one");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= Timeout)
            {
                throw new TimeoutException($"The replication condition was not met within {Timeout}.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!await predicate().ConfigureAwait(false))
        {
            if (stopwatch.Elapsed >= Timeout)
            {
                throw new TimeoutException($"The replication condition was not met within {Timeout}.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class ReceiverHandler(
        IGeoReplicationReceiver receiver,
        Task? gate = null) : HttpMessageHandler
    {
        public bool Offline { get; set; }

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            if (Offline)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            byte[] payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            string signature = request.Headers.GetValues("X-SlimVector-Signature").Single();
            try
            {
                GeoReplicationReceiveResult result = await receiver
                    .ReceiveAsync(payload, signature, cancellationToken)
                    .ConfigureAwait(false);
                return new HttpResponseMessage(result == GeoReplicationReceiveResult.Applied
                    ? HttpStatusCode.NoContent
                    : HttpStatusCode.OK);
            }
            catch (GeoReplicationDivergenceException exception)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(exception.Message),
                };
            }
        }
    }

    private sealed class StorageNode : IAsyncDisposable
    {
        private readonly StorageRaftCommandApplier _applier;

        private StorageNode(FileSystemStorageEngine storage, StorageRaftCommandApplier applier, IConsensusCoordinator consensus)
        {
            Storage = storage;
            _applier = applier;
            Consensus = consensus;
        }

        public FileSystemStorageEngine Storage { get; }

        public IConsensusCoordinator Consensus { get; }

        public static async Task<StorageNode> CreateAsync(string path, CancellationToken cancellationToken)
        {
            FileSystemStorageEngine storage = new(new StorageSettings { Path = path, FlushToDisk = false });
            await storage.InitializeAsync(cancellationToken);
            StorageRaftCommandApplier applier = new(storage);
            DirectConsensusCoordinator consensus = new(applier);
            await consensus.StartAsync(cancellationToken);
            return new StorageNode(storage, applier, consensus);
        }

        public async ValueTask DisposeAsync()
        {
            await Consensus.DisposeAsync();
            _applier.Dispose();
            Storage.Dispose();
        }
    }
}
