using SlimVector.Domain;

namespace SlimVector.Storage.Tests;

public sealed class FileSystemStorageEngineTests
{
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
}
