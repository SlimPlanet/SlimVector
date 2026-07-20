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
        (string Id, float[] Vector)[] vectors = Vectors(640, 8);
        index.Build(vectors);
        float[] query = vectors[37].Vector;

        IReadOnlyList<RankedResult> results = index.Search(query, 5);

        Assert.Contains(results, result => result.Id == vectors[37].Id);
        Assert.True(index.TrainedCodebookSize > 0);
        IvfPqIndex restored = Assert.IsType<IvfPqIndex>(IvfPqIndex.Deserialize(index.Serialize(), definition));
        Assert.Contains(restored.Search(query, 5), result => result.Id == vectors[37].Id);
    }

    [Fact]
    public void IvfPqIncrementalIngestionMatchesBulkRecallBand()
    {
        (string Id, float[] Vector)[] vectors = Vectors(640, 8);
        FlatVectorIndex truth = new(8, DistanceMetric.Euclidean);
        IvfPqIndex bulk = new(8, DistanceMetric.Euclidean, 4, 4, 8, 2, 16, 8, 8);
        IvfPqIndex incremental = new(8, DistanceMetric.Euclidean, 4, 4, 8, 2, 16, 8, 8);
        foreach ((string id, float[] vector) in vectors)
        {
            truth.Upsert(id, vector);
        }
        bulk.Build(vectors);
        foreach ((string id, float[] vector) in vectors)
        {
            incremental.Upsert(id, vector);
        }

        double bulkRecall = 0;
        double incrementalRecall = 0;
        foreach ((string _, float[] query) in vectors.Take(20))
        {
            HashSet<string> expected = truth.Search(query, 10).Select(static result => result.Id).ToHashSet(StringComparer.Ordinal);
            bulkRecall += bulk.Search(query, 10).Count(result => expected.Contains(result.Id)) / 10D;
            incrementalRecall += incremental.Search(query, 10).Count(result => expected.Contains(result.Id)) / 10D;
        }

        bulkRecall /= 20;
        incrementalRecall /= 20;
        Assert.Equal(IvfTrainingState.Active, bulk.TrainingState);
        Assert.Equal(IvfTrainingState.Active, incremental.TrainingState);
        Assert.True(bulkRecall >= 0.8, $"Bulk recall was {bulkRecall:P2}.");
        Assert.True(incrementalRecall >= 0.8, $"Incremental recall was {incrementalRecall:P2}.");
        Assert.InRange(Math.Abs(bulkRecall - incrementalRecall), 0, 0.1);
    }

    [Fact]
    public void SmallIncrementalIvfCollectionsRemainExactAndSerializable()
    {
        CollectionDefinition definition = Definition(VectorIndexKind.IvfPq);
        IvfPqIndex index = new(8, DistanceMetric.Euclidean, 4, 4, 8, 2, 16, 8, 8);
        (string Id, float[] Vector)[] vectors = Vectors(200, 8);
        foreach ((string id, float[] vector) in vectors)
        {
            index.Upsert(id, vector);
        }

        Assert.Equal(IvfTrainingState.Collecting, index.TrainingState);
        Assert.Equal(0, index.TrainedCodebookSize);
        Assert.Equal(vectors[37].Id, Assert.Single(index.Search(vectors[37].Vector, 1)).Id);

        IvfPqIndex restored = Assert.IsType<IvfPqIndex>(IvfPqIndex.Deserialize(index.Serialize(), definition));
        Assert.Equal(IvfTrainingState.Collecting, restored.TrainingState);
        Assert.Equal(vectors[37].Id, Assert.Single(restored.Search(vectors[37].Vector, 1)).Id);
    }

    [Fact]
    public void IvfLifecycleMarksGrowthForRetrainingAndRebuildsDeterministically()
    {
        IvfFlatIndex index = new(8, DistanceMetric.Euclidean, 4, 4, 8);
        (string Id, float[] Vector)[] vectors = Vectors(312, 8);
        foreach ((string id, float[] vector) in vectors)
        {
            index.Upsert(id, vector);
        }

        Assert.Equal(IvfTrainingState.NeedsRetrain, index.TrainingState);
        Assert.True(index.RebuildIfNeeded());
        Assert.Equal(IvfTrainingState.Active, index.TrainingState);
        Assert.False(index.RebuildIfNeeded());
        Assert.Equal(vectors[157].Id, Assert.Single(index.Search(vectors[157].Vector, 1)).Id);
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
    public void DiskAnnCanCommitItsFirstGenerationFromIncrementalUpserts()
    {
        string path = Path.Combine(Path.GetTempPath(), "slimvector-diskann-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using DiskAnnIndex index = new(384, DistanceMetric.Cosine, 16, 128, 4, 100, path);
            index.Build([]);
            (string Id, float[] Vector)[] vectors = Vectors(100, 384);
            foreach ((string id, float[] vector) in vectors)
            {
                index.Upsert(id, vector);
            }

            Assert.Equal(2, index.Generation);
            Assert.Equal(100, index.Count);
            Assert.NotEmpty(index.Search(vectors[17].Vector, 10));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void DiskAnnWarmSearchStaysWithinAllocationBudget()
    {
        string path = Path.Combine(Path.GetTempPath(), "slimvector-diskann-allocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using DiskAnnIndex index = new(32, DistanceMetric.Euclidean, 12, 64, 4, 1_000, path, cachePages: 128);
            (string Id, float[] Vector)[] vectors = Vectors(512, 32);
            index.Build(vectors);
            float[] query = vectors[211].Vector;
            for (int warmup = 0; warmup < 16; warmup++)
            {
                _ = index.Search(query, 10);
            }

            const int searches = 200;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < searches; iteration++)
            {
                Assert.NotEmpty(index.Search(query, 10));
            }

            long allocatedPerSearch = (GC.GetAllocatedBytesForCurrentThread() - before) / searches;
            Assert.True(allocatedPerSearch < 16 * 1024, $"DiskANN allocated {allocatedPerSearch:N0} bytes/search.");
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
