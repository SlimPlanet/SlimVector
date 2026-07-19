using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SlimVector.Benchmarks;

internal static class ReliableBenchmarkReportWriter
{
    public static void Write(string runDirectory, string outputRoot, BenchmarkRunV5 run, double[] recallThresholds)
    {
        Directory.CreateDirectory(runDirectory);
        using (FileStream stream = File.Create(Path.Combine(runDirectory, "benchmark-results.json")))
        {
            JsonSerializer.Serialize(stream, run, ReliableBenchmarkJsonContext.Default.BenchmarkRunV5);
        }

        File.WriteAllText(Path.Combine(runDirectory, "benchmark-results.csv"), ScenarioCsv(run), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-operations.csv"), OperationCsv(run), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-resource-samples.csv"), ResourceCsv(run), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-summary.md"), Markdown(run, recallThresholds), Encoding.UTF8);
        File.WriteAllText(Path.Combine(runDirectory, "benchmark-report.html"), Html(run, recallThresholds), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputRoot, "latest.txt"), runDirectory, Encoding.UTF8);
    }

    private static string Markdown(BenchmarkRunV5 run, double[] recallThresholds)
    {
        StringBuilder builder = new();
        BenchmarkEnvironmentV5 environment = run.Environment;
        builder.AppendLine("# SlimVector reliable benchmark summary").AppendLine();
        builder.Append("Measured with schema v5 at `").Append(environment.StartedAt.ToString("O", CultureInfo.InvariantCulture))
            .Append("`, profile `").Append(environment.Profile).Append("`, ").Append(environment.Repetitions)
            .Append(" measured forks after ").Append(environment.Warmups).AppendLine(" warm-up fork(s).").AppendLine();
        builder.Append("Environment: ").Append(environment.OperatingSystem).Append(", ").Append(environment.Architecture)
            .Append(", ").Append(environment.Framework).Append(", ").Append(environment.ProcessorCount)
            .Append(" logical CPUs (`").Append(environment.CpuModel).Append("`), server GC `")
            .Append(environment.ServerGc).AppendLine("`.").AppendLine();
        builder.Append("Dataset: ").Append(environment.VectorCount.ToString("N0", CultureInfo.InvariantCulture)).Append(" × ")
            .Append(environment.Dimension).Append(" vectors, ").Append(environment.QueryCount).Append(" independent queries, top-")
            .Append(environment.TopK).Append("; fingerprint `").Append(environment.DatasetFingerprint).AppendLine("`.").AppendLine();
        builder.Append("Process cold-load cache state: `").Append(environment.ProcessColdLoadCacheState).AppendLine("`.").AppendLine();
        builder.Append("Protocol: `").Append(environment.ProtocolVersion).Append("`; mutation batches ")
            .Append(environment.MutationBatchSize).Append(", pressure ").Append(environment.PressureProbeRequests)
            .Append(" requests × ").Append(environment.DocumentsPerPressureRequest).AppendLine(" documents.").AppendLine();
        builder.Append("Baseline: **").Append(run.BaselineStatus).Append("**. Significant regression: **")
            .Append(run.HasSignificantRegression ? "yes" : "no").Append("**. Runtime: ")
            .Append(TimeSpan.FromSeconds(run.DurationSeconds).ToString("c", CultureInfo.InvariantCulture)).AppendLine(".").AppendLine();
        builder.AppendLine("## Scenario comparison").AppendLine();
        builder.AppendLine("| Scenario | Index | Tuning | Storage | Recall median | Select p95 ms | Throughput median | Errors | Expected rejections |");
        builder.AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|");
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            OperationAggregate? select = SelectOperation(scenario);
            builder.Append("| ").Append(scenario.Scenario).Append(" | ").Append(scenario.IndexKind).Append(" | ")
                .Append(scenario.SearchTuning?.ToString(CultureInfo.InvariantCulture) ?? "n/a").Append(" | ")
                .Append(scenario.StorageMode?.ToString() ?? "n/a").Append(" | ").Append(Format(scenario.RecallAtK.Median))
                .Append(" | ").Append(Format(select?.P95Milliseconds)).Append(" | ")
                .Append(Format(select?.ThroughputPerSecond.Median)).Append(" | ").Append(scenario.ErrorCount)
                .Append(" | ").Append(scenario.ExpectedRejectionCount).AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Recall-qualified recommendations").AppendLine();
        foreach (double threshold in recallThresholds)
        {
            BenchmarkScenarioAggregate? recommendation = Recommendation(run.Results, threshold);
            if (recommendation is null)
            {
                BenchmarkScenarioAggregate? highest = LocalIndexResults(run.Results)
                    .OrderByDescending(static result => result.RecallAtK.Median).FirstOrDefault();
                builder.Append("- Recall ≥ ").Append(threshold.ToString("F2", CultureInfo.InvariantCulture))
                    .Append(": no eligible configuration");
                if (highest is not null)
                {
                    builder.Append("; highest measured recall was `").Append(highest.Scenario).Append("` at ")
                        .Append(Format(highest.RecallAtK.Median));
                }

                builder.AppendLine(".");
            }
            else
            {
                builder.Append("- Recall ≥ ").Append(threshold.ToString("F2", CultureInfo.InvariantCulture)).Append(": `")
                    .Append(recommendation.Scenario).Append("` with select p95 ")
                    .Append(Format(SelectOperation(recommendation)?.P95Milliseconds)).AppendLine(" ms.");
            }
        }

        builder.AppendLine().AppendLine("## Detailed operations").AppendLine();
        builder.AppendLine("Percentiles are `n/a` until their minimum sample count is met. CPU average is expressed as core-equivalents; disk columns are logical instrumented I/O, while artifact delta is only a directory-size change.").AppendLine();
        builder.AppendLine("| Scenario | Operation | Unit | Iterations | Items/run | Samples | Wall median [CI95] ms | items/s | p50 | p95 | p99 | CPU cores | Peak RSS MiB | Managed Δ MiB | Alloc MiB | Storage R/W MiB | Flushes | Errors/rejections |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            foreach (OperationAggregate operation in scenario.Operations)
            {
                builder.Append("| ").Append(scenario.Scenario).Append(" | ").Append(operation.Operation).Append(" | ")
                    .Append(operation.LatencyUnit).Append(operation.BatchSize > 1 ? $" ×{operation.BatchSize}" : string.Empty)
                    .Append(" | ").Append(operation.IterationCount).Append(" | ").Append(operation.ItemCountPerIteration)
                    .Append(" | ").Append(operation.LatencySampleCount).Append(" | ")
                    .Append(Format(operation.WallMilliseconds.Median)).Append(" [")
                    .Append(Format(operation.WallMilliseconds.ConfidenceLow95)).Append(',')
                    .Append(Format(operation.WallMilliseconds.ConfidenceHigh95)).Append("] | ")
                    .Append(Format(operation.ThroughputPerSecond.Median)).Append(" | ")
                    .Append(Format(operation.P50Milliseconds)).Append(" | ").Append(Format(operation.P95Milliseconds))
                    .Append(" | ").Append(Format(operation.P99Milliseconds)).Append(" | ")
                    .Append(Format(operation.AverageCpuCoreEquivalent.Median)).Append(" | ")
                    .Append(FormatMib(operation.PeakWorkingSetBytes?.Median)).Append(" | ")
                    .Append(FormatMib(operation.ManagedBytesDelta?.Median)).Append(" | ")
                    .Append(FormatMib(operation.AllocatedBytes?.Median)).Append(" | ")
                    .Append(FormatMib(operation.StorageReadBytes?.Median)).Append('/')
                    .Append(FormatMib(operation.StorageWrittenBytes?.Median)).Append(" | ")
                    .Append(Format(operation.StorageDurableFlushes?.Median)).Append(" | ")
                    .Append(operation.ErrorCount).Append('/').Append(operation.ExpectedRejectionCount).AppendLine(" |");
            }
        }

        BenchmarkScenarioAggregate[] failures = run.Results.Where(static result => result.Failure is not null).ToArray();
        if (failures.Length > 0)
        {
            builder.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (BenchmarkScenarioAggregate failure in failures)
            {
                builder.Append("- `").Append(failure.Scenario).Append("`: ").AppendLine(failure.Failure);
            }
        }

        builder.AppendLine().AppendLine("Raw measured data is available in `benchmark-results.json`, `benchmark-results.csv`, `benchmark-operations.csv`, and `benchmark-resource-samples.csv`.");
        return builder.ToString();
    }

    private static string ScenarioCsv(BenchmarkRunV5 run)
    {
        StringBuilder builder = new("scenario,indexKind,quantization,searchTuning,storageMode,iterations,recallMedian,selectP95Ms,selectThroughputMedian,successCount,errorCount,expectedRejectionCount,queueRejections,congestionRejections,rateLimitRejections,failure\n");
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            OperationAggregate? select = SelectOperation(scenario);
            builder.Append(Csv(scenario.Scenario)).Append(',').Append(Csv(scenario.IndexKind)).Append(',')
                .Append(Csv(scenario.Quantization)).Append(',').Append(scenario.SearchTuning?.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(scenario.StorageMode).Append(',').Append(scenario.Iterations.Count).Append(',')
                .Append(Raw(scenario.RecallAtK.Median)).Append(',').Append(Raw(select?.P95Milliseconds)).Append(',')
                .Append(Raw(select?.ThroughputPerSecond.Median)).Append(',').Append(scenario.SuccessCount).Append(',')
                .Append(scenario.ErrorCount).Append(',').Append(scenario.ExpectedRejectionCount).Append(',')
                .Append(scenario.QueueSaturationRejections).Append(',').Append(scenario.CongestionRejections).Append(',')
                .Append(scenario.ContractualRateLimitRejections).Append(',').Append(Csv(scenario.Failure ?? string.Empty)).AppendLine();
        }

        return builder.ToString();
    }

    private static string OperationCsv(BenchmarkRunV5 run)
    {
        StringBuilder builder = new("scenario,operation,latencyUnit,batchSize,iterationCount,itemCountPerIteration,latencySampleCount,wallMedianMs,wallCiLowMs,wallCiHighMs,throughputMedian,p50Ms,p95Ms,p99Ms,cpuSecondsMedian,cpuCoreEquivalentMedian,normalizedCpuMedian,peakWorkingSetMedian,managedDeltaMedian,allocatedMedian,gcPauseMedianMs,artifactSizeDeltaMedian,storageReadMedian,storageWrittenMedian,durableFlushesMedian,errorCount,expectedRejectionCount\n");
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            foreach (OperationAggregate operation in scenario.Operations)
            {
                builder.Append(Csv(scenario.Scenario)).Append(',').Append(Csv(operation.Operation)).Append(',')
                    .Append(Csv(operation.LatencyUnit)).Append(',').Append(operation.BatchSize).Append(',')
                    .Append(operation.IterationCount).Append(',').Append(operation.ItemCountPerIteration).Append(',')
                    .Append(operation.LatencySampleCount).Append(',').Append(Raw(operation.WallMilliseconds.Median)).Append(',')
                    .Append(Raw(operation.WallMilliseconds.ConfidenceLow95)).Append(',')
                    .Append(Raw(operation.WallMilliseconds.ConfidenceHigh95)).Append(',')
                    .Append(Raw(operation.ThroughputPerSecond.Median)).Append(',').Append(Raw(operation.P50Milliseconds)).Append(',')
                    .Append(Raw(operation.P95Milliseconds)).Append(',').Append(Raw(operation.P99Milliseconds)).Append(',')
                    .Append(Raw(operation.CpuSeconds.Median)).Append(',').Append(Raw(operation.AverageCpuCoreEquivalent.Median)).Append(',')
                    .Append(Raw(operation.NormalizedCpuUtilization.Median)).Append(',').Append(Raw(operation.PeakWorkingSetBytes?.Median)).Append(',')
                    .Append(Raw(operation.ManagedBytesDelta?.Median)).Append(',').Append(Raw(operation.AllocatedBytes?.Median)).Append(',')
                    .Append(Raw(operation.GcPauseMilliseconds?.Median)).Append(',').Append(Raw(operation.ArtifactSizeDeltaBytes.Median)).Append(',')
                    .Append(Raw(operation.StorageReadBytes?.Median)).Append(',').Append(Raw(operation.StorageWrittenBytes?.Median)).Append(',')
                    .Append(Raw(operation.StorageDurableFlushes?.Median)).Append(',').Append(operation.ErrorCount).Append(',')
                    .Append(operation.ExpectedRejectionCount).AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string ResourceCsv(BenchmarkRunV5 run)
    {
        StringBuilder builder = new("scenario,operation,iteration,processId,elapsedMs,cpuCoreEquivalent,normalizedCpuUtilization,workingSetBytes,privateBytes\n");
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            foreach (OperationAggregate operation in scenario.Operations)
            {
                for (int iterationIndex = 0; iterationIndex < operation.Iterations.Count; iterationIndex++)
                {
                    OperationIterationResult iteration = operation.Iterations[iterationIndex];
                    foreach (ResourceSample sample in iteration.ResourceSamples)
                    {
                        BenchmarkIterationResult? scenarioIteration = scenario.Iterations.ElementAtOrDefault(iterationIndex);
                        int processId = operation.Operation == "ProcessColdLoad"
                            ? scenarioIteration?.ColdLoadProcessId ?? 0
                            : scenarioIteration?.ProcessId ?? 0;
                        builder.Append(Csv(scenario.Scenario)).Append(',').Append(Csv(operation.Operation)).Append(',')
                            .Append(iterationIndex).Append(',').Append(processId).Append(',')
                            .Append(Raw(sample.ElapsedMilliseconds)).Append(',').Append(Raw(sample.CpuCoreEquivalent)).Append(',')
                            .Append(Raw(sample.NormalizedCpuUtilization)).Append(',').Append(sample.WorkingSetBytes).Append(',')
                            .Append(sample.PrivateBytes).AppendLine();
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static string Html(BenchmarkRunV5 run, double[] recallThresholds)
    {
        string markdownSummary = WebUtility.HtmlEncode(
            $"{run.Environment.Profile}: {run.Environment.Repetitions} measured forks, baseline {run.BaselineStatus}, " +
            $"unexpected errors {run.Results.Sum(static result => result.ErrorCount)}, expected rejections {run.Results.Sum(static result => result.ExpectedRejectionCount)}.");
        StringBuilder rows = new();
        foreach (BenchmarkScenarioAggregate scenario in run.Results)
        {
            foreach (OperationAggregate operation in scenario.Operations)
            {
                rows.Append("<tr><td>").Append(WebUtility.HtmlEncode(scenario.Scenario)).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(operation.Operation)).Append("</td><td>")
                    .Append(operation.IterationCount).Append("</td><td>").Append(Format(operation.WallMilliseconds.Median))
                    .Append("</td><td>").Append(Format(operation.P95Milliseconds)).Append("</td><td>")
                    .Append(Format(operation.ThroughputPerSecond.Median)).Append("</td><td>")
                    .Append(FormatMib(operation.PeakWorkingSetBytes?.Median)).Append("</td><td class=\"bad\">")
                    .Append(operation.ErrorCount).Append("</td><td class=\"good\">").Append(operation.ExpectedRejectionCount)
                    .Append("</td><td>").Append(Sparkline(operation)).AppendLine("</td></tr>");
            }
        }

        string recommendations = string.Join("", recallThresholds.Select(threshold =>
        {
            BenchmarkScenarioAggregate? result = Recommendation(run.Results, threshold);
            return $"<li>Recall ≥ {threshold:F2}: {WebUtility.HtmlEncode(result?.Scenario ?? "no eligible configuration")}</li>";
        }));
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>SlimVector benchmark v5</title>" +
            "<style>body{font:14px system-ui;margin:24px;color:#17202a}h1,h2{color:#183153}.cards{display:flex;gap:12px;flex-wrap:wrap}.card{background:#eef4ff;padding:12px;border-radius:8px}input{padding:8px;width:min(420px,90%)}table{border-collapse:collapse;width:100%;margin-top:12px}th,td{padding:7px;border-bottom:1px solid #ddd;text-align:right}th:first-child,td:first-child,th:nth-child(2),td:nth-child(2){text-align:left}th{cursor:pointer;background:#f5f7fa;position:sticky;top:0}.bad{color:#a00}.good{color:#075}.chart{overflow:auto}svg{background:#fafafa}</style></head><body>" +
            "<h1>SlimVector reliable benchmark</h1><div class=\"cards\"><div class=\"card\">" + markdownSummary +
            "</div><div class=\"card\"><strong>Recall-qualified choices</strong><ul>" + recommendations + "</ul></div></div>" +
            "<h2>Recall / latency frontier</h2><div class=\"chart\">" + RecallChart(run.Results) + "</div>" +
            "<h2>Operations</h2><input id=\"filter\" placeholder=\"Filter scenario or operation\"><table id=\"ops\"><thead><tr><th>Scenario</th><th>Operation</th><th>Forks</th><th>Wall median ms</th><th>p95 ms</th><th>items/s</th><th>Peak RSS MiB</th><th>Errors</th><th>Expected rejects</th><th>CPU / RAM samples</th></tr></thead><tbody>" +
            rows + "</tbody></table><script>const f=document.getElementById('filter'),t=document.getElementById('ops');f.oninput=()=>{const q=f.value.toLowerCase();for(const r of t.tBodies[0].rows)r.hidden=!r.innerText.toLowerCase().includes(q)};for(const [i,h] of [...t.tHead.rows[0].cells].entries())h.onclick=()=>{const b=t.tBodies[0],rs=[...b.rows],n=rs.every(r=>!isNaN(parseFloat(r.cells[i].innerText)));rs.sort((a,c)=>n?parseFloat(a.cells[i].innerText)-parseFloat(c.cells[i].innerText):a.cells[i].innerText.localeCompare(c.cells[i].innerText));rs.forEach(r=>b.appendChild(r))};</script></body></html>";
    }

    private static string RecallChart(IReadOnlyList<BenchmarkScenarioAggregate> results)
    {
        BenchmarkScenarioAggregate[] points = LocalIndexResults(results)
            .Where(static result => SelectOperation(result)?.P95Milliseconds is not null).ToArray();
        if (points.Length == 0)
        {
            return "<p>No qualified latency samples.</p>";
        }

        double maxLatency = points.Max(static result => SelectOperation(result)!.P95Milliseconds!.Value);
        StringBuilder svg = new("<svg viewBox=\"0 0 700 330\" width=\"700\" height=\"330\" role=\"img\"><path d=\"M50 20V290H680\" stroke=\"#777\" fill=\"none\"/><text x=\"300\" y=\"320\">Recall@k</text><text x=\"5\" y=\"15\">p95 ms</text>");
        foreach (BenchmarkScenarioAggregate point in points)
        {
            double latency = SelectOperation(point)!.P95Milliseconds!.Value;
            double x = 50 + point.RecallAtK.Median * 630;
            double y = 290 - Math.Log10(1 + latency) / Math.Log10(1 + maxLatency) * 260;
            svg.Append("<circle cx=\"").Append(Raw(x)).Append("\" cy=\"").Append(Raw(y))
                .Append("\" r=\"5\"><title>").Append(WebUtility.HtmlEncode(point.Scenario)).Append(": recall ")
                .Append(Format(point.RecallAtK.Median)).Append(", p95 ").Append(Format(latency)).Append(" ms</title></circle>");
        }

        return svg.Append("</svg>").ToString();
    }

    private static string Sparkline(OperationAggregate operation)
    {
        ResourceSample[] samples = operation.Iterations.SelectMany(static iteration => iteration.ResourceSamples).Take(120).ToArray();
        if (samples.Length < 2)
        {
            return "n/a";
        }

        double maxCpu = Math.Max(0.001, samples.Max(static sample => sample.NormalizedCpuUtilization));
        long minRam = samples.Min(static sample => sample.WorkingSetBytes);
        long maxRam = Math.Max(minRam + 1, samples.Max(static sample => sample.WorkingSetBytes));
        string cpu = string.Join(' ', samples.Select((sample, index) =>
            $"{index * 158D / (samples.Length - 1):F1},{30 - sample.NormalizedCpuUtilization / maxCpu * 28:F1}"));
        string ram = string.Join(' ', samples.Select((sample, index) =>
            $"{index * 158D / (samples.Length - 1):F1},{30 - (sample.WorkingSetBytes - minRam) / (double)(maxRam - minRam) * 28:F1}"));
        return $"<svg viewBox=\"0 0 160 32\" width=\"160\" height=\"32\"><polyline points=\"{cpu}\" fill=\"none\" stroke=\"#d43\"/><polyline points=\"{ram}\" fill=\"none\" stroke=\"#36c\"/></svg>";
    }

    private static BenchmarkScenarioAggregate? Recommendation(
        IReadOnlyList<BenchmarkScenarioAggregate> results,
        double threshold) => LocalIndexResults(results)
        .Where(result => result.RecallAtK.Median >= threshold && result.Failure is null)
        .OrderBy(static result => SelectOperation(result)?.P95Milliseconds ?? double.PositiveInfinity)
        .FirstOrDefault();

    private static IEnumerable<BenchmarkScenarioAggregate> LocalIndexResults(
        IReadOnlyList<BenchmarkScenarioAggregate> results) => results.Where(static result =>
            !result.Scenario.StartsWith("Server-", StringComparison.Ordinal) &&
            !result.Scenario.StartsWith("AutoMigration", StringComparison.Ordinal) &&
            !result.Scenario.StartsWith("Raft-", StringComparison.Ordinal));

    private static OperationAggregate? SelectOperation(BenchmarkScenarioAggregate result) => result.Operations.FirstOrDefault(static operation =>
        operation.Operation is "SelectVector" or "HttpSelectMixed");

    private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";

    private static string Raw(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatMib(double? value) => value.HasValue
        ? (value.Value / 1_048_576D).ToString("F2", CultureInfo.InvariantCulture)
        : "n/a";

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\n', '\r']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
