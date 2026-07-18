using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class CollectionSearchIndexPersistenceTests
{
    [Theory]
    [InlineData(VectorIndexKind.Flat)]
    [InlineData(VectorIndexKind.Hnsw)]
    public void SnapshotRestoresVectorTextAndMetadataIndexes(VectorIndexKind kind)
    {
        CollectionDefinition definition = CollectionDefinition.Create(
            "persistent",
            3,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration
            {
                Kind = kind,
                HnswM = 8,
                HnswEfConstruction = 40,
                HnswEfSearch = 20,
            });
        DocumentRecord[] documents =
        [
            Document("one", "distributed vector database", [1, 0, 0], 2026),
            Document("two", "text search engine", [0, 1, 0], 2024),
            Document("three", "hybrid database search", [0.9f, 0.1f, 0], 2025),
        ];
        CollectionSearchIndex original = new(definition, kind, documents, persistedVectorIndex: null);
        byte[] snapshot = original.Serialize(documents);

        CollectionSearchIndex restored = new(definition, kind, documents, snapshot);

        Assert.True(restored.WasRestored);
        SearchRequest hybrid = new()
        {
            Mode = SearchMode.Hybrid,
            Text = "database",
            Vector = [1, 0, 0],
            Limit = 3,
        };
        Assert.Equal(
            original.Search(hybrid, 4).Select(static result => result.Id),
            restored.Search(hybrid, 4).Select(static result => result.Id));
        SearchRequest metadata = new()
        {
            Mode = SearchMode.Metadata,
            Limit = 10,
            Filter = new MetadataFilter
            {
                Operator = MetadataOperator.GreaterThanOrEqual,
                Field = "year",
                Value = MetadataValue.From(2025L),
            },
        };
        Assert.Equal(
            original.Search(metadata, 4).Select(static result => result.Id),
            restored.Search(metadata, 4).Select(static result => result.Id));
    }

    [Fact]
    public void StaleSnapshotIsRejectedAndAllIndexesAreRebuilt()
    {
        CollectionDefinition definition = CollectionDefinition.Create("stale", 2, DistanceMetric.Euclidean);
        DocumentRecord[] initial = [Document("one", "old text", [1, 0], 2024)];
        CollectionSearchIndex original = new(definition, VectorIndexKind.Flat, initial, persistedVectorIndex: null);
        byte[] snapshot = original.Serialize(initial);
        DocumentRecord[] changed = [Document("one", "new text", [0, 1], 2026) with { Version = 2 }];

        CollectionSearchIndex rebuilt = new(definition, VectorIndexKind.Flat, changed, snapshot);

        Assert.False(rebuilt.WasRestored);
        SearchRequest request = new() { Mode = SearchMode.Text, Text = "new", Limit = 1 };
        Assert.Equal("one", Assert.Single(rebuilt.Search(request, 4)).Id);
    }

    [Fact]
    public void DisabledMetadataIndexUsesCorrectScanFallbackAndPersistsOtherIndexes()
    {
        CollectionDefinition definition = CollectionDefinition.Create(
            "unindexed",
            2,
            DistanceMetric.Cosine,
            timeProvider: null,
            metadataIndexed: false);
        DocumentRecord[] documents =
        [
            Document("one", "first", [1, 0], 2026),
            Document("two", "second", [0, 1], 2024),
        ];
        CollectionSearchIndex original = new(definition, VectorIndexKind.Flat, documents, persistedVectorIndex: null);
        byte[] snapshot = original.Serialize(documents);

        CollectionSearchIndex restored = new(definition, VectorIndexKind.Flat, documents, snapshot);
        SearchRequest request = new()
        {
            Mode = SearchMode.Metadata,
            Limit = 10,
            Filter = new MetadataFilter
            {
                Operator = MetadataOperator.And,
                Operands =
                [
                    new MetadataFilter
                    {
                        Operator = MetadataOperator.GreaterThan,
                        Field = "year",
                        Value = MetadataValue.From(2025L),
                    },
                    new MetadataFilter
                    {
                        Operator = MetadataOperator.Equal,
                        Field = "tags",
                        Value = MetadataValue.From("search"),
                    },
                ],
            },
        };

        Assert.True(restored.WasRestored);
        Assert.Equal("one", Assert.Single(restored.Search(request, 4)).Id);
    }

    private static DocumentRecord Document(string id, string text, float[] vector, long year) => new()
    {
        Id = id,
        Text = text,
        Vector = vector,
        Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["year"] = MetadataValue.From(year),
            ["tags"] = MetadataValue.From(["search", id]),
        },
        Version = 1,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };
}
