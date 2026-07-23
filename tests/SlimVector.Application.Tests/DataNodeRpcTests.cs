using System.Net;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Routing;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class DataNodeRpcTests
{
    [Fact]
    public async Task EmptyTopologyBootstrapsThroughTheLocalCatalogRaftGroup()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        using FileSystemClusterTopologyStore topology = new(new StorageSettings
        {
            Path = directory.Path,
            FlushToDisk = false,
        });
        await topology.InitializeAsync(cancellationToken);
        BootstrapConsensus local = new();
        RejectingRpcClient rpc = new();
        SharedNothingConsensusCoordinator coordinator = new(
            local,
            new LocalGroups(),
            topology,
            rpc,
            local,
            new NoOpCatalogCache(),
            Options.Create(new RaftOptions
            {
                Mode = ExecutionMode.Cluster,
                NodeId = "node-1",
            }));

        await coordinator.ReplaceTopologyAsync(new ClusterTopology(), cancellationToken);

        Assert.Equal(1, local.ReplicatedCommands);
        Assert.Equal(0, rpc.Requests);
    }

    [Fact]
    public async Task StaleQueryPrefersTheHealthyReplicaWithTheLowestObservedLag()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        using FileSystemClusterTopologyStore topology = new(new StorageSettings
        {
            Path = directory.Path,
            FlushToDisk = false,
        });
        await topology.InitializeAsync(cancellationToken);
        ClusterNodeDescriptor[] nodes = Enumerable.Range(0, 4).Select(index => new ClusterNodeDescriptor
        {
            NodeId = $"node-{index}",
            ApiEndpoint = $"http://node-{index}:8080",
            InternalEndpoint = $"http://node-{index}:8080",
            RaftHost = $"10.0.0.{index + 1}",
            Zone = $"zone-{index}",
            CapacityBytes = 1_000_000,
            RaftPortStart = 4_000,
            RaftPortCount = 16,
            State = ClusterNodeState.Active,
            LastSeenAt = DateTimeOffset.UtcNow,
        }).ToArray();
        await topology.ReplaceAsync(new ClusterTopology
        {
            Epoch = 3,
            Nodes = nodes,
            CatalogNodeIds = ["node-0", "node-1", "node-2"],
            DataGroups =
            [
                new DataGroupDescriptor
                {
                    GroupId = "data-0",
                    ReplicationFactor = 3,
                    State = DataGroupState.Active,
                    Replicas =
                    [
                        new DataGroupReplica { NodeId = "node-1", RaftEndpoint = "http://10.0.0.2:4000", ObservedReplicationLag = 100 },
                        new DataGroupReplica { NodeId = "node-2", RaftEndpoint = "http://10.0.0.3:4000", ObservedReplicationLag = 2 },
                        new DataGroupReplica { NodeId = "node-3", RaftEndpoint = "http://10.0.0.4:4000", ObservedReplicationLag = 0, Healthy = false },
                    ],
                },
            ],
        }, cancellationToken);
        Uri? requested = null;
        using HttpClient httpClient = new(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MemoryPackSerializer.Serialize(new DataNodeQueryResponse())),
            };
        }));
        DataNodeQueryClient client = new(
            httpClient,
            topology,
            Options.Create(new RaftOptions { NodeId = "node-0" }),
            Options.Create(new ApiOptions()),
            new DataNodeRpcMetrics());

        _ = await client.CountDocumentsAsync("docs", "data-0", ReadConsistency.Stale, cancellationToken);

        Assert.Equal("node-2", requested?.Host);
    }

    [Fact]
    public async Task SignedCatalogSnapshotSeedsAPersistentNonVoterCache()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory sourceDirectory = new();
        using TemporaryDirectory targetDirectory = new();
        StorageSettings sourceSettings = new() { Path = sourceDirectory.Path, FlushToDisk = false };
        StorageSettings targetSettings = new() { Path = targetDirectory.Path, FlushToDisk = false };
        using FileSystemStorageEngine sourceCatalog = new(sourceSettings);
        using FileSystemDataGroupStorage sourceGroups = new(sourceSettings);
        using FileSystemClusterTopologyStore sourceTopology = new(sourceSettings);
        using FileSystemStorageEngine targetCatalog = new(targetSettings);
        using FileSystemDataGroupStorage targetGroups = new(targetSettings);
        using FileSystemClusterTopologyStore targetTopology = new(targetSettings);
        await sourceCatalog.InitializeAsync(cancellationToken);
        await sourceGroups.InitializeAsync(cancellationToken);
        await sourceTopology.InitializeAsync(cancellationToken);
        await targetCatalog.InitializeAsync(cancellationToken);
        await targetGroups.InitializeAsync(cancellationToken);
        await targetTopology.InitializeAsync(cancellationToken);
        using StorageRaftCommandApplier sourceApplier = new(sourceCatalog, ["data-0"], sourceGroups, sourceTopology);
        using StorageRaftCommandApplier targetApplier = new(targetCatalog, ["data-0"], targetGroups, targetTopology);
        await using DirectConsensusCoordinator sourceConsensus = new(sourceApplier);
        await using DirectConsensusCoordinator targetConsensus = new(targetApplier);
        ApiOptions apiOptions = new() { AdminApiKey = "catalog-cache-signing-secret-00000001" };
        LocalCatalogSnapshotExchange source = new(sourceConsensus, sourceApplier, Options.Create(apiOptions));
        LocalCatalogSnapshotExchange target = new(targetConsensus, targetApplier, Options.Create(apiOptions));
        CollectionDefinition collection = CollectionDefinition.Create("cached", 2, DistanceMetric.Cosine) with
        {
            Placement = CollectionPlacement.Create(Guid.Parse("20000000-0000-0000-0000-000000000001"), ["data-0"]),
        };
        collection = collection with { Id = Guid.Parse("20000000-0000-0000-0000-000000000001") };
        await sourceConsensus.UpsertCollectionAsync(collection, cancellationToken);
        ClusterNodeDescriptor[] nodes = Enumerable.Range(1, 4).Select(index => new ClusterNodeDescriptor
        {
            NodeId = $"node-{index}",
            ApiEndpoint = $"https://node-{index}:8080",
            InternalEndpoint = $"https://node-{index}:8080",
            RaftHost = $"10.0.0.{index}",
            Zone = $"zone-{index}",
            CapacityBytes = 1_000_000,
            RaftPortStart = 4_000,
            RaftPortCount = 16,
            State = ClusterNodeState.Active,
            LastSeenAt = DateTimeOffset.UtcNow,
        }).ToArray();
        await sourceConsensus.ReplaceTopologyAsync(new ClusterTopology
        {
            Epoch = 4,
            Nodes = nodes,
            CatalogNodeIds = ["node-1", "node-2", "node-3"],
        }, cancellationToken);

        byte[] snapshot = await source.CaptureAsync(requireLeaderBarrier: true, cancellationToken);
        string signature = Assert.IsType<string>(source.Sign(snapshot));
        await target.InstallAsync(snapshot, signature, cancellationToken);

        Assert.Equal(collection.Id, (await targetCatalog.GetCollectionAsync("cached", cancellationToken))?.Id);
        Assert.Equal(["node-1", "node-2", "node-3"], (await targetTopology.GetAsync(cancellationToken)).CatalogNodeIds);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => target.InstallAsync(
            snapshot,
            new string('0', 64),
            cancellationToken).AsTask());
    }

    [Fact]
    public async Task RoutedMemoryPackWriteIsIdempotentAndRejectsAnOldEpoch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        StorageSettings settings = new() { Path = directory.Path, FlushToDisk = false };
        using FileSystemStorageEngine catalog = new(settings);
        using FileSystemDataGroupStorage groups = new(settings);
        using FileSystemClusterTopologyStore topology = new(settings);
        await catalog.InitializeAsync(cancellationToken);
        await groups.InitializeAsync(cancellationToken);
        await topology.InitializeAsync(cancellationToken);
        using StorageRaftCommandApplier applier = new(catalog, ["data-0"], groups, topology);
        await using DirectConsensusCoordinator consensus = new(applier);
        CollectionDefinition collection = CollectionDefinition.Create("rpc", 2, DistanceMetric.Cosine) with
        {
            Placement = CollectionPlacement.Create(Guid.Parse("10000000-0000-0000-0000-000000000001"), ["data-0"]),
        };
        collection = collection with { Id = Guid.Parse("10000000-0000-0000-0000-000000000001") };
        await consensus.UpsertCollectionAsync(collection, cancellationToken);
        DataNodeRpcMetrics metrics = new();
        DataNodeRpcReceiver receiver = new(
            consensus,
            new LocalGroups(),
            catalog,
            Options.Create(new ApiOptions()),
            metrics);
        DocumentRecord document = new()
        {
            Id = "one",
            Text = "routed",
            Vector = [1, 0],
            Metadata = [],
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        RaftCommandEnvelope command = RaftCommandCodec.ShardBatch(
            Guid.NewGuid(),
            "data-0",
            [new CollectionWrite(collection, [StorageOperation.Upsert(document)], collection.Placement!.Resolve(collection.Id, document.Id))]);
        byte[] payload = RaftCommandCodec.Serialize(command);

        await receiver.ReceiveAsync(payload, signature: null, cancellationToken);
        await receiver.ReceiveAsync(payload, signature: null, cancellationToken);

        Assert.Single(await groups.LoadDocumentsAsync("data-0", collection.Id, cancellationToken));
        Assert.Equal(2, metrics.GetSnapshot().IncomingRequests);
        CollectionDefinition stale = collection with
        {
            Placement = collection.Placement! with { Epoch = 2 },
        };
        RaftCommandEnvelope staleCommand = RaftCommandCodec.ShardBatch(
            Guid.NewGuid(),
            "data-0",
            [new CollectionWrite(stale, [StorageOperation.Upsert(document)], stale.Placement!.Resolve(stale.Id, document.Id))]);
        DomainException failure = await Assert.ThrowsAsync<DomainException>(() => receiver.ReceiveAsync(
            RaftCommandCodec.Serialize(staleCommand),
            signature: null,
            cancellationToken).AsTask());
        Assert.Equal(ErrorCodes.RoutingEpochMismatch, failure.Code);
        Assert.Equal(1, metrics.GetSnapshot().RoutingEpochRejections);
    }

    private sealed class LocalGroups : ILocalRaftGroupManager
    {
        public IReadOnlyList<string> GetHostedDataGroupIds() => ["data-0"];

        public ValueTask AddLocalDataGroupAsync(
            RaftGroupNodeOptions options,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask RemoveLocalDataGroupAsync(
            string groupId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class BootstrapConsensus : IConsensusCoordinator, ILocalRaftCommandReplicator
    {
        public event Action<Guid?>? StateChanged
        {
            add { }
            remove { }
        }

        public ExecutionMode Mode => ExecutionMode.Cluster;

        public bool IsReady => false;

        public int ReplicatedCommands { get; private set; }

        public IReadOnlyList<RaftGroupStatus> GetStatuses() =>
        [
            new RaftGroupStatus
            {
                GroupId = MultiRaftNode.CatalogGroupId,
                LocalEndpoint = "127.0.0.1:3262",
                LeaderEndpoint = "127.0.0.1:3262",
                IsLeader = true,
                Term = 1,
                LastAppliedIndex = 0,
                LastCommittedIndex = 0,
                AppliedCommandCount = 0,
            },
        ];

        public ValueTask ReplicateLocalAsync(
            RaftCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplicatedCommands++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public string GetDataGroupId(Guid collectionId) => "data-0";

        public ValueTask UpsertCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask AppendAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask AppendBatchAsync(
            IReadOnlyList<CollectionWrite> writes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ApplyReadBarrierAsync(
            Guid? collectionId,
            ReadConsistency consistency,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RejectingRpcClient : IDataNodeRpcClient
    {
        public int Requests { get; private set; }

        public ValueTask ReplicateAsync(
            RaftCommandEnvelope command,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            return ValueTask.FromException(new InvalidOperationException("The bootstrap must remain on local Raft."));
        }
    }

    private sealed class NoOpCatalogCache : ICatalogCacheSynchronizer
    {
        public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask SeedNodeAsync(
            string internalEndpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
