using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SlimVector.Benchmarks;

internal static class BenchmarkReportWriter
{
    public static void Write(string runDirectory, string outputRoot, BenchmarkRun run)
    {
        string jsonPath = Path.Combine(runDirectory, "benchmark-results.json");
        using (FileStream stream = new(jsonPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, run, BenchmarkJsonContext.Default.BenchmarkRun);
        }

        File.WriteAllText(Path.Combine(runDirectory, "benchmark-results.csv"), Csv(run), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-operations.csv"), OperationsCsv(run), Encoding.UTF8);
        string markdown = Markdown(run);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-summary.md"), markdown, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputRoot, "benchmark-summary.md"), markdown, Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-report.html"), Html(run), Encoding.UTF8);
    }

    private static string Markdown(BenchmarkRun run)
    {
        StringBuilder builder = new();
        builder.AppendLine("# SlimVector benchmark summary");
        builder.AppendLine();
        builder.AppendLine($"Measured at `{run.Environment.StartedAt:O}` with profile `{run.Environment.Profile}` and version `{run.Environment.Version}`.");
        builder.AppendLine();
        builder.AppendLine($"Environment: {run.Environment.OperatingSystem}, {run.Environment.Architecture}, {run.Environment.Framework}, {run.Environment.ProcessorCount} logical CPUs ({run.Environment.CpuModel}), server GC `{run.Environment.ServerGc}`, commit `{run.Environment.Commit}`.");
        builder.AppendLine();
        builder.AppendLine($"Dataset: {run.Environment.VectorCount:N0} vectors × {run.Environment.Dimension} dimensions, {run.Environment.QueryCount} queries, top-{run.Environment.TopK}. Disk: {run.Environment.DiskFormat}, {run.Environment.DiskAvailableBytes / 1_073_741_824D:F1} GiB free of {run.Environment.DiskTotalBytes / 1_073_741_824D:F1} GiB.");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Build/catch-up ms | p50 ms | p95 ms | p99 ms | QPS/entries s⁻¹ | Recall@k | Errors | Disk MiB | GC 0/1/2 | Baseline p95 | Regression |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            EndToEndBenchmarkResult? baseline = Baseline(run, result.Scenario);
            string regression = baseline is null || baseline.P95Milliseconds <= 0
                ? "n/a"
                : Regression(
                    (result.P95Milliseconds - baseline.P95Milliseconds) / baseline.P95Milliseconds,
                    run.RegressionThreshold);
            builder.Append("| ").Append(result.Scenario)
                .Append(" | ").Append(Format(result.BuildMilliseconds))
                .Append(" | ").Append(Format(result.P50Milliseconds))
                .Append(" | ").Append(Format(result.P95Milliseconds))
                .Append(" | ").Append(Format(result.P99Milliseconds))
                .Append(" | ").Append(Format(result.ThroughputQueriesPerSecond))
                .Append(" | ").Append(Format(result.RecallAtK))
                .Append(" | ").Append(FormatRatio(result.ErrorRate))
                .Append(" | ").Append(Format(result.DiskBytes / 1_048_576D))
                .Append(" | ").Append(result.Gen0Collections).Append('/').Append(result.Gen1Collections).Append('/').Append(result.Gen2Collections)
                .Append(" | ").Append(baseline is null ? "n/a" : Format(baseline.P95Milliseconds))
                .Append(" | ").Append(regression).AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Index CRUD and resource comparison");
        builder.AppendLine();
        builder.AppendLine("| Index | Quantization | Build ms | Insert items/s | Select q/s | Select p95 ms | Update items/s | Delete items/s | Persist ms | Cold-load ms | CPU total s | RAM idle/peak MiB | RAM Δ MiB | Disk MiB |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (EndToEndBenchmarkResult result in run.Results.Where(static result =>
                     result.Operations.Any(operation => string.Equals(operation.Operation, "IndexBuild", StringComparison.Ordinal))))
        {
            OperationBenchmarkResult build = RequiredOperation(result, "IndexBuild");
            OperationBenchmarkResult insert = RequiredOperation(result, "Insert");
            OperationBenchmarkResult select = RequiredOperation(result, "SelectVector");
            OperationBenchmarkResult update = RequiredOperation(result, "Update");
            OperationBenchmarkResult delete = RequiredOperation(result, "Delete");
            OperationBenchmarkResult persist = RequiredOperation(result, "PersistSnapshot");
            OperationBenchmarkResult coldLoad = RequiredOperation(result, "ColdLoad");
            builder.Append("| ").Append(result.IndexKind)
                .Append(" | ").Append(result.Quantization)
                .Append(" | ").Append(Format(build.WallMilliseconds))
                .Append(" | ").Append(Format(insert.ThroughputPerSecond))
                .Append(" | ").Append(Format(select.ThroughputPerSecond))
                .Append(" | ").Append(Format(select.P95Milliseconds))
                .Append(" | ").Append(Format(update.ThroughputPerSecond))
                .Append(" | ").Append(Format(delete.ThroughputPerSecond))
                .Append(" | ").Append(Format(persist.WallMilliseconds))
                .Append(" | ").Append(Format(coldLoad.WallMilliseconds))
                .Append(" | ").Append(Format(result.CpuSeconds))
                .Append(" | ").Append(FormatMib(result.IdleWorkingSetBytes)).Append('/').Append(FormatMib(result.PeakWorkingSetBytes))
                .Append(" | ").Append(FormatMib(result.WorkingSetBytesDelta))
                .Append(" | ").Append(FormatMib(result.DiskBytes))
                .AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Detailed operation comparison");
        builder.AppendLine();
        builder.AppendLine("Latencies are measured per operation sample (one HTTP batch for batched API writes); throughput uses the number of affected documents or executed queries.");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Operation | Items | Samples | Wall ms | items/s | p50 ms | p95 ms | p99 ms | CPU s | CPU avg/peak | RAM before/avg/peak MiB | RAM Δ MiB | Managed Δ MiB | Alloc MiB | GC 0/1/2 | GC pause ms | Disk Δ MiB | Errors | Baseline wall | Regression |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            foreach (OperationBenchmarkResult operation in result.Operations)
            {
                OperationBenchmarkResult? baseline = BaselineOperation(run, result.Scenario, operation.Operation);
                string regression = baseline is null || baseline.WallMilliseconds <= 0
                    ? "n/a"
                    : Regression(
                        (operation.WallMilliseconds - baseline.WallMilliseconds) / baseline.WallMilliseconds,
                        run.RegressionThreshold);
                builder.Append("| ").Append(result.Scenario)
                    .Append(" | ").Append(operation.Operation)
                    .Append(" | ").Append(operation.ItemCount)
                    .Append(" | ").Append(operation.SampleCount)
                    .Append(" | ").Append(Format(operation.WallMilliseconds))
                    .Append(" | ").Append(Format(operation.ThroughputPerSecond))
                    .Append(" | ").Append(Format(operation.P50Milliseconds))
                    .Append(" | ").Append(Format(operation.P95Milliseconds))
                    .Append(" | ").Append(Format(operation.P99Milliseconds))
                    .Append(" | ").Append(Format(operation.CpuSeconds))
                    .Append(" | ").Append(FormatRatio(operation.AverageCpuUtilization)).Append('/').Append(FormatRatio(operation.PeakCpuUtilization))
                    .Append(" | ").Append(FormatMib(operation.WorkingSetBytesBefore)).Append('/').Append(FormatMib(operation.AverageWorkingSetBytes)).Append('/').Append(FormatMib(operation.PeakWorkingSetBytes))
                    .Append(" | ").Append(FormatMib(operation.WorkingSetBytesDelta))
                    .Append(" | ").Append(FormatMib(operation.ManagedBytesDelta))
                    .Append(" | ").Append(FormatMib(operation.AllocatedBytes))
                    .Append(" | ").Append(operation.Gen0Collections).Append('/').Append(operation.Gen1Collections).Append('/').Append(operation.Gen2Collections)
                    .Append(" | ").Append(Format(operation.GcPauseMilliseconds))
                    .Append(" | ").Append(FormatMib(operation.DiskBytesDelta))
                    .Append(" | ").Append(operation.ErrorCount)
                    .Append(" | ").Append(baseline is null ? "n/a" : Format(baseline.WallMilliseconds))
                    .Append(" | ").Append(regression).AppendLine(" |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Auto Index evidence");
        builder.AppendLine();
        EndToEndBenchmarkResult? recommendation = run.Results
            .Where(static result => result.Failure is null && result.IndexKind is not "Raft" && result.RecallAtK >= 0.90 && result.P95Milliseconds > 0)
            .OrderBy(static result => result.P95Milliseconds)
            .FirstOrDefault();
        builder.AppendLine(recommendation is null
            ? "No measured approximate scenario met recall 0.90; keep Flat or tune search breadth before changing Auto thresholds."
            : $"At the executed {run.Environment.VectorCount:N0} × {run.Environment.Dimension} workload, `{recommendation.Scenario}` had the lowest measured p95 among scenarios with recall@k ≥ 0.90. Treat this as one measured threshold anchor, not a universal default.");
        builder.AppendLine();
        string[] allProfiles = ["Smoke", "Standard", "Large"];
        builder.AppendLine("Profile coverage: " + string.Join(", ", allProfiles.Select(profile =>
            string.Equals(profile, run.Environment.Profile, StringComparison.OrdinalIgnoreCase)
                ? $"{profile} executed"
                : $"{profile} not executed")) + ".");
        EndToEndBenchmarkResult? server = run.Results.FirstOrDefault(static result =>
            string.Equals(result.Scenario, "Server-HTTP-Mixed-Saturation", StringComparison.Ordinal));
        if (server is not null && server.Failure is null)
        {
            builder.AppendLine();
            builder.AppendLine($"Real HTTP mixed workload evidence: {server.MixedReadSuccesses} reads succeeded alongside concurrent writes, with {server.QueueSaturationRejections} bounded-queue rejections, {server.CongestionRejections} adaptive-congestion rejections, and {server.RateLimitRejections} contractual rate-limit rejections; Retry-After was {server.RateLimitRetryAfterSeconds:F0}s.");
        }

        if (run.Environment.Configuration is BenchmarkConfiguration configuration)
        {
            builder.AppendLine();
            builder.AppendLine($"SlimVector workload configuration: seed {configuration.RandomSeed}, IVF {configuration.IvfLists} lists/{configuration.IvfProbes} probes, HNSW M={configuration.HnswDegree}/efConstruction={configuration.HnswConstruction}/efSearch={configuration.HnswSearch}, DiskANN degree={configuration.DiskAnnDegree}/search-list={configuration.DiskAnnSearchList}.");
        }

        EndToEndBenchmarkResult[] failures = run.Results.Where(static result => result.Failure is not null).ToArray();
        if (failures.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failures");
            builder.AppendLine();
            foreach (EndToEndBenchmarkResult failure in failures)
            {
                builder.Append("- ").Append(failure.Scenario).Append(": ").AppendLine(failure.Failure);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Raw measured data is in `benchmark-results.json`, `benchmark-results.csv`, and `benchmark-operations.csv`; the HTML report contains scenario and per-operation charts.");
        return builder.ToString();
    }

    private static string OperationsCsv(BenchmarkRun run)
    {
        StringBuilder builder = new();
        builder.AppendLine("scenario,indexKind,quantization,operation,itemCount,sampleCount,wallMs,itemsPerSecond,p50Ms,p95Ms,p99Ms,cpuSeconds,avgCpuUtilization,peakCpuUtilization,workingSetBefore,workingSetAfter,workingSetDelta,avgWorkingSet,peakWorkingSet,managedBefore,managedAfter,managedDelta,allocatedBytes,gen0,gen1,gen2,lohBytesDelta,gcPauseMs,diskBytesDelta,errorCount,baselineWallMs,wallRegression");
        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            foreach (OperationBenchmarkResult operation in result.Operations)
            {
                OperationBenchmarkResult? baseline = BaselineOperation(run, result.Scenario, operation.Operation);
                string[] values =
                [
                    result.Scenario,
                    result.IndexKind,
                    result.Quantization,
                    operation.Operation,
                    operation.ItemCount.ToString(CultureInfo.InvariantCulture),
                    operation.SampleCount.ToString(CultureInfo.InvariantCulture),
                    FormatRaw(operation.WallMilliseconds),
                    FormatRaw(operation.ThroughputPerSecond),
                    FormatRaw(operation.P50Milliseconds),
                    FormatRaw(operation.P95Milliseconds),
                    FormatRaw(operation.P99Milliseconds),
                    FormatRaw(operation.CpuSeconds),
                    FormatRaw(operation.AverageCpuUtilization),
                    FormatRaw(operation.PeakCpuUtilization),
                    operation.WorkingSetBytesBefore.ToString(CultureInfo.InvariantCulture),
                    operation.WorkingSetBytesAfter.ToString(CultureInfo.InvariantCulture),
                    operation.WorkingSetBytesDelta.ToString(CultureInfo.InvariantCulture),
                    operation.AverageWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                    operation.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                    operation.ManagedBytesBefore.ToString(CultureInfo.InvariantCulture),
                    operation.ManagedBytesAfter.ToString(CultureInfo.InvariantCulture),
                    operation.ManagedBytesDelta.ToString(CultureInfo.InvariantCulture),
                    operation.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    operation.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                    operation.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                    operation.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                    operation.LohBytesDelta.ToString(CultureInfo.InvariantCulture),
                    FormatRaw(operation.GcPauseMilliseconds),
                    operation.DiskBytesDelta.ToString(CultureInfo.InvariantCulture),
                    operation.ErrorCount.ToString(CultureInfo.InvariantCulture),
                    baseline is null ? string.Empty : FormatRaw(baseline.WallMilliseconds),
                    baseline is null || baseline.WallMilliseconds <= 0
                        ? string.Empty
                        : FormatRaw((operation.WallMilliseconds - baseline.WallMilliseconds) / baseline.WallMilliseconds),
                ];
                builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
            }
        }

        return builder.ToString();
    }

    private static string Csv(BenchmarkRun run)
    {
        StringBuilder builder = new();
        builder.AppendLine("scenario,indexKind,quantization,vectorCount,dimension,buildMs,persistMs,coldLoadMs,qps,p50Ms,p95Ms,p99Ms,recallAtK,cpuSeconds,avgCpuUtilization,peakCpuUtilization,idleWorkingSetBytes,managedBytesDelta,workingSetBytesDelta,avgWorkingSetBytes,peakWorkingSetBytes,managedBytesFreedAfterDispose,lohBytesDelta,gcPauseMs,diskBytes,snapshotBytes,diskWriteBytesPerSecond,gen0,gen1,gen2,migrationMs,raftCatchUpMs,ingestMs,requestCount,errorCount,errorRate,mixedReadSuccesses,backpressureRejections,queueSaturationRejections,congestionRejections,rateLimitRejections,rateLimitRetryAfterSeconds,failure");
        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            string[] values =
            [
                result.Scenario,
                result.IndexKind,
                result.Quantization,
                result.VectorCount.ToString(CultureInfo.InvariantCulture),
                result.Dimension.ToString(CultureInfo.InvariantCulture),
                FormatRaw(result.BuildMilliseconds),
                FormatRaw(result.PersistMilliseconds),
                FormatRaw(result.ColdLoadMilliseconds),
                FormatRaw(result.ThroughputQueriesPerSecond),
                FormatRaw(result.P50Milliseconds),
                FormatRaw(result.P95Milliseconds),
                FormatRaw(result.P99Milliseconds),
                FormatRaw(result.RecallAtK),
                FormatRaw(result.CpuSeconds),
                FormatRaw(result.CpuUtilization),
                FormatRaw(result.PeakCpuUtilization),
                result.IdleWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                result.ManagedBytesDelta.ToString(CultureInfo.InvariantCulture),
                result.WorkingSetBytesDelta.ToString(CultureInfo.InvariantCulture),
                result.AverageWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                result.PeakWorkingSetBytes.ToString(CultureInfo.InvariantCulture),
                result.ManagedBytesFreedAfterDispose.ToString(CultureInfo.InvariantCulture),
                result.LohBytesDelta.ToString(CultureInfo.InvariantCulture),
                FormatRaw(result.GcPauseMilliseconds),
                result.DiskBytes.ToString(CultureInfo.InvariantCulture),
                result.SnapshotBytes.ToString(CultureInfo.InvariantCulture),
                FormatRaw(result.DiskWriteBytesPerSecond),
                result.Gen0Collections.ToString(CultureInfo.InvariantCulture),
                result.Gen1Collections.ToString(CultureInfo.InvariantCulture),
                result.Gen2Collections.ToString(CultureInfo.InvariantCulture),
                result.MigrationMilliseconds.HasValue ? FormatRaw(result.MigrationMilliseconds.Value) : string.Empty,
                result.RaftCatchUpMilliseconds.HasValue ? FormatRaw(result.RaftCatchUpMilliseconds.Value) : string.Empty,
                result.IngestMilliseconds.HasValue ? FormatRaw(result.IngestMilliseconds.Value) : string.Empty,
                result.RequestCount.ToString(CultureInfo.InvariantCulture),
                result.ErrorCount.ToString(CultureInfo.InvariantCulture),
                FormatRaw(result.ErrorRate),
                result.MixedReadSuccesses.ToString(CultureInfo.InvariantCulture),
                result.BackpressureRejections.ToString(CultureInfo.InvariantCulture),
                result.QueueSaturationRejections.ToString(CultureInfo.InvariantCulture),
                result.CongestionRejections.ToString(CultureInfo.InvariantCulture),
                result.RateLimitRejections.ToString(CultureInfo.InvariantCulture),
                FormatRaw(result.RateLimitRetryAfterSeconds),
                result.Failure ?? string.Empty,
            ];
            builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    private static string Html(BenchmarkRun run)
    {
        double maximumP95 = Math.Max(0.001, run.Results.Max(static result => result.P95Milliseconds));
        StringBuilder rows = new();
        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            double width = result.P95Milliseconds / maximumP95 * 100;
            rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(result.Scenario)).Append("</td><td>")
                .Append(Format(result.P95Milliseconds)).Append("</td><td><div class=\"bar latency\" style=\"width:")
                .Append(width.ToString("F2", CultureInfo.InvariantCulture)).Append("%\"></div></td><td>")
                .Append(Format(result.RecallAtK)).Append("</td><td><div class=\"bar recall\" style=\"width:")
                .Append((result.RecallAtK * 100).ToString("F2", CultureInfo.InvariantCulture)).AppendLine("%\"></div></td></tr>");
        }

        OperationBenchmarkResult[] operations = run.Results.SelectMany(static result => result.Operations).ToArray();
        double maximumOperationWall = Math.Max(0.001, operations.Length == 0 ? 0 : operations.Max(static operation => operation.WallMilliseconds));
        long maximumOperationMemory = Math.Max(1, operations.Length == 0 ? 0 : operations.Max(static operation => operation.PeakWorkingSetBytes));
        StringBuilder operationRows = new();
        StringBuilder indexRows = new();
        foreach (EndToEndBenchmarkResult result in run.Results.Where(static result =>
                     result.Operations.Any(operation => string.Equals(operation.Operation, "IndexBuild", StringComparison.Ordinal))))
        {
            OperationBenchmarkResult build = RequiredOperation(result, "IndexBuild");
            OperationBenchmarkResult insert = RequiredOperation(result, "Insert");
            OperationBenchmarkResult select = RequiredOperation(result, "SelectVector");
            OperationBenchmarkResult update = RequiredOperation(result, "Update");
            OperationBenchmarkResult delete = RequiredOperation(result, "Delete");
            indexRows.Append("<tr><td>").Append(WebUtility.HtmlEncode(result.IndexKind)).Append("</td><td>")
                .Append(WebUtility.HtmlEncode(result.Quantization)).Append("</td><td>")
                .Append(Format(build.WallMilliseconds)).Append("</td><td>")
                .Append(Format(insert.ThroughputPerSecond)).Append("</td><td>")
                .Append(Format(select.ThroughputPerSecond)).Append("</td><td>")
                .Append(Format(select.P95Milliseconds)).Append("</td><td>")
                .Append(Format(update.ThroughputPerSecond)).Append("</td><td>")
                .Append(Format(delete.ThroughputPerSecond)).Append("</td><td>")
                .Append(Format(result.CpuSeconds)).Append("</td><td>")
                .Append(FormatMib(result.PeakWorkingSetBytes)).AppendLine("</td></tr>");
        }

        foreach (EndToEndBenchmarkResult result in run.Results)
        {
            foreach (OperationBenchmarkResult operation in result.Operations)
            {
                double wallWidth = operation.WallMilliseconds / maximumOperationWall * 100;
                double memoryWidth = operation.PeakWorkingSetBytes / (double)maximumOperationMemory * 100;
                operationRows.Append("<tr><td>").Append(WebUtility.HtmlEncode(result.Scenario)).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(operation.Operation)).Append("</td><td>")
                    .Append(operation.ItemCount.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                    .Append(Format(operation.WallMilliseconds)).Append("</td><td><div class=\"bar operation\" style=\"width:")
                    .Append(wallWidth.ToString("F2", CultureInfo.InvariantCulture)).Append("%\"></div></td><td>")
                    .Append(Format(operation.ThroughputPerSecond)).Append("</td><td>")
                    .Append(Format(operation.P95Milliseconds)).Append("</td><td>")
                    .Append(Format(operation.CpuSeconds)).Append("</td><td>")
                    .Append(FormatRatio(operation.PeakCpuUtilization)).Append("</td><td>")
                    .Append(FormatMib(operation.PeakWorkingSetBytes)).Append("</td><td><div class=\"bar memory\" style=\"width:")
                    .Append(memoryWidth.ToString("F2", CultureInfo.InvariantCulture)).Append("%\"></div></td><td>")
                    .Append(FormatMib(operation.AllocatedBytes)).AppendLine("</td></tr>");
            }
        }

        string title = WebUtility.HtmlEncode($"SlimVector {run.Environment.Profile} benchmark – {run.Environment.Version}");
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title +
            "</title><style>body{font:14px system-ui;margin:2rem;color:#172033}table{border-collapse:collapse;width:100%;margin-bottom:2rem}th,td{padding:.55rem;border-bottom:1px solid #ccd3df;text-align:left}.chart{width:24%}.bar{height:1rem;border-radius:3px}.latency{background:#ef8354}.recall{background:#3b82f6}.operation{background:#8b5cf6}.memory{background:#10b981}code{background:#edf1f7;padding:.15rem .3rem}</style></head><body><h1>" + title +
            "</h1><p>Measured <code>" + run.Environment.StartedAt.ToString("O", CultureInfo.InvariantCulture) +
            "</code> on " + WebUtility.HtmlEncode(run.Environment.OperatingSystem) + ", " +
            WebUtility.HtmlEncode(run.Environment.Architecture) + ".</p><h2>Scenario comparison</h2><table><thead><tr><th>Scenario</th><th>p95 ms</th><th class=\"chart\">Relative latency</th><th>Recall@k</th><th class=\"chart\">Recall</th></tr></thead><tbody>" +
            rows + "</tbody></table><h2>Index CRUD comparison</h2><table><thead><tr><th>Index</th><th>Quantization</th><th>Build ms</th><th>Insert/s</th><th>Select/s</th><th>Select p95 ms</th><th>Update/s</th><th>Delete/s</th><th>CPU total s</th><th>Peak RAM MiB</th></tr></thead><tbody>" +
            indexRows + "</tbody></table><h2>Detailed operations</h2><table><thead><tr><th>Scenario</th><th>Operation</th><th>Items</th><th>Wall ms</th><th class=\"chart\">Relative wall time</th><th>items/s</th><th>p95 ms</th><th>CPU s</th><th>Peak CPU</th><th>Peak RAM MiB</th><th class=\"chart\">Relative RAM</th><th>Allocated MiB</th></tr></thead><tbody>" +
            operationRows + "</tbody></table></body></html>";
    }

    private static EndToEndBenchmarkResult? Baseline(BenchmarkRun run, string scenario) => run.Baseline?.Results
        .FirstOrDefault(result => string.Equals(result.Scenario, scenario, StringComparison.Ordinal));

    private static OperationBenchmarkResult? BaselineOperation(BenchmarkRun run, string scenario, string operation) =>
        Baseline(run, scenario)?.Operations.FirstOrDefault(candidate =>
            string.Equals(candidate.Operation, operation, StringComparison.Ordinal));

    private static OperationBenchmarkResult RequiredOperation(EndToEndBenchmarkResult result, string operation) =>
        result.Operations.Single(candidate => string.Equals(candidate.Operation, operation, StringComparison.Ordinal));

    private static string EscapeCsv(string value) => value.IndexOfAny([',', '"', '\n', '\r']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Format(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string FormatRaw(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static string FormatRatio(double value) => value.ToString("P1", CultureInfo.InvariantCulture);

    private static string FormatMib(long value) => (value / 1_048_576D).ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatPercent(double value) => value.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture);

    private static string Regression(double value, double threshold) =>
        FormatPercent(value) + (value > threshold ? " ⚠" : string.Empty);
}
