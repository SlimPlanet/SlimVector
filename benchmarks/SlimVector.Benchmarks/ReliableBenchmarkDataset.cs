using System.Globalization;
using System.Security.Cryptography;
using SlimVector.Domain;
using SlimVector.Indexing;

namespace SlimVector.Benchmarks;

internal sealed record ReliableBenchmarkDataset(
    DocumentRecord[] Documents,
    float[][] Queries,
    HashSet<string>[] Truth);

internal static class ReliableBenchmarkDatasetFactory
{
    private const int Seed = 20260719;

    public static BenchmarkDatasetSpecification Specification(
        string kind,
        string? vectorsPath,
        string? queriesPath,
        string? truthPath)
    {
        if (string.Equals(kind, "synthetic", StringComparison.OrdinalIgnoreCase))
        {
            return new BenchmarkDatasetSpecification { Kind = "synthetic", Fingerprint = "synthetic-clustered-v1-seed-20260719" };
        }

        if (!string.Equals(kind, "fvecs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--dataset must be synthetic or fvecs.", nameof(kind));
        }

        string vectors = RequiredFile(vectorsPath, "--vectors-path");
        string queries = RequiredFile(queriesPath, "--queries-path");
        string? truth = string.IsNullOrWhiteSpace(truthPath) ? null : RequiredFile(truthPath, "--truth-path");
        string fingerprint = string.Join(':', new[] { vectors, queries, truth }.Where(static path => path is not null)
            .Select(static path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path!)))));
        return new BenchmarkDatasetSpecification
        {
            Kind = "fvecs",
            VectorsPath = vectors,
            QueriesPath = queries,
            TruthPath = truth,
            Fingerprint = fingerprint,
        };
    }

    public static ReliableBenchmarkDataset Create(
        ReliableBenchmarkProfile profile,
        BenchmarkDatasetSpecification specification,
        bool includeQueriesAndTruth = true)
    {
        if (string.Equals(specification.Kind, "fvecs", StringComparison.Ordinal))
        {
            return CreateFvecs(profile, specification, includeQueriesAndTruth);
        }

        return CreateSynthetic(profile, includeQueriesAndTruth);
    }

    private static ReliableBenchmarkDataset CreateSynthetic(ReliableBenchmarkProfile profile, bool includeQueriesAndTruth)
    {
        Random random = new(Seed);
        int clusterCount = Math.Clamp(profile.VectorCount / 32, 8, 64);
        float[][] centers = Enumerable.Range(0, clusterCount)
            .Select(_ => Normalize(Enumerable.Range(0, profile.Dimension).Select(_ => (float)NextGaussian(random)).ToArray()))
            .ToArray();
        DocumentRecord[] documents = new DocumentRecord[profile.VectorCount];
        for (int index = 0; index < documents.Length; index++)
        {
            int cluster = Math.Min(clusterCount - 1, (int)(Math.Pow(random.NextDouble(), 2.2) * clusterCount));
            float[] vector = Perturb(centers[cluster], random, 0.12);
            documents[index] = new DocumentRecord
            {
                Id = "doc-" + index.ToString(CultureInfo.InvariantCulture),
                Text = $"topic-{cluster} vector retrieval document token-{index % 97} quality-{index % 7}",
                Vector = vector,
                Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["cluster"] = MetadataValue.From((long)cluster),
                    ["category"] = MetadataValue.From("category-" + (cluster % 8).ToString(CultureInfo.InvariantCulture)),
                    ["active"] = MetadataValue.From(index % 5 != 0),
                    ["created"] = MetadataValue.From(DateTimeOffset.UnixEpoch.AddMinutes(index)),
                    ["tags"] = MetadataValue.From(["benchmark", "cluster-" + cluster.ToString(CultureInfo.InvariantCulture)]),
                },
                Version = 1,
                UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
            };
        }

        if (!includeQueriesAndTruth)
        {
            return new ReliableBenchmarkDataset(documents, [], []);
        }

        float[][] queries = new float[profile.QueryCount][];
        for (int index = 0; index < queries.Length; index++)
        {
            int cluster = Math.Min(clusterCount - 1, (int)(Math.Pow(random.NextDouble(), 1.8) * clusterCount));
            queries[index] = Perturb(centers[cluster], random, index % 10 == 0 ? 0.35 : 0.16);
        }

        return new ReliableBenchmarkDataset(documents, queries, BuildTruth(documents, queries, profile.TopK));
    }

    private static ReliableBenchmarkDataset CreateFvecs(
        ReliableBenchmarkProfile profile,
        BenchmarkDatasetSpecification specification,
        bool includeQueriesAndTruth)
    {
        float[][] vectors = ReadFvecs(specification.VectorsPath!, profile.VectorCount, profile.Dimension);
        DocumentRecord[] documents = vectors.Select((vector, index) => new DocumentRecord
        {
            Id = "doc-" + index.ToString(CultureInfo.InvariantCulture),
            Text = "imported vector benchmark document",
            Vector = vector,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["source"] = MetadataValue.From("fvecs"),
                ["ordinal"] = MetadataValue.From((long)index),
            },
            Version = 1,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }).ToArray();
        if (!includeQueriesAndTruth)
        {
            return new ReliableBenchmarkDataset(documents, [], []);
        }

        float[][] queries = ReadFvecs(specification.QueriesPath!, profile.QueryCount, profile.Dimension);
        HashSet<string>[] truth = specification.TruthPath is null
            ? BuildTruth(documents, queries, profile.TopK)
            : ReadIvecs(specification.TruthPath, queries.Length, profile.TopK, documents.Length);
        return new ReliableBenchmarkDataset(documents, queries, truth);
    }

    internal static HashSet<string>[] BuildTruth(DocumentRecord[] documents, float[][] queries, int topK)
    {
        FlatVectorIndex exact = new(documents[0].Vector.Length, DistanceMetric.Cosine);
        foreach (DocumentRecord document in documents)
        {
            exact.Upsert(document.Id, document.Vector);
        }

        return queries.Select(query => exact.Search(query, topK)
            .Select(static result => result.Id)
            .ToHashSet(StringComparer.Ordinal)).ToArray();
    }

    private static float[][] ReadFvecs(string path, int count, int expectedDimension)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        List<float[]> vectors = new(count);
        while (stream.Position < stream.Length && vectors.Count < count)
        {
            int dimension = reader.ReadInt32();
            if (dimension != expectedDimension)
            {
                throw new InvalidDataException($"Vector dimension {dimension} in '{path}' does not match {expectedDimension}.");
            }

            float[] vector = new float[dimension];
            for (int index = 0; index < dimension; index++)
            {
                vector[index] = reader.ReadSingle();
            }

            vectors.Add(vector);
        }

        if (vectors.Count != count)
        {
            throw new InvalidDataException($"'{path}' contains {vectors.Count} vectors but {count} are required.");
        }

        return vectors.ToArray();
    }

    private static HashSet<string>[] ReadIvecs(string path, int count, int topK, int vectorCount)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        HashSet<string>[] truth = new HashSet<string>[count];
        for (int query = 0; query < count; query++)
        {
            if (stream.Position >= stream.Length)
            {
                throw new InvalidDataException($"'{path}' contains fewer than {count} truth rows.");
            }

            int neighbors = reader.ReadInt32();
            HashSet<string> ids = new(StringComparer.Ordinal);
            for (int index = 0; index < neighbors; index++)
            {
                int ordinal = reader.ReadInt32();
                if (index < topK && ordinal >= 0 && ordinal < vectorCount)
                {
                    ids.Add("doc-" + ordinal.ToString(CultureInfo.InvariantCulture));
                }
            }

            truth[query] = ids;
        }

        return truth;
    }

    private static float[] Perturb(float[] center, Random random, double noise)
    {
        float[] vector = new float[center.Length];
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = center[index] + (float)(NextGaussian(random) * noise);
        }

        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        double norm = Math.Sqrt(vector.Sum(static value => value * value));
        if (norm <= double.Epsilon)
        {
            vector[0] = 1;
            return vector;
        }

        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = (float)(vector[index] / norm);
        }

        return vector;
    }

    private static double NextGaussian(Random random)
    {
        double first = 1 - random.NextDouble();
        double second = 1 - random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(first)) * Math.Cos(2 * Math.PI * second);
    }

    private static string RequiredFile(string? path, string option)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{option} is required for --dataset fvecs.");
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Dataset file '{fullPath}' was not found.", fullPath);
        }

        return fullPath;
    }
}
