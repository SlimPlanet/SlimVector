using SlimVector.Domain;

namespace SlimVector.Storage.Tests;

public sealed class FileSystemStorageEngineTests
{
    [Fact]
    public async Task DistributedStorageKeepsDataGroupsPhysicallyIsolatedAndPersistsTopology()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        StorageSettings settings = new() { Path = directory.Path, FlushToDisk = true };
        CollectionDefinition definition = CollectionDefinition.Create("isolated", 2, DistanceMetric.Cosine);
        using FileSystemDataGroupStorage groups = new(settings);
        using FileSystemClusterTopologyStore topologyStore = new(settings);
        await groups.InitializeAsync(cancellationToken);
        await topologyStore.InitializeAsync(cancellationToken);
        await groups.EnsureDataGroupAsync("data-0", cancellationToken);
        await groups.EnsureDataGroupAsync("data-1", cancellationToken);
        await groups.EnsureCollectionAsync("data-0", definition, cancellationToken);
        await groups.EnsureCollectionAsync("data-1", definition, cancellationToken);
        await groups.AppendAsync(
            "data-0",
            definition.Id,
            [StorageOperation.Upsert(Document("left", [1, 0]))],
            cancellationToken);
        await groups.AppendAsync(
            "data-1",
            definition.Id,
            [StorageOperation.Upsert(Document("right", [0, 1]))],
            cancellationToken);

