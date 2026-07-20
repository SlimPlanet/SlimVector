using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlimVector.Domain;
using SlimVector.Indexing;

namespace SlimVector.Benchmarks;

internal static class ReliableBenchmarkRunner
{
    private const int Seed = 20260719;
    private static readonly int[] HnswSearchValues = [32, 64, 128, 256];
    private static readonly int[] IvfProbeValues = [1, 2, 4, 8, 16, 32];
    private static readonly int[] DiskAnnSearchValues = [32, 64, 128, 256];

    public static async Task<int> RunAsync(string[] args)
    {
        string? workerJob = GetArgument(args, "--worker-job");
        if (workerJob is not null)
        {
            string workerResult = GetArgument(args, "--worker-result") ??
                throw new ArgumentException("--worker-result is required with --worker-job.", nameof(args));
            return await ReliableBenchmarkWorker.RunAsync(workerJob, workerResult).ConfigureAwait(false);
        }

        Stopwatch total = Stopwatch.StartNew();
        ReliableBenchmarkProfile profile = ParseProfile(GetArgument(args, "--profile") ?? "Smoke");
        profile = profile with
        {
            Repetitions = ParsePositiveInt(args, "--repetitions", profile.Repetitions),
            Warmups = ParseNonNegativeInt(args, "--warmups", profile.Warmups),
            OperationCount = ParsePositiveInt(args, "--operation-count", profile.OperationCount),
            SaturationWarmupSeconds = ParseNonNegativeInt(
                args,
                "--saturation-warmup-seconds",
                profile.SaturationWarmupSeconds),
            SaturationStageSeconds = ParsePositiveInt(
                args,
                "--saturation-stage-seconds",
                profile.SaturationStageSeconds),
            SaturationRatesPerSecond = ParsePositiveIntList(
                GetArgument(args, "--saturation-rates"),
                profile.SaturationRatesPerSecond,
                "--saturation-rates"),
        };
        string datasetKind = GetArgument(args, "--dataset") ?? "synthetic";
        BenchmarkDatasetSpecification dataset = ReliableBenchmarkDatasetFactory.Specification(
            datasetKind,
            GetArgument(args, "--vectors-path"),
            GetArgument(args, "--queries-path"),
            GetArgument(args, "--truth-path"));
        double[] recallThresholds = ParseRecallThresholds(GetArgument(args, "--recall-thresholds") ?? "0.90,0.95,0.99");
        string? requestedIndexes = GetArgument(args, "--indexes");
        if (requestedIndexes is null && string.Equals(profile.Name, "Saturation", StringComparison.Ordinal))
        {
            requestedIndexes = "Flat-Float32";
        }

        ReliableIndexScenario[] scenarios = FilterScenarios(CreateScenarios(profile), requestedIndexes);
        BenchmarkStorageMode[] storageModes = ParseStorageModes(GetArgument(args, "--storage-mode") ??
            (profile.Name == "Large" ? "durable" : "both"));
        BenchmarkWireFormat[] wireFormats = ParseWireFormats(GetArgument(args, "--wire-format") ?? "json");
        string outputRoot = Path.GetFullPath(GetArgument(args, "--output") ?? "artifacts/benchmarks");
        Directory.CreateDirectory(outputRoot);
        BenchmarkEnvironmentV5 environment = CaptureEnvironment(profile, dataset, scenarios, storageModes, recallThresholds, outputRoot);
        string runDirectory = Path.Combine(
            outputRoot,
            $"{Sanitize(environment.Version)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{profile.Name.ToLowerInvariant()}-v5");
        string workspace = Path.Combine(runDirectory, "workers");
        Directory.CreateDirectory(workspace);
        List<BenchmarkIterationResult> iterations = [];
        try
        {
            bool saturationProfile = string.Equals(profile.Name, "Saturation", StringComparison.Ordinal);
            List<BenchmarkJobTemplate> firstStage = saturationProfile
                ? []
                : scenarios.Select(scenario => new BenchmarkJobTemplate(BenchmarkJobKind.Index, scenario, null)).ToList();
            if (!saturationProfile && !HasFlag(args, "--skip-migration"))
            {
                firstStage.Add(new BenchmarkJobTemplate(BenchmarkJobKind.Migration, null, null));
            }

            if (!saturationProfile && !HasFlag(args, "--skip-raft"))
            {
                firstStage.Add(new BenchmarkJobTemplate(BenchmarkJobKind.Raft, null, null));
            }

            await RunStageAsync(firstStage, profile, dataset, workspace, iterations).ConfigureAwait(false);
            BenchmarkScenarioAggregate[] localAggregates = Aggregate(iterations).ToArray();
            if (!HasFlag(args, "--skip-server"))
            {
                ReliableIndexScenario[] serverScenarios = saturationProfile
                    ? FilterScenarios(scenarios, GetArgument(args, "--server-indexes"))
                    : SelectServerScenarios(
                        scenarios,
                        localAggregates,
                        recallThresholds[0],
                        GetArgument(args, "--server-indexes"));
                List<BenchmarkJobTemplate> serverStage = [];
                foreach (ReliableIndexScenario scenario in serverScenarios)
                {
                    foreach (BenchmarkStorageMode storageMode in storageModes)
                    {
                        foreach (BenchmarkWireFormat wireFormat in wireFormats)
                        {
                            serverStage.Add(new BenchmarkJobTemplate(
                                saturationProfile ? BenchmarkJobKind.Saturation : BenchmarkJobKind.ServerCrud,
                                scenario,
                                storageMode,
                                wireFormat));
                        }
                    }
                }

                foreach (BenchmarkStorageMode storageMode in saturationProfile ? [] : storageModes)
                {
                    serverStage.Add(new BenchmarkJobTemplate(
                        BenchmarkJobKind.ServerControl,
                        new ReliableIndexScenario
                        {
                            Name = "Flat-Float32",
                            Kind = VectorIndexKind.Flat,
                            Quantization = VectorQuantizationKind.Float32,
                        },
                        storageMode,
                        BenchmarkWireFormat.Json));
                }

                await RunStageAsync(serverStage, profile, dataset, workspace, iterations).ConfigureAwait(false);
            }

            BenchmarkScenarioAggregate[] results = Aggregate(iterations).ToArray();
            environment = FinalizeEnvironmentFingerprint(environment, results);
            string? baselinePath = GetArgument(args, "--baseline");
            (BenchmarkRunV5? baseline, string baselineStatus) = LoadBaseline(baselinePath, environment.BenchmarkFingerprint);
            bool regression = baseline is not null && HasRegression(results, baseline.Results);
            total.Stop();
            BenchmarkRunV5 run = new()
            {
                Environment = environment,
                Results = results,
                Baseline = baseline,
                BaselineStatus = baselineStatus,
                HasSignificantRegression = regression,
                DurationSeconds = total.Elapsed.TotalSeconds,
            };
            ReliableBenchmarkReportWriter.Write(runDirectory, outputRoot, run, recallThresholds);
            Console.WriteLine(Path.Combine(runDirectory, "benchmark-summary.md"));
            bool failure = results.Any(static result => result.Failure is not null || result.ErrorCount > 0);
            bool failOnRegression = HasFlag(args, "--fail-on-regression");
            return failure || (failOnRegression && (regression || baselinePath is not null && baseline is null)) ? 1 : 0;
        }
        finally
        {
            bool hasFailureLogs = iterations.Any(static result => result.Failure is not null);
            if (!hasFailureLogs && Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    internal static IEnumerable<BenchmarkScenarioAggregate> Aggregate(IEnumerable<BenchmarkIterationResult> source)
    {
        return source.GroupBy(static result => new
        {
            result.Scenario,
            result.IndexKind,
            result.Quantization,
            result.SearchTuning,
            result.StorageMode,
        }).Select((group, groupIndex) =>
        {
            BenchmarkIterationResult[] values = group.OrderBy(static result => result.Iteration).ToArray();
            OperationAggregate[] operations = values.SelectMany(static result => result.Operations)
                .GroupBy(static operation => new { operation.Operation, operation.LatencyUnit, operation.BatchSize })
                .Select((operationGroup, operationIndex) => AggregateOperation(
                    operationGroup.ToArray(),
                    Seed + groupIndex * 101 + operationIndex))
                .OrderBy(static operation => operation.Operation, StringComparer.Ordinal)
                .ToArray();
            return new BenchmarkScenarioAggregate
            {
                Scenario = group.Key.Scenario,
                IndexKind = group.Key.IndexKind,
                Quantization = group.Key.Quantization,
                SearchTuning = group.Key.SearchTuning,
                StorageMode = group.Key.StorageMode,
                RecallAtK = ReliableBenchmarkStatistics.Distribution(
                    values.Select(static result => result.RecallAtK),
                    Seed + groupIndex,
                    "ratio"),
                SuccessCount = values.Sum(static result => result.SuccessCount),
                ErrorCount = values.Sum(static result => result.ErrorCount),
                ExpectedRejectionCount = values.Sum(static result => result.ExpectedRejectionCount),
                QueueSaturationRejections = values.Sum(static result => result.QueueSaturationRejections),
                CongestionRejections = values.Sum(static result => result.CongestionRejections),
                ContractualRateLimitRejections = values.Sum(static result => result.ContractualRateLimitRejections),
                MaxSustainableQps = values.Max(static result => result.MaxSustainableQps),
                Operations = operations,
                Iterations = values,
                Failure = string.Join(" | ", values.Select(static result => result.Failure)
                    .Where(static failure => !string.IsNullOrWhiteSpace(failure)).Distinct(StringComparer.Ordinal)) is { Length: > 0 } failure
                    ? failure
                    : null,
            };
        }).OrderBy(static result => result.Scenario, StringComparer.Ordinal);
    }

    private static OperationAggregate AggregateOperation(OperationIterationResult[] values, int seed)
    {
        double[] latencies = values.SelectMany(static value => value.LatencySamplesMilliseconds).ToArray();
        return new OperationAggregate
        {
            Operation = values[0].Operation,
            LatencyUnit = values[0].LatencyUnit,
            BatchSize = values[0].BatchSize,
            IterationCount = values.Length,
            ItemCountPerIteration = (int)Math.Round(values.Average(static value => value.ItemCount)),
            LatencySampleCount = latencies.Length,
            WallMilliseconds = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => value.WallMilliseconds),
                seed,
                "milliseconds"),
            ThroughputPerSecond = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => value.ThroughputPerSecond),
                seed + 1,
                "items/second"),
            P50Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencies, 0.50),
            P95Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencies, 0.95),
            P99Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencies, 0.99),
            CpuSeconds = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => value.CpuSeconds),
                seed + 2,
                "seconds"),
            AverageCpuCoreEquivalent = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => value.AverageCpuCoreEquivalent), seed + 3, "cpu-cores"),
            NormalizedCpuUtilization = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => value.NormalizedCpuUtilization), seed + 4, "ratio"),
            PeakWorkingSetBytes = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.PeakWorkingSetBytes is long metric ? (double?)metric : null), seed + 5, "bytes"),
            ManagedBytesDelta = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.ManagedBytesDelta is long metric ? (double?)metric : null), seed + 6, "bytes"),
            AllocatedBytes = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.AllocatedBytes is long metric ? (double?)metric : null), seed + 7, "bytes"),
            GcPauseMilliseconds = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.GcPauseMilliseconds), seed + 8, "milliseconds"),
            ArtifactSizeDeltaBytes = ReliableBenchmarkStatistics.Distribution(
                values.Select(static value => (double)value.ArtifactSizeDeltaBytes), seed + 9, "bytes"),
            StorageReadBytes = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.StorageReadBytes is long metric ? (double?)metric : null), seed + 10, "bytes"),
            StorageWrittenBytes = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.StorageWrittenBytes is long metric ? (double?)metric : null), seed + 11, "bytes"),
            StorageDurableFlushes = ReliableBenchmarkStatistics.NullableDistribution(
                values.Select(static value => value.StorageDurableFlushes is long metric ? (double?)metric : null), seed + 12, "flushes"),
            ErrorCount = values.Sum(static value => value.ErrorCount),
            ExpectedRejectionCount = values.Sum(static value => value.ExpectedRejectionCount),
            QueueSaturationRejections = values.Sum(static value => value.QueueSaturationRejections),
            CongestionRejections = values.Sum(static value => value.CongestionRejections),
            ContractualRateLimitRejections = values.Sum(static value => value.ContractualRateLimitRejections),
            OfferedCountPerIteration = (int)Math.Round(values.Average(static value => value.OfferedCount)),
            CompletedCountPerIteration = (int)Math.Round(values.Average(static value => value.CompletedCount)),
            OfferedRatePerSecond = values.Select(static value => value.OfferedRatePerSecond)
                .FirstOrDefault(static value => value.HasValue),
            MeetsSaturationSlo = values.Any(static value => value.MeetsSaturationSlo.HasValue)
                ? values.All(static value => value.MeetsSaturationSlo == true)
                : null,
            CoordinatedOmissionCorrected = values.Any(static value => value.CoordinatedOmissionCorrected),
            Iterations = values,
        };
    }

    private static async Task RunStageAsync(
        IReadOnlyList<BenchmarkJobTemplate> templates,
        ReliableBenchmarkProfile profile,
        BenchmarkDatasetSpecification dataset,
        string workspace,
        List<BenchmarkIterationResult> results)
    {
        for (int iteration = -profile.Warmups; iteration < profile.Repetitions; iteration++)
        {
            bool warmup = iteration < 0;
            BenchmarkJobTemplate[] ordered = templates.ToArray();
            Shuffle(ordered, new Random(Seed + iteration + profile.Warmups));
            foreach ((BenchmarkJobTemplate template, int ordinal) in ordered.Select((template, ordinal) => (template, ordinal)))
            {
                string scenarioName = template.Scenario?.Name ?? template.Kind.ToString();
                string workerPath = Path.Combine(
                    workspace,
                    $"{(warmup ? "warmup" : "iteration")}-{Math.Abs(iteration)}-{ordinal}-{Sanitize(scenarioName)}-{template.StorageMode}-{template.WireFormat}");
                BenchmarkWorkerJob job = new()
                {
                    Kind = template.Kind,
                    Profile = profile,
                    Scenario = template.Scenario,
                    Dataset = dataset,
                    StorageMode = template.StorageMode ?? BenchmarkStorageMode.Buffered,
                    WireFormat = template.WireFormat,
                    Iteration = iteration,
                    Warmup = warmup,
                    Workspace = workerPath,
                };
                try
                {
                    BenchmarkWorkerEnvelope envelope = await ReliableBenchmarkProcess.RunWorkerAsync(job).ConfigureAwait(false);
                    if (!warmup)
                    {
                        results.Add(envelope.Result with
                        {
                            ProcessId = envelope.ProcessId,
                            ColdLoadProcessId = envelope.ColdLoadProcessId,
                        });
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    if (!warmup)
                    {
                        results.Add(CoordinatorFailure(template, iteration, exception));
                    }
                }
            }
        }
    }

    private static BenchmarkIterationResult CoordinatorFailure(
        BenchmarkJobTemplate template,
        int iteration,
        Exception exception)
    {
        string scenario = template.Kind switch
        {
            BenchmarkJobKind.ServerCrud => $"Server-{template.Scenario!.Name}-{template.StorageMode}{WireFormatSuffix(template.WireFormat)}",
            BenchmarkJobKind.ServerControl => $"Server-Control-{template.StorageMode}",
            BenchmarkJobKind.Saturation => $"Server-Saturation-{template.Scenario!.Name}-{template.StorageMode}{WireFormatSuffix(template.WireFormat)}",
            BenchmarkJobKind.Migration => "AutoMigration-FlatToHnsw",
            BenchmarkJobKind.Raft => "Raft-Add-CatchUp",
            _ => template.Scenario?.Name ?? template.Kind.ToString(),
        };
        return new BenchmarkIterationResult
        {
            Scenario = scenario,
            IndexKind = template.Scenario?.Kind.ToString() ?? template.Kind.ToString(),
            Quantization = template.Scenario?.Quantization.ToString() ?? "n/a",
            SearchTuning = template.Scenario?.SearchTuning,
            StorageMode = template.StorageMode,
            Iteration = iteration,
            ErrorCount = 1,
            Failure = exception.GetType().Name + ": " + exception.Message,
        };
    }

    private static ReliableIndexScenario[] SelectServerScenarios(
        ReliableIndexScenario[] scenarios,
        IReadOnlyList<BenchmarkScenarioAggregate> aggregates,
        double recallThreshold,
        string? filter)
    {
        IEnumerable<IGrouping<VectorIndexKind, ReliableIndexScenario>> families =
            scenarios.GroupBy(static scenario => scenario.Kind);
        List<ReliableIndexScenario> selected = [];
        foreach (IGrouping<VectorIndexKind, ReliableIndexScenario> family in families)
        {
            ReliableIndexScenario[] candidates = family.ToArray();
            ReliableIndexScenario? best = candidates.Select(candidate => new
            {
                Scenario = candidate,
                Result = aggregates.FirstOrDefault(result =>
                    string.Equals(result.Scenario, candidate.Name, StringComparison.Ordinal) &&
                    result.SearchTuning == candidate.SearchTuning),
            })
                .Where(static item => item.Result is not null && item.Result.Failure is null)
                .Where(item => item.Result!.RecallAtK.Median >= recallThreshold)
                .OrderBy(item => item.Result!.Operations.FirstOrDefault(static operation => operation.Operation == "SelectVector")
                    ?.P95Milliseconds ?? double.PositiveInfinity)
                .Select(static item => item.Scenario)
                .FirstOrDefault();
            best ??= candidates.OrderByDescending(candidate => aggregates.FirstOrDefault(result =>
                    string.Equals(result.Scenario, candidate.Name, StringComparison.Ordinal))?.RecallAtK.Median ?? 0)
                .First();
            selected.Add(best);
        }

        return FilterScenarios(selected, filter);
    }

    internal static bool HasRegression(
        IReadOnlyList<BenchmarkScenarioAggregate> current,
        IReadOnlyList<BenchmarkScenarioAggregate> baseline)
    {
        foreach (BenchmarkScenarioAggregate scenario in current)
        {
            BenchmarkScenarioAggregate? previous = baseline.FirstOrDefault(candidate =>
                string.Equals(candidate.Scenario, scenario.Scenario, StringComparison.Ordinal) &&
                candidate.SearchTuning == scenario.SearchTuning && candidate.StorageMode == scenario.StorageMode);
            if (previous is null)
            {
                continue;
            }

            foreach (OperationAggregate operation in scenario.Operations)
            {
                OperationAggregate? old = previous.Operations.FirstOrDefault(candidate =>
                    string.Equals(candidate.Operation, operation.Operation, StringComparison.Ordinal));
                if (old is null)
                {
                    continue;
                }

                if (ReliableBenchmarkStatistics.IsRegression(operation.WallMilliseconds, old.WallMilliseconds, 0.10, 5) ||
                    ReliableBenchmarkStatistics.IsRegression(
                        operation.ThroughputPerSecond,
                        old.ThroughputPerSecond,
                        0.10,
                        10,
                        lowerIsBetter: false) ||
                    operation.PeakWorkingSetBytes is not null && old.PeakWorkingSetBytes is not null &&
                    ReliableBenchmarkStatistics.IsRegression(
                        operation.PeakWorkingSetBytes,
                        old.PeakWorkingSetBytes,
                        0.10,
                        1_048_576))
                {
                    return true;
                }

                MetricDistribution? latency = IterationP95(operation);
                MetricDistribution? oldLatency = IterationP95(old);
                if (latency is not null && oldLatency is not null &&
                    ReliableBenchmarkStatistics.IsRegression(latency, oldLatency, 0.10, 0.1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static MetricDistribution? IterationP95(OperationAggregate operation) =>
        ReliableBenchmarkStatistics.NullableDistribution(
            operation.Iterations.Select(static iteration => iteration.P95Milliseconds),
            unit: "milliseconds");

    internal static (BenchmarkRunV5? Run, string Status) LoadBaseline(string? path, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, "not supplied");
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Benchmark baseline '{fullPath}' was not found.", fullPath);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
        int schemaVersion = document.RootElement.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != 5)
        {
            return (null, $"incompatible schema v{schemaVersion}; v5 required");
        }

        BenchmarkRunV5 baseline = JsonSerializer.Deserialize(
            document.RootElement.GetRawText(),
            ReliableBenchmarkJsonContext.Default.BenchmarkRunV5) ?? throw new InvalidDataException("The baseline is empty.");
        return string.Equals(baseline.Environment.BenchmarkFingerprint, fingerprint, StringComparison.Ordinal)
            ? (baseline, "compatible")
            : (null, "incompatible benchmark fingerprint");
    }

    private static BenchmarkEnvironmentV5 CaptureEnvironment(
        ReliableBenchmarkProfile profile,
        BenchmarkDatasetSpecification dataset,
        ReliableIndexScenario[] scenarios,
        BenchmarkStorageMode[] storageModes,
        double[] recallThresholds,
        string outputRoot)
    {
        Assembly assembly = typeof(ReliableBenchmarkRunner).Assembly;
        DriveInfo drive = new(Path.GetPathRoot(outputRoot) ?? outputRoot);
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ?? "unknown";
        string cpu = GetCpuModel();
        string[] matrix = scenarios.Select(static scenario => scenario.Name)
            .Concat(storageModes.Select(static mode => "server:" + mode))
            .ToArray();
        string fingerprintSource = string.Join('|',
            "v5",
            profile.Name,
            dataset.Fingerprint,
            profile.VectorCount,
            profile.Dimension,
            profile.QueryCount,
            profile.TopK,
            profile.Repetitions,
            profile.Warmups,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription,
            cpu,
            Environment.ProcessorCount,
            System.Runtime.GCSettings.IsServerGC,
            string.Join(',', matrix));
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        return new BenchmarkEnvironmentV5
        {
            ProtocolVersion = "reliable-v5.2",
            Version = version,
            Profile = profile.Name,
            StartedAt = DateTimeOffset.UtcNow,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            MachineName = Environment.MachineName,
            CpuModel = cpu,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            Commit = TryRun("git", ["rev-parse", "HEAD"]) ?? "unknown",
            DiskFormat = drive.DriveFormat,
            DatasetFingerprint = dataset.Fingerprint,
            BenchmarkFingerprint = fingerprint,
            ProcessColdLoadCacheState = "uncontrolled",
            VectorCount = profile.VectorCount,
            Dimension = profile.Dimension,
            QueryCount = profile.QueryCount,
            TopK = profile.TopK,
            Repetitions = profile.Repetitions,
            Warmups = profile.Warmups,
            MutationBatchSize = 16,
            PressureProbeRequests = 40,
            DocumentsPerPressureRequest = 20,
            RecallThresholds = recallThresholds.Select(static value => value.ToString("F2", CultureInfo.InvariantCulture)).ToArray(),
            ScenarioMatrix = matrix,
        };
    }

    private static BenchmarkEnvironmentV5 FinalizeEnvironmentFingerprint(
        BenchmarkEnvironmentV5 environment,
        IReadOnlyList<BenchmarkScenarioAggregate> results)
    {
        string[] matrix = results.Select(static result => string.Join(':',
                result.Scenario,
                result.IndexKind,
                result.Quantization,
                result.SearchTuning?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
                result.StorageMode?.ToString() ?? "n/a"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string source = string.Join('|',
            "v5",
            environment.Profile,
            environment.DatasetFingerprint,
            environment.VectorCount,
            environment.Dimension,
            environment.QueryCount,
            environment.TopK,
            environment.Repetitions,
            environment.Warmups,
            environment.ProtocolVersion,
            environment.MutationBatchSize,
            environment.PressureProbeRequests,
            environment.DocumentsPerPressureRequest,
            environment.OperatingSystem,
            environment.Architecture,
            environment.Framework,
            environment.CpuModel,
            environment.ProcessorCount,
            environment.ServerGc,
            environment.ProcessColdLoadCacheState,
            string.Join(',', environment.RecallThresholds),
            string.Join(',', matrix));
        return environment with
        {
            ScenarioMatrix = matrix,
            BenchmarkFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
        };
    }

    internal static ReliableIndexScenario[] CreateScenarios(ReliableBenchmarkProfile profile)
    {
        List<ReliableIndexScenario> scenarios =
        [
            new() { Name = "Flat-Float32", Kind = VectorIndexKind.Flat, Quantization = VectorQuantizationKind.Float32 },
            new() { Name = "Flat-Float16", Kind = VectorIndexKind.Flat, Quantization = VectorQuantizationKind.Float16 },
            new() { Name = "Flat-Int8", Kind = VectorIndexKind.Flat, Quantization = VectorQuantizationKind.Int8 },
        ];
        scenarios.AddRange(HnswSearchValues.Select(value => new ReliableIndexScenario
        {
            Name = "Hnsw-ef" + value.ToString(CultureInfo.InvariantCulture),
            Kind = VectorIndexKind.Hnsw,
            Quantization = VectorQuantizationKind.Float32,
            SearchTuning = value,
        }));
        int trainableLists = Math.Min(
            profile.IvfLists,
            Math.Max(1, profile.VectorCount / IvfFlatIndex.MinimumTrainingPointsPerCentroid));
        int[] probes = IvfProbeValues.Where(value => value <= trainableLists).ToArray();
        scenarios.AddRange(probes.Select(value => new ReliableIndexScenario
        {
            Name = "IvfFlat-nprobe" + value.ToString(CultureInfo.InvariantCulture),
            Kind = VectorIndexKind.IvfFlat,
            Quantization = VectorQuantizationKind.Float32,
            SearchTuning = value,
        }));
        scenarios.AddRange(probes.Select(value => new ReliableIndexScenario
        {
            Name = "IvfPq-nprobe" + value.ToString(CultureInfo.InvariantCulture),
            Kind = VectorIndexKind.IvfPq,
            Quantization = VectorQuantizationKind.Float32,
            SearchTuning = value,
        }));
        scenarios.AddRange(DiskAnnSearchValues.Select(value => new ReliableIndexScenario
        {
            Name = "DiskAnn-search" + value.ToString(CultureInfo.InvariantCulture),
            Kind = VectorIndexKind.DiskAnn,
            Quantization = VectorQuantizationKind.Float32,
            SearchTuning = value,
        }));
        return scenarios.ToArray();
    }

    private static ReliableIndexScenario[] FilterScenarios(IEnumerable<ReliableIndexScenario> scenarios, string? filter)
    {
        ReliableIndexScenario[] values = scenarios.ToArray();
        if (string.IsNullOrWhiteSpace(filter) || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return values;
        }

        string[] requested = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ReliableIndexScenario[] selected = values.Where(scenario => requested.Any(candidate =>
            string.Equals(candidate, scenario.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, scenario.Kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
            scenario.Name.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException($"No benchmark index matched '{filter}'.", nameof(filter));
        }

        return selected;
    }

    internal static ReliableBenchmarkProfile ParseProfile(string value) => value.ToLowerInvariant() switch
    {
        "smoke" => new ReliableBenchmarkProfile("Smoke", 400, 384, 20, 10, 32, 8, 12, 64, 8, 16, 40, 100, 5, 1),
        "standard" => new ReliableBenchmarkProfile("Standard", 25_000, 768, 200, 10, 256, 8, 16, 128, 16, 32, 1_600, 2_000, 5, 1),
        "large" => new ReliableBenchmarkProfile("Large", 250_000, 1_536, 500, 10, 1_024, 16, 16, 200, 32, 48, 5_000, 10_000, 3, 1),
        "saturation" => new ReliableBenchmarkProfile("Saturation", 10_000, 384, 100, 10, 64, 8, 12, 128, 16, 32, 1_000, 10_000, 1, 0),
        _ => throw new ArgumentException("Benchmark profile must be Smoke, Standard, Large, or Saturation.", nameof(value)),
    };

    internal static BenchmarkStorageMode[] ParseStorageModes(string value) => value.ToLowerInvariant() switch
    {
        "buffered" => [BenchmarkStorageMode.Buffered],
        "durable" => [BenchmarkStorageMode.Durable],
        "both" => [BenchmarkStorageMode.Buffered, BenchmarkStorageMode.Durable],
        _ => throw new ArgumentException("--storage-mode must be buffered, durable, or both.", nameof(value)),
    };

    internal static BenchmarkWireFormat[] ParseWireFormats(string value) => value.ToLowerInvariant() switch
    {
        "json" => [BenchmarkWireFormat.Json],
        "messagepack" or "msgpack" => [BenchmarkWireFormat.MessagePack],
        "both" => [BenchmarkWireFormat.Json, BenchmarkWireFormat.MessagePack],
        _ => throw new ArgumentException("--wire-format must be json, messagepack, or both.", nameof(value)),
    };

    internal static double[] ParseRecallThresholds(string value)
    {
        double[] thresholds = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => double.Parse(item, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        if (thresholds.Length == 0 || thresholds.Any(static threshold => threshold is <= 0 or > 1))
        {
            throw new ArgumentException("--recall-thresholds must contain values in (0, 1].", nameof(value));
        }

        return thresholds;
    }

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        int value = ParseNonNegativeInt(args, name, fallback);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name, "The value must be positive.");
    }

    private static int ParseNonNegativeInt(string[] args, string name, int fallback)
    {
        string? argument = GetArgument(args, name);
        return argument is null
            ? fallback
            : int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0
                ? value
                : throw new ArgumentException($"{name} must be a non-negative integer.", nameof(args));
    }

    internal static int[] ParsePositiveIntList(string? value, int[] fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        int[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{name} must contain positive integers.", nameof(value)))
            .Distinct()
            .Order()
            .ToArray();
        return values.Length > 0 ? values : throw new ArgumentException($"{name} may not be empty.", nameof(value));
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (int index = values.Length - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (values[index], values[swap]) = (values[swap], values[index]);
        }
    }

    private static string Sanitize(string value) => new(value.Select(static character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());

    internal static string WireFormatSuffix(BenchmarkWireFormat wireFormat) =>
        wireFormat == BenchmarkWireFormat.MessagePack ? "-MessagePack" : string.Empty;

    private static string GetCpuModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return TryRun("sysctl", ["-n", "machdep.cpu.brand_string"]) ?? "unknown";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/cpuinfo"))
        {
            string? line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(static value =>
                value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            return line?.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "unknown";
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static string? TryRun(string command, IReadOnlyList<string> arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed record BenchmarkJobTemplate(
        BenchmarkJobKind Kind,
        ReliableIndexScenario? Scenario,
        BenchmarkStorageMode? StorageMode,
        BenchmarkWireFormat WireFormat = BenchmarkWireFormat.Json);
}
