using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class AdvancedVectorIndexTests
{
    public static TheoryData<VectorQuantizationKind> QuantizationKinds => new()
    {
        VectorQuantizationKind.Float32,
        VectorQuantizationKind.Float16,
        VectorQuantizationKind.Int8,
    };

    [Theory]
    [MemberData(nameof(QuantizationKinds))]
    public void ScalarQuantizationFindsAndPersistsNearestVector(VectorQuantizationKind kind)
    {
        ScalarQuantizedVectorIndex index = new(4, DistanceMetric.Euclidean, kind, rerankCandidateMultiplier: 3);
        index.Upsert("far", [10, 10, 10, 10]);
        index.Upsert("nearest", [1, 2, 3, 4]);
        index.Upsert("middle", [4, 4, 4, 4]);

        RankedResult result = Assert.Single(index.Search([1, 2, 3, 4], 1));

        Assert.Equal("nearest", result.Id);
        Assert.True(index.EncodedBytes > 0);
    }

    [Fact]
    public void IvfFlatTrainsMutatesFiltersAndRestores()
    {
        CollectionDefinition definition = Definition(VectorIndexKind.IvfFlat);
        IvfFlatIndex index = new(8, DistanceMetric.Euclidean, 4, 4, 8);
        (string Id, float[] Vector)[] vectors = Vectors(48, 8);
        index.Build(vectors);
        index.Upsert("exact", [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal("exact", Assert.Single(index.Search([1, 2, 3, 4, 5, 6, 7, 8], 1)).Id);
        Assert.Empty(index.Search([1, 2, 3, 4, 5, 6, 7, 8], 1, new HashSet<string>(["missing"], StringComparer.Ordinal)));

        IvfFlatIndex restored = Assert.IsType<IvfFlatIndex>(IvfFlatIndex.Deserialize(index.Serialize(), definition));
        Assert.Equal(index.Count, restored.Count);
        Assert.Equal("exact", Assert.Single(restored.Search([1, 2, 3, 4, 5, 6, 7, 8], 1)).Id);
        Assert.True(restored.Remove("exact"));
    }

    [Fact]
    public void IvfPqUsesResidualCodesAndExactReranking()
    {
        CollectionDefinition definition = Definition(VectorIndexKind.IvfPq);
        IvfPqIndex index = new(8, DistanceMetric.Euclidean, 4, 4, 8, 2, 16, 8, 8);
        (string Id, float[] Vector)[] vectors = Vectors(64, 8);
        index.Build(vectors);
        float[] query = vectors[37].Vector;

        IReadOnlyList<RankedResult> results = index.Search(query, 5);

        Assert.Contains(results, result => result.Id == vectors[37].Id);
        Assert.True(index.TrainedCodebookSize > 0);
        IvfPqIndex restored = Assert.IsType<IvfPqIndex>(IvfPqIndex.Deserialize(index.Serialize(), definition));
        Assert.Contains(restored.Search(query, 5), result => result.Id == vectors[37].Id);
    }

    [Fact]
    public void DiskAnnSearchesDiskCommitsDeltaAndRollsBackGeneration()
    {
        string path = Path.Combine(Path.GetTempPath(), "slimvector-diskann-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using DiskAnnIndex index = new(8, DistanceMetric.Euclidean, 8, 128, 4, 2, path, cachePages: 4);
            (string Id, float[] Vector)[] vectors = Vectors(48, 8);
            index.Build(vectors);
            long firstGeneration = index.Generation;
            float[] query = vectors[21].Vector;
            Assert.Contains(index.Search(query, 5), result => result.Id == vectors[21].Id);

            index.Upsert("delta-one", [1, 1, 1, 1, 1, 1, 1, 1]);
            index.Upsert("delta-two", [2, 2, 2, 2, 2, 2, 2, 2]);

            Assert.True(index.Generation > firstGeneration);
            Assert.Equal("delta-one", Assert.Single(index.Search([1, 1, 1, 1, 1, 1, 1, 1], 1)).Id);
            Assert.True(index.CacheMisses > 0);
            _ = index.Search(query, 5);
            Assert.True(index.CacheHits > 0);
            Assert.True(index.Rollback());
            Assert.DoesNotContain(index.Search([1, 1, 1, 1, 1, 1, 1, 1], 48), result => result.Id == "delta-one");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void CollectionSnapshotRestoresEveryAdvancedIndex()
    {
        foreach (VectorIndexKind kind in new[] { VectorIndexKind.IvfFlat, VectorIndexKind.IvfPq, VectorIndexKind.DiskAnn })
        {
            CollectionDefinition definition = Definition(kind);
            DocumentRecord[] documents = Vectors(32, 8).Select(item => new DocumentRecord
            {
                Id = item.Id,
                Vector = item.Vector,
                Text = "persistent vector index",
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
                Version = 1,
            }).ToArray();
            using CollectionSearchIndex original = new(definition, kind, documents, persistedVectorIndex: null);
            byte[] snapshot = original.Serialize(documents);
            using CollectionSearchIndex restored = new(definition, kind, documents, snapshot);

            Assert.True(restored.WasRestored);
            Assert.Equal(documents[7].Id, restored.Search(new SearchRequest
            {
                Mode = SearchMode.Vector,
                Vector = documents[7].Vector,
                Limit = 1,
            }, 4)[0].Id);
        }
    }

    private static CollectionDefinition Definition(VectorIndexKind kind) => CollectionDefinition.Create(
        "advanced-" + kind.ToString().ToLowerInvariant(),
        8,
        DistanceMetric.Euclidean,
        new VectorIndexConfiguration
        {
            Kind = kind,
            IvfListCount = 4,
            IvfProbeCount = 4,
            IvfTrainingIterations = 8,
            PqSubvectorCount = 2,
            PqCentroidCount = 16,
            PqTrainingIterations = 8,
            RerankCandidateMultiplier = 8,
            DiskAnnMaxDegree = 8,
            DiskAnnSearchListSize = 128,
            DiskAnnBeamWidth = 4,
            DiskAnnDeltaThreshold = 16,
        });

    private static (string Id, float[] Vector)[] Vectors(int count, int dimension)
    {
        Random random = new(73);
        return Enumerable.Range(0, count)
            .Select(index => (
                Id: "v-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Vector: Enumerable.Range(0, dimension).Select(_ => random.NextSingle() * 10).ToArray()))
            .ToArray();
    }
}