        Assert.Equal(["left"], (await groups.LoadDocumentsAsync("data-0", definition.Id, cancellationToken)).Keys);
        Assert.Equal(["right"], (await groups.LoadDocumentsAsync("data-1", definition.Id, cancellationToken)).Keys);
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "data-groups", "data-0", "collections")));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "data-groups", "data-1", "collections")));

        ClusterTopology topology = new()
        {
            Epoch = 2,
            Nodes = Enumerable.Range(0, 3).Select(index => Node($"node-{index}", 4_000 + index)).ToArray(),
            DataGroups =
            [
                new DataGroupDescriptor
                {
                    GroupId = "data-0",
                    ReplicationFactor = 3,
                    State = DataGroupState.Active,
                    Replicas = Enumerable.Range(0, 3).Select(index => new DataGroupReplica
                    {
                        NodeId = $"node-{index}",
                        RaftEndpoint = $"http://127.0.0.1:{4_000 + index}",
                    }).ToArray(),
                },
            ],
        };
        await topologyStore.ReplaceAsync(topology, cancellationToken);
        Assert.Equal(2, (await topologyStore.GetAsync(cancellationToken)).Epoch);
        Assert.True(File.Exists(Path.Combine(directory.Path, "storage-format-v2.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "cluster-topology-v2.json")));
    }

    [Fact]
    public async Task LogicalIoMetricsAreMonotonicAndRecordDurableFlushes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        StorageMetrics metrics = new();
        CollectionDefinition definition = CollectionDefinition.Create("metrics", 2, DistanceMetric.Cosine);
        using FileSystemStorageEngine storage = new(
            new StorageSettings { Path = directory.Path, FlushToDisk = true },
            metrics: metrics);

        await storage.InitializeAsync(cancellationToken);
        StorageMetricsSnapshot initialized = metrics.GetSnapshot();
        await storage.CreateCollectionAsync(definition, cancellationToken);
        await storage.AppendAsync(
            definition.Id,
            [StorageOperation.Upsert(Document("one", [1, 0]))],
            cancellationToken);
        StorageMetricsSnapshot written = metrics.GetSnapshot();
        _ = await storage.LoadDocumentsAsync(definition.Id, cancellationToken);
        StorageMetricsSnapshot read = metrics.GetSnapshot();

        Assert.True(written.BytesWritten > initialized.BytesWritten);
        Assert.True(written.DurableFlushes > initialized.DurableFlushes);
        Assert.True(read.BytesRead > written.BytesRead);
        Assert.True(read.BytesWritten >= written.BytesWritten);
        Assert.True(read.DurableFlushes >= written.DurableFlushes);
    }

    [Fact]
    public async Task ImmutableSegmentsSurviveRestartAndTombstones()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("articles", 2, DistanceMetric.Cosine);
        DocumentRecord first = Document("one", [1, 0]);
        DocumentRecord second = Document("two", [0, 1]);

        using (FileSystemStorageEngine storage = CreateStorage(directory.Path))
        {
            await storage.InitializeAsync(cancellationToken);
            await storage.CreateCollectionAsync(definition, cancellationToken);
            await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(first), StorageOperation.Upsert(second)], cancellationToken);
            await storage.AppendAsync(definition.Id, [StorageOperation.Delete("one")], cancellationToken);
        }

        using FileSystemStorageEngine restarted = CreateStorage(directory.Path);
        await restarted.InitializeAsync(cancellationToken);
        IReadOnlyDictionary<string, DocumentRecord> documents = await restarted.LoadDocumentsAsync(definition.Id, cancellationToken);

        Assert.Single(documents);
        Assert.Equal(second.Id, documents["two"].Id);
        Assert.Equal(second.Vector, documents["two"].Vector);
    }

    [Fact]
    public async Task CompactionKeepsCurrentStateAndMovesOldSegments()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("compact", 2, DistanceMetric.Euclidean);
        using FileSystemStorageEngine storage = CreateStorage(directory.Path);
        await storage.InitializeAsync(cancellationToken);
        await storage.CreateCollectionAsync(definition, cancellationToken);
        await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(Document("one", [1, 0]))], cancellationToken);
        await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(Document("one", [2, 0]))], cancellationToken);

        await storage.CompactAsync(definition.Id, cancellationToken);
        IReadOnlyDictionary<string, DocumentRecord> documents = await storage.LoadDocumentsAsync(definition.Id, cancellationToken);

        Assert.Equal(2, documents["one"].Vector[0]);
        string segmentPath = System.IO.Path.Combine(directory.Path, "collections", definition.Id.ToString("N"), "segments");
        Assert.Single(Directory.EnumerateFiles(segmentPath, "*.segment"));
    }

    [Fact]
    public async Task AppendAutomaticallyCompactsAtConfiguredSegmentThreshold()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("auto-compact", 2, DistanceMetric.Cosine);
        using FileSystemStorageEngine storage = new(new StorageSettings
        {
            Path = directory.Path,
            FlushToDisk = false,
            MaximumSegmentsBeforeCompaction = 3,
        });
        await storage.InitializeAsync(cancellationToken);
        await storage.CreateCollectionAsync(definition, cancellationToken);

        await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(Document("one", [1, 0]))], cancellationToken);
        await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(Document("two", [0, 1]))], cancellationToken);
        await storage.AppendAsync(definition.Id, [StorageOperation.Delete("one")], cancellationToken);

        IReadOnlyDictionary<string, DocumentRecord> documents = await storage.LoadDocumentsAsync(definition.Id, cancellationToken);
        Assert.Single(documents);
        Assert.Contains("two", documents.Keys);
        string segmentPath = System.IO.Path.Combine(directory.Path, "collections", definition.Id.ToString("N"), "segments");
        Assert.Single(Directory.EnumerateFiles(segmentPath, "*.segment"));
    }

    private static FileSystemStorageEngine CreateStorage(string path) => new(new StorageSettings
    {
        Path = path,
        FlushToDisk = false,
    });

    private static DocumentRecord Document(string id, float[] vector) => new()
    {
        Id = id,
        Text = id,
        Vector = vector,
        Metadata = [],
        Version = 1,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ClusterNodeDescriptor Node(string nodeId, int port) => new()
    {
        NodeId = nodeId,
        ApiEndpoint = $"http://127.0.0.1:{port + 100}",
        InternalEndpoint = $"http://127.0.0.1:{port + 100}",
        RaftHost = "127.0.0.1",
        Zone = $"zone-{port % 3}",
        CapacityBytes = 1L << 40,
        RaftPortStart = port,
        RaftPortCount = 10,
        State = ClusterNodeState.Active,
        LastSeenAt = DateTimeOffset.UtcNow,
    };
}
