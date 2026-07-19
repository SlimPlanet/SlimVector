using System.Security.Cryptography;
using System.Text;
using SlimVector.Domain;

namespace SlimVector.Indexing;

internal static class VectorIndexUtilities
{
    public static void ValidateVector(ReadOnlySpan<float> vector, int dimension)
    {
        if (vector.Length != dimension)
        {
            throw new DomainException(
                ErrorCodes.DimensionMismatch,
                $"Expected a vector with dimension {dimension}, but received {vector.Length} values.");
        }

        for (int index = 0; index < vector.Length; index++)
        {
            if (!float.IsFinite(vector[index]))
            {
                throw new DomainException(ErrorCodes.InvalidVector, "Vectors may contain only finite values.");
            }
        }
    }

    public static int StableIndex(string value, int count)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        uint number = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash);
        return (int)(number % (uint)count);
    }

    public static RankedResult[] Drain(PriorityQueue<RankedResult, double> queue)
    {
        RankedResult[] results = new RankedResult[queue.Count];
        for (int index = results.Length - 1; index >= 0; index--)
        {
            results[index] = queue.Dequeue();
        }

        Array.Sort(results, static (left, right) =>
        {
            int comparison = left.Score.CompareTo(right.Score);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });
        return results;
    }

    public static float[][] TrainKMeans(
        IReadOnlyList<float[]> vectors,
        int dimension,
        int requestedCentroids,
        int iterations,
        int seedOffset = 0)
    {
        if (vectors.Count == 0)
        {
            return [];
        }

        int centroidCount = Math.Min(requestedCentroids, vectors.Count);
        float[][] centroids = new float[centroidCount][];
        int first = Math.Abs(seedOffset % vectors.Count);
        centroids[0] = (float[])vectors[first].Clone();
        double[] nearestSquared = new double[vectors.Count];
        Array.Fill(nearestSquared, double.PositiveInfinity);

        for (int centroid = 1; centroid < centroidCount; centroid++)
        {
            double total = 0;
            for (int vectorIndex = 0; vectorIndex < vectors.Count; vectorIndex++)
            {
                double squared = SquaredEuclidean(vectors[vectorIndex], centroids[centroid - 1]);
                nearestSquared[vectorIndex] = Math.Min(nearestSquared[vectorIndex], squared);
                total += nearestSquared[vectorIndex];
            }

            if (total <= double.Epsilon)
            {
                centroids[centroid] = (float[])vectors[(first + centroid) % vectors.Count].Clone();
                continue;
            }

            double target = total * Halton(centroid + 1 + seedOffset, 2);
            double cumulative = 0;
            int selected = vectors.Count - 1;
            for (int vectorIndex = 0; vectorIndex < vectors.Count; vectorIndex++)
            {
                cumulative += nearestSquared[vectorIndex];
                if (cumulative >= target)
                {
                    selected = vectorIndex;
                    break;
                }
            }

            centroids[centroid] = (float[])vectors[selected].Clone();
        }

        int[] assignments = new int[vectors.Count];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            float[][] sums = Enumerable.Range(0, centroidCount).Select(_ => new float[dimension]).ToArray();
            int[] counts = new int[centroidCount];
            bool changed = false;
            for (int vectorIndex = 0; vectorIndex < vectors.Count; vectorIndex++)
            {
                int nearest = FindNearest(vectors[vectorIndex], centroids);
                changed |= iteration == 0 || assignments[vectorIndex] != nearest;
                assignments[vectorIndex] = nearest;
                counts[nearest]++;
                ReadOnlySpan<float> vector = vectors[vectorIndex];
                for (int coordinate = 0; coordinate < dimension; coordinate++)
                {
                    sums[nearest][coordinate] += vector[coordinate];
                }
            }

            for (int centroid = 0; centroid < centroidCount; centroid++)
            {
                if (counts[centroid] == 0)
                {
                    centroids[centroid] = (float[])vectors[(first + iteration + centroid) % vectors.Count].Clone();
                    continue;
                }

                float inverse = 1F / counts[centroid];
                for (int coordinate = 0; coordinate < dimension; coordinate++)
                {
                    sums[centroid][coordinate] *= inverse;
                }

                centroids[centroid] = sums[centroid];
            }

            if (!changed)
            {
                break;
            }
        }

        return centroids;
    }

    public static int FindNearest(ReadOnlySpan<float> vector, IReadOnlyList<float[]> centroids)
    {
        int nearest = 0;
        double best = double.PositiveInfinity;
        for (int index = 0; index < centroids.Count; index++)
        {
            double distance = SquaredEuclidean(vector, centroids[index]);
            if (distance < best)
            {
                best = distance;
                nearest = index;
            }
        }

        return nearest;
    }

    public static double SquaredEuclidean(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double result = 0;
        for (int index = 0; index < left.Length; index++)
        {
            double difference = left[index] - right[index];
            result += difference * difference;
        }

        return result;
    }

    private static double Halton(int index, int @base)
    {
        double fraction = 1;
        double result = 0;
        int value = Math.Abs(index);
        while (value > 0)
        {
            fraction /= @base;
            result += fraction * (value % @base);
            value /= @base;
        }

        return result;
    }
}
