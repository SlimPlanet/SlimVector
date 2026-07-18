using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class HnswIndexTests
{
    [Fact]
    public void SearchFindsNearestNeighborAndHonorsCandidates()
    {
        HnswIndex index = new(3, DistanceMetric.Euclidean, m: 8, efConstruction: 64, efSearch: 32);
        for (int value = 0; value < 100; value++)
        {
            index.Upsert(value.ToString(System.Globalization.CultureInfo.InvariantCulture), [value, 0, 0]);
        }

        Assert.Equal("42", index.Search([42.1f, 0, 0], 1)[0].Id);

        HashSet<string> candidates = new(StringComparer.Ordinal) { "40", "44" };
        Assert.Equal("44", index.Search([43.8f, 0, 0], 1, candidates)[0].Id);
    }

    [Fact]
    public void MemoryPackSnapshotRoundTripsAndRejectsStaleSignature()
    {
        CollectionDefinition definition = CollectionDefinition.Create(
            "hnsw",
            2,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration { Kind = VectorIndexKind.Hnsw, HnswM = 8, HnswEfConstruction = 64, HnswEfSearch = 32 });
        DocumentRecord[] documents =
        [
            Document("one", [1, 0], 1),
            Document("two", [0, 1], 1),
        ];
        HnswIndex index = new(2, DistanceMetric.Cosine, 8, 64, 32);
        foreach (DocumentRecord document in documents)
        {
            index.Upsert(document.Id, document.Vector);
        }

        string signature = HnswIndex.ComputeDocumentSignature(documents);
        byte[] snapshot = index.Serialize(signature);
        HnswIndex? restored = HnswIndex.Deserialize(snapshot, definition, signature);

        Assert.NotNull(restored);
        Assert.Equal("one", restored.Search([1, 0], 1)[0].Id);
        Assert.Null(HnswIndex.Deserialize(snapshot, definition, "stale"));
    }

    private static DocumentRecord Document(string id, float[] vector, long version) => new()
    {
        Id = id,
        Text = id,
        Vector = vector,
        Metadata = [],
        Version = version,
    };
}
