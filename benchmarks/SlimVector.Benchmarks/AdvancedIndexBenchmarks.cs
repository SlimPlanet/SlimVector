using BenchmarkDotNet.Attributes;
using SlimVector.Domain;
using SlimVector.Indexing;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public sealed class AdvancedIndexBenchmarks : IDisposable
{
    private CollectionSearchIndex _index = null!;
    private DocumentRecord[] _documents = [];
    private SearchRequest _query = null!;
    private string _artifactPath = string.Empty;

    [Params(VectorIndexKind.Flat, VectorIndexKind.Hnsw, VectorIndexKind.IvfFlat, VectorIndexKind.IvfPq, VectorIndexKind.DiskAnn)]
    public VectorIndexKind Kind { get; set; }

    [Params(1_000, 10_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int dimension = 128;
        Random random = new(42);
        _documents = Enumerable.Range(0, DocumentCount).Select(index => new DocumentRecord
        {
            Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Text = "advanced vector benchmark",
            Vector = Enumerable.Range(0, dimension).Select(_ => random.NextSingle()).ToArray(),
            Metadata = [],
            Version = 1,
        }).ToArray();
        VectorIndexConfiguration configuration = new()
        {
            Kind = Kind,
            IvfListCount = Math.Min(64, DocumentCount),
            IvfProbeCount = Math.Min(8, Math.Min(64, DocumentCount)),
            PqSubvectorCount = 8,
            PqCentroidCount = Math.Min(64, DocumentCount),
            RerankCandidateMultiplier = 4,
            DiskAnnMaxDegree = 16,
            DiskAnnSearchListSize = 64,
            DiskAnnDeltaThreshold = 1_000,
        };
        CollectionDefinition definition = CollectionDefinition.Create(
            "advanced-benchmark",
            dimension,
            DistanceMetric.Cosine,
            configuration);
        _artifactPath = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks", Guid.NewGuid().ToString("N"));
        _index = new CollectionSearchIndex(
            definition,
            Kind,
            _documents,
            persistedVectorIndex: null,
            diskAnnArtifactDirectory: _artifactPath);
        _query = new SearchRequest
        {
            Mode = SearchMode.Vector,
            Vector = _documents[DocumentCount / 2].Vector,
            Limit = 10,
        };
    }

    [Benchmark]
    public IReadOnlyList<HybridRankedResult> SearchTopTen() => _index.Search(_query, 4);

    [Benchmark]
    public byte[] PersistGeneration() => _index.Serialize(_documents);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _index?.Dispose();
        if (Directory.Exists(_artifactPath))
        {
            Directory.Delete(_artifactPath, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
