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
        Assert.Equal(collection, restoredCollection);
        IReadOnlyDictionary<string, DocumentRecord> restoredDocuments = await targetStorage
            .LoadDocumentsAsync(collection.Id, cancellationToken);
        DocumentRecord restored = Assert.Single(restoredDocuments).Value;
        Assert.Equal(expected.Id, restored.Id);
        Assert.Equal(expected.Text, restored.Text);
        Assert.Equal(expected.Vector, restored.Vector);
        Assert.Equal(expected.Version, restored.Version);
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
