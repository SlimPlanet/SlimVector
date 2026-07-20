using MemoryPack;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft.Tests;

public sealed class StorageRaftCommandApplierTests
{
    [Fact]
    public async Task GroupSnapshotsRestoreExactCatalogAndDocuments()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory sourceDirectory = new();
        using TemporaryDirectory targetDirectory = new();
        using FileSystemStorageEngine sourceStorage = Storage(sourceDirectory.Path);
        using FileSystemStorageEngine targetStorage = Storage(targetDirectory.Path);
        await sourceStorage.InitializeAsync(cancellationToken);
        await targetStorage.InitializeAsync(cancellationToken);
        using StorageRaftCommandApplier source = new(sourceStorage);
        using StorageRaftCommandApplier target = new(targetStorage);
        DateTimeOffset timestamp = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        CollectionDefinition collection = Definition(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            "articles",
            timestamp);
        DocumentRecord expected = Document("current", "snapshot state", 3, timestamp);
        await source.ApplyAsync(
            RaftCommandCodec.CatalogUpsert(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, collection, "data-0"),
            cancellationToken);
        await source.ApplyAsync(
            RaftCommandCodec.DataBatch(Guid.NewGuid(), "data-0", collection, [StorageOperation.Upsert(expected)]),
            cancellationToken);

        CollectionDefinition obsolete = Definition(Guid.NewGuid(), "obsolete", timestamp);
        await targetStorage.CreateCollectionAsync(obsolete, cancellationToken);
        await targetStorage.CreateCollectionAsync(collection, cancellationToken);
        await targetStorage.AppendAsync(
            collection.Id,
            [StorageOperation.Upsert(Document("stale", "remove me", 1, timestamp))],
            cancellationToken);

        byte[] catalogSnapshot = await source.CaptureSnapshotAsync(MultiRaftNode.CatalogGroupId, cancellationToken);
        byte[] dataSnapshot = await source.CaptureSnapshotAsync("data-0", cancellationToken);
        await target.RestoreSnapshotAsync(MultiRaftNode.CatalogGroupId, catalogSnapshot, cancellationToken);
        await target.RestoreSnapshotAsync("data-0", dataSnapshot, cancellationToken);

        CollectionDefinition restoredCollection = Assert.Single(await targetStorage.ListCollectionsAsync(cancellationToken));
        Assert.Equal(collection.Id, restoredCollection.Id);
        Assert.Equal(collection.Name, restoredCollection.Name);
        Assert.NotNull(restoredCollection.Placement);
        Assert.All(restoredCollection.Placement.Shards, static shard => Assert.Equal("data-0", shard.DataGroupId));
        IReadOnlyDictionary<string, DocumentRecord> restoredDocuments = await targetStorage
            .LoadDocumentsAsync(collection.Id, cancellationToken);
        DocumentRecord restored = Assert.Single(restoredDocuments).Value;
        Assert.Equal(expected.Id, restored.Id);
        Assert.Equal(expected.Text, restored.Text);
        Assert.Equal(expected.Vector, restored.Vector);
        Assert.Equal(expected.Version, restored.Version);
    }

    [Fact]
    public async Task DataGroupSnapshotsContainOnlyTheirVirtualShards()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        using FileSystemStorageEngine storage = Storage(directory.Path);
        await storage.InitializeAsync(cancellationToken);
        using StorageRaftCommandApplier applier = new(storage, ["data-0", "data-1"]);
        Guid collectionId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        CollectionDefinition collection = Definition(collectionId, "partitioned", DateTimeOffset.UtcNow) with
        {
            Placement = CollectionPlacement.Create(collectionId, ["data-0", "data-1"], 32),
        };
        string data0Id = Enumerable.Range(0, 1_000).Select(static index => $"doc-{index}")
            .First(id => collection.Placement.Resolve(collection.Id, id).DataGroupId == "data-0");
        string data1Id = Enumerable.Range(0, 1_000).Select(static index => $"doc-{index}")
            .First(id => collection.Placement.Resolve(collection.Id, id).DataGroupId == "data-1");
        await applier.ApplyAsync(
            RaftCommandCodec.CatalogUpsert(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, collection, "data-0"),
            cancellationToken);
        foreach (string id in new[] { data0Id, data1Id })
        {
            ShardRoute route = collection.Placement.Resolve(collection.Id, id);
            await applier.ApplyAsync(
                RaftCommandCodec.DataBatch(
                    Guid.NewGuid(),
                    route.DataGroupId,
                    collection,
                    [StorageOperation.Upsert(Document(id, id, 1, DateTimeOffset.UtcNow))],
                    route),
                cancellationToken);
        }

        RaftApplicationSnapshot data0 = MemoryPackSerializer.Deserialize<RaftApplicationSnapshot>(
            await applier.CaptureSnapshotAsync("data-0", cancellationToken))!;
        RaftApplicationSnapshot data1 = MemoryPackSerializer.Deserialize<RaftApplicationSnapshot>(
            await applier.CaptureSnapshotAsync("data-1", cancellationToken))!;

        Assert.Equal(data0Id, Assert.Single(Assert.Single(data0.Collections).Documents).Id);
        Assert.Equal(data1Id, Assert.Single(Assert.Single(data1.Collections).Documents).Id);

        ShardRoute currentRoute = collection.Placement.Resolve(collection.Id, data0Id);
        DomainException stale = await Assert.ThrowsAsync<DomainException>(() => applier.ApplyAsync(
            RaftCommandCodec.DataBatch(
                Guid.NewGuid(),
                currentRoute.DataGroupId,
                collection,
                [StorageOperation.Upsert(Document(data0Id, "stale", 2, DateTimeOffset.UtcNow))],
                currentRoute with { RoutingEpoch = currentRoute.RoutingEpoch + 1 }),
            cancellationToken).AsTask());
        Assert.Equal(ErrorCodes.RoutingEpochMismatch, stale.Code);
    }

    private static FileSystemStorageEngine Storage(string path) => new(new StorageSettings
    {
        Path = path,
        FlushToDisk = false,
    });

    private static CollectionDefinition Definition(Guid id, string name, DateTimeOffset timestamp) => new()
    {
        Id = id,
        Name = name,
        Dimension = 2,
        Metric = DistanceMetric.Cosine,
        CreatedAt = timestamp,
        UpdatedAt = timestamp,
    };

    private static DocumentRecord Document(string id, string text, long version, DateTimeOffset timestamp) => new()
    {
        Id = id,
        Text = text,
        Vector = [1, 0],
        Metadata = new Dictionary<string, MetadataValue> { ["source"] = MetadataValue.From("raft") },
        Version = version,
        UpdatedAt = timestamp,
    };
}
