using System.Text.Json;
namespace SlimVector.Benchmarks.Tests;

public sealed class ReliableBenchmarkIntegrationTests
{
    [Fact]
    public async Task CoordinatorUsesDistinctForksAndColdLoadProcesses()
    {
        string output = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            int exitCode = await ReliableBenchmarkRunner.RunAsync(
            [
                "--profile", "Smoke",
                "--indexes", "Flat-Float32",
                "--warmups", "0",
                "--repetitions", "2",
                "--skip-server",
                "--skip-raft",
                "--skip-migration",
                "--output", output,
            ]);
            Assert.Equal(0, exitCode);
            string runDirectory = Assert.Single(Directory.GetDirectories(output));
            string jsonPath = Path.Combine(runDirectory, "benchmark-results.json");
            await using FileStream stream = File.OpenRead(jsonPath);
            BenchmarkRunV5 run = await JsonSerializer.DeserializeAsync(
                stream,
                ReliableBenchmarkJsonContext.Default.BenchmarkRunV5,
                TestContext.Current.CancellationToken) ?? throw new InvalidDataException();
            BenchmarkScenarioAggregate result = Assert.Single(run.Results);
            Assert.Equal(2, result.Iterations.Select(static iteration => iteration.ProcessId).Distinct().Count());
            Assert.Equal(2, result.Iterations.Select(static iteration => iteration.ColdLoadProcessId).Distinct().Count());
            Assert.All(result.Iterations, static iteration => Assert.NotEqual(iteration.ProcessId, iteration.ColdLoadProcessId));
            Assert.Contains(result.Operations, static operation => operation.Operation == "DurableSnapshotWrite");
            Assert.Contains(result.Operations, static operation => operation.Operation == "ProcessColdLoad");
            Assert.True(File.Exists(Path.Combine(runDirectory, "benchmark-resource-samples.csv")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }
}
