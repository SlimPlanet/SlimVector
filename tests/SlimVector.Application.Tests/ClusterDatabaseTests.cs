using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class ClusterDatabaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 60_000)]
    public async Task DatabasesUseCatalogAndDataLeadersAndServeLinearizableReads()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        string[] groupIds = [MultiRaftNode.CatalogGroupId, "data-0"];
        IPEndPoint[][] endpoints = groupIds.Select(static _ => AllocateLoopbackEndpoints(3)).ToArray();
        ClusterNode[] nodes = Enumerable.Range(0, 3)
            .Select(nodeIndex => ClusterNode.Create(directory.Path, nodeIndex, groupIds, endpoints))
            .ToArray();

        try
        {
            await Task.WhenAll(nodes.Select(node => node.Database.InitializeAsync(cancellationToken).AsTask()));
            int catalogLeaderIndex = await FindLeaderIndexAsync(
                nodes,
                MultiRaftNode.CatalogGroupId,
                endpoints[0],
                cancellationToken);
            CollectionDefinition collection = await nodes[catalogLeaderIndex].Database.CreateCollectionAsync(
                "cluster-articles",
                2,
                DistanceMetric.Cosine,
                cancellationToken: cancellationToken);
            await WaitUntilAsync(
                () => AllCollectionsExistAsync(nodes, collection.Name, cancellationToken),
                cancellationToken);

            int dataLeaderIndex = await FindLeaderIndexAsync(nodes, "data-0", endpoints[1], cancellationToken);
            BatchMutationResult result = await nodes[dataLeaderIndex].Database.MutateAsync(
                collection.Name,
                [new DocumentMutation
                {
                    Kind = DocumentMutationKind.Add,
                    Id = "raft-document",
                    Document = new DocumentRecord
                    {
                        Id = "raft-document",
                        Text = "replicated hybrid vector search",
                        Vector = [1, 0],
                        Metadata = new Dictionary<string, MetadataValue> { ["region"] = MetadataValue.From("eu-west") },
                    },
                }],
                atomic: true,
                cancellationToken: cancellationToken);
            Assert.Equal(1, result.Succeeded);
            await WaitUntilAsync(
                () => AllDocumentsExistAsync(nodes, collection.Id, cancellationToken),
                cancellationToken);

            int readerIndex = (dataLeaderIndex + 1) % nodes.Length;
            SearchResponse response = await nodes[readerIndex].Database.SearchAsync(collection.Name, new SearchRequest
            {
                Mode = SearchMode.Hybrid,
                Text = "hybrid search",
                Vector = [1, 0],
                Limit = 1,
                Consistency = ReadConsistency.Linearizable,
            }, cancellationToken);

            Assert.Equal("raft-document", Assert.Single(response.Hits).Id);
            Assert.All(nodes, node => Assert.Equal(1, node.ApplierCommandsAppliedTo("data-0")));
        }
        finally
        {
            foreach (ClusterNode node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    private static async Task<int> FindLeaderIndexAsync(
        IReadOnlyList<ClusterNode> nodes,
        string groupId,
        IReadOnlyList<IPEndPoint> endpoints,
        CancellationToken cancellationToken)
    {
        EndPoint leader = await nodes[0].Raft.GetGroup(groupId).WaitForLeaderAsync(Timeout, cancellationToken);
        int index = endpoints.ToList().FindIndex(endpoint => Equals(endpoint, leader));
        Assert.InRange(index, 0, nodes.Count - 1);
        await WaitUntilAsync(
            () => Task.FromResult(nodes[index].Raft.GetGroup(groupId).IsLeader),
            cancellationToken);
        return index;
    }

    private static async Task<bool> AllCollectionsExistAsync(
        IEnumerable<ClusterNode> nodes,
        string name,
        CancellationToken cancellationToken)
    {
        foreach (ClusterNode node in nodes)
        {
            if (await node.Storage.GetCollectionAsync(name, cancellationToken) is null)
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> AllDocumentsExistAsync(
        IEnumerable<ClusterNode> nodes,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        foreach (ClusterNode node in nodes)
        {
            IReadOnlyDictionary<string, DocumentRecord> documents = await node.Storage
                .LoadDocumentsAsync(collectionId, cancellationToken);
            if (!documents.ContainsKey("raft-document"))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!await predicate().ConfigureAwait(false))
        {
            if (stopwatch.Elapsed >= Timeout)
            {
                throw new TimeoutException($"The cluster condition was not met within {Timeout}.");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static IPEndPoint[] AllocateLoopbackEndpoints(int count)
    {
        IPEndPoint[] endpoints = new IPEndPoint[count];
        for (int index = 0; index < count; index++)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            endpoints[index] = (IPEndPoint)listener.LocalEndpoint;
        }

        return endpoints;
    }

    private sealed class ClusterNode : IAsyncDisposable
    {
        private readonly StorageRaftCommandApplier _applier;
        private readonly DistributedConsensusCoordinator _coordinator;
        private readonly IWriteScheduler _writeScheduler;

        private ClusterNode(
            FileSystemStorageEngine storage,
            StorageRaftCommandApplier applier,
            MultiRaftNode raft,
            DistributedConsensusCoordinator coordinator,
            IWriteScheduler writeScheduler,
            SlimVectorDatabase database)
        {
            Storage = storage;
            _applier = applier;
            Raft = raft;
            _coordinator = coordinator;
            _writeScheduler = writeScheduler;
            Database = database;
        }

        public FileSystemStorageEngine Storage { get; }

        public MultiRaftNode Raft { get; }

        public SlimVectorDatabase Database { get; }

        public int ApplierCommandsAppliedTo(string groupId) => checked((int)Raft.GetGroup(groupId).AppliedCommandCount);

        public static ClusterNode Create(
            string rootPath,
            int nodeIndex,
            IReadOnlyList<string> groupIds,
            IPEndPoint[][] endpoints)
        {
            FileSystemStorageEngine storage = new(new StorageSettings
            {
                Path = Path.Combine(rootPath, $"storage-{nodeIndex}"),
                FlushToDisk = false,
            });
            StorageRaftCommandApplier applier = new(storage, ["data-0"]);
            RaftGroupNodeOptions[] groupOptions = groupIds.Select((groupId, groupIndex) => new RaftGroupNodeOptions
            {
                GroupId = groupId,
                LocalEndpoint = endpoints[groupIndex][nodeIndex],
                Members = endpoints[groupIndex],
                StoragePath = Path.Combine(rootPath, $"raft-{nodeIndex}"),
                LowerElectionTimeoutMilliseconds = 150,
                UpperElectionTimeoutMilliseconds = 350,
                RequestTimeout = TimeSpan.FromSeconds(2),
                SnapshotEveryEntries = 100,
                TransmissionBlockSize = 4 * 1024,
            }).ToArray();
            MultiRaftNode raft = new(groupOptions, _ => applier);
            DistributedConsensusCoordinator coordinator = new(raft, applier);
            IWriteScheduler writeScheduler = new AdaptiveWriteScheduler(
                coordinator,
                Options.Create(new AdaptiveBatchingOptions()),
                Options.Create(new BackpressureOptions()),
                TimeProvider.System);
            SlimVectorDatabase database = new(
                storage,
                coordinator,
                writeScheduler,
                TimeProvider.System,
                Options.Create(new CollectionsOptions()),
                Options.Create(new VectorIndexOptions()));
            return new ClusterNode(storage, applier, raft, coordinator, writeScheduler, database);
        }

        public async ValueTask DisposeAsync()
        {
            await _writeScheduler.DisposeAsync();
            await _coordinator.DisposeAsync();
            _applier.Dispose();
            Storage.Dispose();
        }
    }
}
