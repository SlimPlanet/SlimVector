using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task DocumentCountIsMigratedIntoManifestAndMaintainedFromCachedState()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("counted", 2, DistanceMetric.Cosine);
        string manifestPath = Path.Combine(
            directory.Path,
            "collections",
            definition.Id.ToString("N"),
            "manifest.json");
        using (FileSystemStorageEngine storage = CreateStorage(directory.Path))
        {
            await storage.InitializeAsync(cancellationToken);
            await storage.CreateCollectionAsync(definition, cancellationToken);
            await storage.AppendAsync(
                definition.Id,
                [
                    StorageOperation.Upsert(Document("one", [1, 0])),
                    StorageOperation.Upsert(Document("two", [0, 1])),
                ],
                cancellationToken);
        }

        JsonObject legacyManifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        legacyManifest.Remove("documentCount");
        await File.WriteAllTextAsync(manifestPath, legacyManifest.ToJsonString(), cancellationToken);

        using (FileSystemStorageEngine migrated = CreateStorage(directory.Path))
        {
            await migrated.InitializeAsync(cancellationToken);
            Assert.Equal(2, await migrated.CountDocumentsAsync(definition.Id, cancellationToken));
            await migrated.AppendAsync(
                definition.Id,
                [StorageOperation.Upsert(Document("two", [0.5F, 0.5F]))],
                cancellationToken);
            Assert.Equal(2, await migrated.CountDocumentsAsync(definition.Id, cancellationToken));
            await migrated.AppendAsync(
                definition.Id,
                [StorageOperation.Delete("one")],
                cancellationToken);
            Assert.Equal(1, await migrated.CountDocumentsAsync(definition.Id, cancellationToken));
        }

        JsonObject persisted = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        Assert.Equal(1, persisted["documentCount"]!.GetValue<long>());
        using FileSystemStorageEngine restarted = CreateStorage(directory.Path);
        await restarted.InitializeAsync(cancellationToken);
        Assert.Equal(1, await restarted.CountDocumentsAsync(definition.Id, cancellationToken));
    }

    [Fact]
    public async Task NewSegmentsUseMemoryPackAndRemainSmallerThanEquivalentJson()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("binary-segment", 384, DistanceMetric.Cosine);
        DocumentRecord document = Document("one", Enumerable.Repeat(0.125F, 384).ToArray()) with
        {
            Text = new string('x', 256),
            Metadata = new Dictionary<string, MetadataValue>
            {
                ["tenant"] = MetadataValue.From("blue"),
                ["rank"] = MetadataValue.From(42L),
            },
        };
        using (FileSystemStorageEngine storage = CreateStorage(directory.Path))
        {
            await storage.InitializeAsync(cancellationToken);
            await storage.CreateCollectionAsync(definition, cancellationToken);
            await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(document)], cancellationToken);
        }

        string segmentPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "collections", definition.Id.ToString("N"), "segments"),
            "*.segment"));
        byte[] contents = await File.ReadAllBytesAsync(segmentPath, cancellationToken);
        int newline = Array.IndexOf(contents, (byte)'\n');
        ReadOnlyMemory<byte> body = contents.AsMemory(newline + 1);
        Assert.True(body.Span.StartsWith("SVS2"u8));

        byte[] equivalentJson = CreateLegacySegmentJson(definition.Id, document);
        Assert.True(body.Length < equivalentJson.Length * 0.8, $"MemoryPack={body.Length}, JSON={equivalentJson.Length}");
    }

    [Fact]
    public async Task LegacyJsonSegmentRemainsReadableAndCompactionUpgradesIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        CollectionDefinition definition = CollectionDefinition.Create("legacy-json", 2, DistanceMetric.Cosine);
        DateTimeOffset updatedAt = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        DocumentRecord document = Document("legacy", [1, 0]) with { UpdatedAt = updatedAt };
        using (FileSystemStorageEngine storage = CreateStorage(directory.Path))
        {
            await storage.InitializeAsync(cancellationToken);
            await storage.CreateCollectionAsync(definition, cancellationToken);
            await storage.AppendAsync(definition.Id, [StorageOperation.Upsert(document)], cancellationToken);
        }

        string collectionPath = Path.Combine(directory.Path, "collections", definition.Id.ToString("N"));
        string segmentPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(collectionPath, "segments"), "*.segment"));
        byte[] legacyBody = CreateLegacySegmentJson(definition.Id, document);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(legacyBody));
        byte[] header = Encoding.ASCII.GetBytes(checksum + "\n");
        await File.WriteAllBytesAsync(segmentPath, [.. header, .. legacyBody], cancellationToken);

        string manifestPath = Path.Combine(collectionPath, "manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))!.AsObject();
        JsonObject descriptor = manifest["segments"]!.AsArray()[0]!.AsObject();
        descriptor["checksum"] = checksum;
        descriptor["length"] = legacyBody.LongLength;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(), cancellationToken);

        using (FileSystemStorageEngine restarted = CreateStorage(directory.Path))
        {
            await restarted.InitializeAsync(cancellationToken);
            IReadOnlyDictionary<string, DocumentRecord> loaded = await restarted.LoadDocumentsAsync(
                definition.Id,
                cancellationToken);
            Assert.Equal(document.Vector, loaded[document.Id].Vector);
            await restarted.AppendAsync(
                definition.Id,
                [StorageOperation.Upsert(document with { Version = 2 })],
                cancellationToken);
            await restarted.CompactAsync(definition.Id, cancellationToken);
        }

        byte[] upgraded = await File.ReadAllBytesAsync(
            Assert.Single(Directory.EnumerateFiles(Path.Combine(collectionPath, "segments"), "*.segment")),
            cancellationToken);
        Assert.True(upgraded.AsSpan(Array.IndexOf(upgraded, (byte)'\n') + 1).StartsWith("SVS2"u8));
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

    private static byte[] CreateLegacySegmentJson(Guid collectionId, DocumentRecord document)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("collectionId", collectionId);
            writer.WriteNumber("sequence", 1);
            writer.WriteString("createdAt", document.UpdatedAt);
            writer.WriteStartArray("operations");
            writer.WriteStartObject();
            writer.WriteString("kind", "upsert");
            writer.WriteString("id", document.Id);
            writer.WriteStartObject("document");
            writer.WriteString("id", document.Id);
            writer.WriteString("text", document.Text);
            writer.WriteStartArray("vector");
            foreach (float value in document.Vector)
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("metadata");
            foreach ((string key, MetadataValue value) in document.Metadata)
            {
                writer.WritePropertyName(key);
                WriteMetadataValue(writer, value);
            }

            writer.WriteEndObject();
            writer.WriteNumber("version", document.Version);
            writer.WriteString("updatedAt", document.UpdatedAt);
            writer.WriteEndObject();
            writer.WriteNumber("version", document.Version);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteMetadataValue(Utf8JsonWriter writer, MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Text:
                writer.WriteStringValue(value.StringValue);
                break;
            case MetadataValueKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue!.Value);
                break;
            case MetadataValueKind.Integral:
                writer.WriteNumberValue(value.IntegerValue!.Value);
                break;
            case MetadataValueKind.Number:
                writer.WriteNumberValue(value.NumberValue!.Value);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

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
