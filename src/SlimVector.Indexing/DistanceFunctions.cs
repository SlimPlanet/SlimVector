using System.Numerics;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public static class DistanceFunctions
{
    public static float Calculate(ReadOnlySpan<float> left, ReadOnlySpan<float> right, DistanceMetric metric)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Vectors must have the same dimension.", nameof(right));
        }

        if (left.IsEmpty)
        {
            throw new ArgumentException("Vectors may not be empty.", nameof(left));
        }

        return metric switch
        {
            DistanceMetric.Cosine => Cosine(left, right),
            DistanceMetric.DotProduct => -Dot(left, right),
            DistanceMetric.Euclidean => Euclidean(left, right),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown distance metric."),
        };
    }

    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ValidateDimensions(left, right);
        int width = Vector<float>.Count;
        int index = 0;
        Vector<float> accumulator = Vector<float>.Zero;
        for (; index <= left.Length - width; index += width)
        {
            accumulator += new Vector<float>(left.Slice(index, width)) * new Vector<float>(right.Slice(index, width));
        }

        float result = Vector.Sum(accumulator);
        for (; index < left.Length; index++)
        {
            result += left[index] * right[index];
        }

        return result;
    }

    public static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ValidateDimensions(left, right);
        int width = Vector<float>.Count;
        int index = 0;
        Vector<float> dotAccumulator = Vector<float>.Zero;
        Vector<float> leftAccumulator = Vector<float>.Zero;
        Vector<float> rightAccumulator = Vector<float>.Zero;

        for (; index <= left.Length - width; index += width)
        {
            Vector<float> leftVector = new(left.Slice(index, width));
            Vector<float> rightVector = new(right.Slice(index, width));
            dotAccumulator += leftVector * rightVector;
            leftAccumulator += leftVector * leftVector;
            rightAccumulator += rightVector * rightVector;
        }

        float dot = Vector.Sum(dotAccumulator);
        float leftNorm = Vector.Sum(leftAccumulator);
        float rightNorm = Vector.Sum(rightAccumulator);
        for (; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return leftNorm == rightNorm ? 0 : 1;
        }

        float similarity = dot / MathF.Sqrt(leftNorm * rightNorm);
        return 1 - Math.Clamp(similarity, -1, 1);
    }

    public static float Euclidean(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        ValidateDimensions(left, right);
        int width = Vector<float>.Count;
        int index = 0;
        Vector<float> accumulator = Vector<float>.Zero;
        for (; index <= left.Length - width; index += width)
        {
            Vector<float> difference = new Vector<float>(left.Slice(index, width)) - new Vector<float>(right.Slice(index, width));
            accumulator += difference * difference;
        }

        float squared = Vector.Sum(accumulator);
        for (; index < left.Length; index++)
        {
            float difference = left[index] - right[index];
            squared += difference * difference;
        }

        return MathF.Sqrt(squared);
    }

    private static void ValidateDimensions(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Vectors must have the same dimension.", nameof(right));
        }

        if (left.IsEmpty)
        {
            throw new ArgumentException("Vectors may not be empty.", nameof(left));
        }
    }
}
