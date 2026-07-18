using BenchmarkDotNet.Attributes;
using SlimVector.Domain;
using SlimVector.Indexing;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class FlatIndexBenchmarks
{
    private readonly FlatVectorIndex _index = new(384, DistanceMetric.Cosine);
    private readonly float[] _query = new float[384];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        for (int document = 0; document < 10_000; document++)
        {
            float[] vector = new float[384];
            for (int index = 0; index < vector.Length; index++)
            {
                vector[index] = random.NextSingle();
            }

            _index.Upsert(document.ToString(System.Globalization.CultureInfo.InvariantCulture), vector);
        }
    }

    [Benchmark]
    public IReadOnlyList<RankedResult> SearchTopTen() => _index.Search(_query, 10);
}
