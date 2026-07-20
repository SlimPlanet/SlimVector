using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Placement;
using SlimVector.Domain;

namespace SlimVector.Benchmarks;

internal sealed record CapacityScaleOutResult(
    int NodeCount,
    int ReplicationFactor,
    long RawBytes,
    long ReservedBytes,
    long UsableBytes,
    int PlannedGroupCount,
    double MaximumAssignedSkewRatio,
    double MedianPlanningMilliseconds);

internal static class CapacityScaleOutBenchmarkRunner
{
    private static readonly int[] NodeCounts = [3, 6, 9];

    public static int Run(string[] args)
    {
        long nodeCapacityGiB = ParsePositiveLong(GetArgument(args, "--node-capacity-gib") ?? "1024");
        string? output = GetArgument(args, "--output");
        DataPlacementOptions options = new();
        SharedNothingPlacementPlanner planner = new(Options.Create(options), TimeProvider.System);
        CapacityScaleOutResult[] results = NodeCounts.Select(nodeCount => Measure(
            planner,
            options,
            nodeCount,
            checked(nodeCapacityGiB * 1_024 * 1_024 * 1_024))).ToArray();
        StringBuilder table = new("nodes,rf,rawGiB,reservedGiB,usableGiB,groups,maxAssignedSkew,planningMedianMs\n");
        foreach (CapacityScaleOutResult result in results)
        {
            table.Append(result.NodeCount).Append(',').Append(result.ReplicationFactor).Append(',')
                .Append(ToGiB(result.RawBytes)).Append(',').Append(ToGiB(result.ReservedBytes)).Append(',')
                .Append(ToGiB(result.UsableBytes)).Append(',').Append(result.PlannedGroupCount).Append(',')
                .Append(result.MaximumAssignedSkewRatio.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(result.MedianPlanningMilliseconds.ToString("F6", CultureInfo.InvariantCulture)).AppendLine();
        }

        Console.Write(table);
        if (!string.IsNullOrWhiteSpace(output))
        {
            string directory = Path.GetFullPath(output);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "capacity-scaleout.csv"), table.ToString(), Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(directory, "capacity-scaleout.json"),
                JsonSerializer.Serialize(results, CapacityScaleOutJsonContext.Default.CapacityScaleOutResultArray),
                Encoding.UTF8);
        }

        long usableThree = results[0].UsableBytes;
        bool capacityLinear = Math.Abs(results[1].UsableBytes - usableThree * 2) <= 1 &&
            Math.Abs(results[2].UsableBytes - usableThree * 3) <= 1;
        bool balanced = results.All(static result => result.MaximumAssignedSkewRatio < 0.15);
        return capacityLinear && balanced ? 0 : 1;
    }

    private static CapacityScaleOutResult Measure(
        SharedNothingPlacementPlanner planner,
        DataPlacementOptions options,
        int nodeCount,
        long nodeCapacityBytes)
    {
        ClusterTopology topology = new()
        {
            Nodes = Enumerable.Range(0, nodeCount).Select(index => new ClusterNodeDescriptor
            {
                NodeId = $"node-{index + 1}",
                ApiEndpoint = $"https://node-{index + 1}:8080",
                InternalEndpoint = $"https://node-{index + 1}:8080",
                RaftHost = $"10.0.0.{index + 1}",
                Zone = $"zone-{index % 3}",
                CapacityBytes = nodeCapacityBytes,
                RaftPortStart = 4_000,
                RaftPortCount = 128,
                State = ClusterNodeState.Active,
                LastSeenAt = DateTimeOffset.UtcNow,
            }).ToArray(),
        };
        double[] elapsed = new double[101];
        SharedNothingRebalancePlan? plan = null;
        for (int iteration = 0; iteration < elapsed.Length; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            plan = planner.Plan(topology);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(elapsed);
        long raw = checked(nodeCapacityBytes * nodeCount);
        long reserved = (long)Math.Ceiling(raw * options.ReserveRatio);
        long minimum = plan!.AssignedBytesAfter.Values.Min();
        long maximum = plan.AssignedBytesAfter.Values.Max();
        return new CapacityScaleOutResult(
            nodeCount,
            options.ReplicationFactor,
            raw,
            reserved,
            Math.Max(0, raw - reserved) / options.ReplicationFactor,
            plan.GroupsToCreate.Count,
            (maximum - minimum) / (double)Math.Max(1, maximum),
            elapsed[elapsed.Length / 2]);
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static long ParsePositiveLong(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException("--node-capacity-gib must be a positive integer.");

    private static string ToGiB(long bytes) => (bytes / (double)(1L << 30)).ToString("F3", CultureInfo.InvariantCulture);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CapacityScaleOutResult[]))]
internal sealed partial class CapacityScaleOutJsonContext : JsonSerializerContext;
