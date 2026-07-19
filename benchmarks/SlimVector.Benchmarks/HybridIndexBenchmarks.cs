using BenchmarkDotNet.Attributes;
using SlimVector.Domain;
using SlimVector.Indexing;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class HybridIndexBenchmarks : IDisposable
{
    private CollectionSearchIndex _flat = null!;
    private CollectionSearchIndex _hnsw = null!;
    private SearchRequest _vector = null!;
    private SearchRequest _text = null!;
    private SearchRequest _metadata = null!;
    private SearchRequest _hybrid = null!;

    [Params(1_000, 10_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int dimension = 128;
        Random random = new(42);
        DocumentRecord[] documents = Enumerable.Range(0, DocumentCount).Select(index => new DocumentRecord
        {
            Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Text = index % 4 == 0 ? "distributed vector database" : "unrelated searchable document",
            Vector = Enumerable.Range(0, dimension).Select(_ => random.NextSingle()).ToArray(),
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["bucket"] = MetadataValue.From((long)(index % 10)),
            },
            Version = 1,
        }).ToArray();
        CollectionDefinition flatDefinition = CollectionDefinition.Create(
            "flat",
            dimension,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration { Kind = VectorIndexKind.Flat });
        CollectionDefinition hnswDefinition = CollectionDefinition.Create(
            "hnsw",
            dimension,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration
            {
                Kind = VectorIndexKind.Hnsw,
                HnswM = 16,
                HnswEfConstruction = 100,
                HnswEfSearch = 64,
            });
        _flat = new CollectionSearchIndex(flatDefinition, VectorIndexKind.Flat, documents, persistedVectorIndex: null);
        _hnsw = new CollectionSearchIndex(hnswDefinition, VectorIndexKind.Hnsw, documents, persistedVectorIndex: null);
        float[] query = documents[DocumentCount / 2].Vector;
        MetadataFilter filter = new()
        {
            Operator = MetadataOperator.Equal,
            Field = "bucket",
            Value = MetadataValue.From(5L),
        };
        _vector = new SearchRequest { Mode = SearchMode.Vector, Vector = query, Limit = 10 };
        _text = new SearchRequest { Mode = SearchMode.Text, Text = "vector database", Limit = 10 };
        _metadata = new SearchRequest { Mode = SearchMode.Metadata, Filter = filter, Limit = 10 };
        _hybrid = new SearchRequest
        {
            Mode = SearchMode.Hybrid,
            Text = "vector database",
            Vector = query,
            Filter = filter,
            Limit = 10,
        };
    }

    [Benchmark(Baseline = true)]
    public IReadOnlyList<HybridRankedResult> FlatVector() => _flat.Search(_vector, 4);

    [Benchmark]
    public IReadOnlyList<HybridRankedResult> HnswVector() => _hnsw.Search(_vector, 4);

    [Benchmark]
    public IReadOnlyList<HybridRankedResult> Bm25Text() => _flat.Search(_text, 4);

    [Benchmark]
    public IReadOnlyList<HybridRankedResult> MetadataOnly() => _flat.Search(_metadata, 4);

    [Benchmark]
    public IReadOnlyList<HybridRankedResult> FilteredHybrid() => _hnsw.Search(_hybrid, 4);

    public void Dispose()
    {
        _flat?.Dispose();
        _hnsw?.Dispose();
        GC.SuppressFinalize(this);
    }
}
