namespace SlimVector.Benchmarks;

internal static class ReliableBenchmarkStatistics
{
    public static MetricDistribution Distribution(
        IEnumerable<double> source,
        int seed = 20260719,
        string unit = "value")
    {
        double[] values = source.Order().ToArray();
        if (values.Length == 0)
        {
            return new MetricDistribution { Unit = unit };
        }

        double mean = values.Average();
        double deviation = values.Length < 2
            ? 0
            : Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1));
        (double low, double high) = BootstrapMedian(values, seed);
        return new MetricDistribution
        {
            Unit = unit,
            Count = values.Length,
            Median = Percentile(values, 0.50),
            Minimum = values[0],
            Maximum = values[^1],
            Mean = mean,
            StandardDeviation = deviation,
            ConfidenceLow95 = low,
            ConfidenceHigh95 = high,
        };
    }

    public static MetricDistribution? NullableDistribution(
        IEnumerable<double?> source,
        int seed = 20260719,
        string unit = "value")
    {
        double[] values = source.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return values.Length == 0 ? null : Distribution(values, seed, unit);
    }

    public static double? QualifiedPercentile(IEnumerable<double> source, double percentile)
    {
        double[] values = source.Order().ToArray();
        int required = percentile switch
        {
            >= 0.99 => 100,
            >= 0.95 => 20,
            _ => 2,
        };
        return values.Length < required ? null : Percentile(values, percentile);
    }

    public static bool ConfidenceIntervalsOverlap(MetricDistribution left, MetricDistribution right) =>
        left.ConfidenceLow95 <= right.ConfidenceHigh95 && right.ConfidenceLow95 <= left.ConfidenceHigh95;

    public static bool IsRegression(
        MetricDistribution current,
        MetricDistribution baseline,
        double relativeThreshold,
        double absoluteThreshold,
        bool lowerIsBetter = true)
    {
        if (current.Count == 0 || baseline.Count == 0 || ConfidenceIntervalsOverlap(current, baseline))
        {
            return false;
        }

        double direction = lowerIsBetter ? current.Median - baseline.Median : baseline.Median - current.Median;
        double denominator = Math.Abs(baseline.Median);
        double relative = denominator <= double.Epsilon ? double.PositiveInfinity : direction / denominator;
        return direction > absoluteThreshold && relative > relativeThreshold;
    }

    private static (double Low, double High) BootstrapMedian(double[] values, int seed)
    {
        Random random = new(seed);
        double[] medians = new double[10_000];
        double[] sample = new double[values.Length];
        for (int iteration = 0; iteration < medians.Length; iteration++)
        {
            for (int index = 0; index < sample.Length; index++)
            {
                sample[index] = values[random.Next(values.Length)];
            }

            Array.Sort(sample);
            medians[iteration] = Percentile(sample, 0.50);
        }

        Array.Sort(medians);
        return (Percentile(medians, 0.025), Percentile(medians, 0.975));
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        double position = (ordered.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        double fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }
}
