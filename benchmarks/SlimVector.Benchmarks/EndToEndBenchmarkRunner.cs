using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Raft;
using SlimVector.Raft.Commands;

namespace SlimVector.Benchmarks;

internal static class EndToEndBenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        BenchmarkProfile profile = ParseProfile(GetArgument(args, "--profile") ?? "Smoke");
        string outputRoot = Path.GetFullPath(GetArgument(args, "--output") ?? "artifacts/benchmarks");
        string? baselinePath = GetArgument(args, "--baseline");
        double regressionThreshold = double.Parse(
            GetArgument(args, "--regression-threshold") ?? "0.10",
            CultureInfo.InvariantCulture);
        if (regressionThreshold is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Regression threshold must be between 0 and 10.");
        }

        Directory.CreateDirectory(outputRoot);
        BenchmarkEnvironment environment = CaptureEnvironment(profile, outputRoot);
        string runDirectory = Path.Combine(
            outputRoot,
            $"{Sanitize(environment.Version)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{profile.Name.ToLowerInvariant()}");
        Directory.CreateDirectory(runDirectory);
        string workspace = Path.Combine(runDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            DocumentRecord[] documents = CreateDocuments(profile);
            float[][] queries = SelectQueries(documents, profile.QueryCount);
            HashSet<string>[] truth = BuildGroundTruth(documents, queries, profile.TopK);
            List<EndToEndBenchmarkResult> results = [];
            foreach (BenchmarkIndexScenario scenario in Scenarios(profile))
            {
                results.Add(RunIndexScenario(scenario, documents, queries, truth, profile, workspace));
                await Task.Yield();
            }

            results.Add(RunMigrationScenario(documents, queries, truth, profile, workspace));
            results.Add(await RunServerScenarioAsync(documents, profile, workspace).ConfigureAwait(false));
            results.Add(await RunRaftCatchUpScenarioAsync(profile, workspace).ConfigureAwait(false));
            BenchmarkRun run = new()
            {
                SchemaVersion = 3,
                Environment = environment,
                Results = results,
                Baseline = LoadBaseline(baselinePath),
                RegressionThreshold = regressionThreshold,
            };
            BenchmarkReportWriter.Write(runDirectory, outputRoot, run);
            Console.WriteLine(Path.Combine(runDirectory, "benchmark-summary.md"));
            return results.Any(static result => result.Failure is not null) ? 1 : 0;
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static EndToEndBenchmarkResult RunIndexScenario(
        BenchmarkIndexScenario scenario,
        DocumentRecord[] documents,
        float[][] queries,
        HashSet<string>[] truth,
        BenchmarkProfile profile,
        string workspace)
    {
        string artifactPath = Path.Combine(workspace, scenario.Name.ToLowerInvariant().Replace('-', '_'));
        Directory.CreateDirectory(artifactPath);
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long workingSetBefore = process.WorkingSet64;
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        long lohBefore = GetLohBytes();
        try
        {
            using ResourceSampler resources = new(process);
            CollectionDefinition definition = Definition(profile, scenario);
            Stopwatch build = Stopwatch.StartNew();
            using CollectionSearchIndex index = new(
                definition,
                scenario.Kind,
                documents,
                persistedVectorIndex: null,
                diskAnnArtifactDirectory: artifactPath);
            build.Stop();
            SearchRequest warmup = new() { Mode = SearchMode.Vector, Vector = queries[0], Limit = profile.TopK };
            _ = index.Search(warmup, 4);
            List<double> latencies = new(queries.Length);
            double recall = 0;
            Stopwatch searchWall = Stopwatch.StartNew();
            for (int queryIndex = 0; queryIndex < queries.Length; queryIndex++)
            {
                SearchRequest request = new() { Mode = SearchMode.Vector, Vector = queries[queryIndex], Limit = profile.TopK };
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<HybridRankedResult> found = index.Search(request, 4);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                recall += found.Count(result => truth[queryIndex].Contains(result.Id)) / (double)profile.TopK;
            }

            searchWall.Stop();
            Stopwatch persist = Stopwatch.StartNew();
            byte[] snapshot = index.Serialize(documents);
            persist.Stop();
            Stopwatch coldLoad = Stopwatch.StartNew();
            using CollectionSearchIndex restored = new(
                definition,
                scenario.Kind,
                documents,
                snapshot,
                diskAnnArtifactDirectory: artifactPath + "-restored");
            coldLoad.Stop();
            long managedBeforeDispose = GC.GetTotalMemory(forceFullCollection: false);
            restored.Dispose();
            index.Dispose();
            long managedAfterDispose = GC.GetTotalMemory(forceFullCollection: true);
            resources.Stop();
            process.Refresh();
            TimeSpan cpuAfter = process.TotalProcessorTime;
            long diskBytes = DirectorySize(artifactPath) + snapshot.LongLength;
            return new EndToEndBenchmarkResult
            {
                Scenario = scenario.Name,
                IndexKind = scenario.Kind.ToString(),
                Quantization = scenario.Quantization.ToString(),
                VectorCount = documents.Length,
                Dimension = profile.Dimension,
                BuildMilliseconds = build.Elapsed.TotalMilliseconds,
                PersistMilliseconds = persist.Elapsed.TotalMilliseconds,
                ColdLoadMilliseconds = coldLoad.Elapsed.TotalMilliseconds,
                ThroughputQueriesPerSecond = queries.Length / searchWall.Elapsed.TotalSeconds,
                P50Milliseconds = Percentile(latencies, 0.50),
                P95Milliseconds = Percentile(latencies, 0.95),
                P99Milliseconds = Percentile(latencies, 0.99),
                RecallAtK = recall / queries.Length,
                CpuSeconds = (cpuAfter - cpuBefore).TotalSeconds,
                CpuUtilization = resources.AverageCpuUtilization,
                PeakCpuUtilization = resources.PeakCpuUtilization,
                ManagedBytesDelta = GC.GetTotalMemory(forceFullCollection: false) - managedBefore,
                WorkingSetBytesDelta = process.WorkingSet64 - workingSetBefore,
                AverageWorkingSetBytes = resources.AverageWorkingSetBytes,
                PeakWorkingSetBytes = resources.PeakWorkingSetBytes,
                ManagedBytesFreedAfterDispose = Math.Max(0, managedBeforeDispose - managedAfterDispose),
                DiskBytes = diskBytes,
                SnapshotBytes = snapshot.LongLength,
                DiskWriteBytesPerSecond = persist.Elapsed.TotalSeconds <= 0
                    ? 0
                    : snapshot.LongLength / persist.Elapsed.TotalSeconds,
                Gen0Collections = GC.CollectionCount(0) - gen0Before,
                Gen1Collections = GC.CollectionCount(1) - gen1Before,
                Gen2Collections = GC.CollectionCount(2) - gen2Before,
                LohBytesDelta = GetLohBytes() - lohBefore,
                GcPauseMilliseconds = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds,
                IdleWorkingSetBytes = workingSetBefore,
                RequestCount = queries.Length,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(scenario, profile, exception);
        }
        finally
        {
            if (Directory.Exists(artifactPath + "-restored"))
            {
                Directory.Delete(artifactPath + "-restored", recursive: true);
            }
        }
    }

    private static EndToEndBenchmarkResult RunMigrationScenario(
        DocumentRecord[] documents,
        float[][] queries,
        HashSet<string>[] truth,
        BenchmarkProfile profile,
        string workspace)
    {
        BenchmarkIndexScenario scenario = new("AutoMigration-FlatToHnsw", VectorIndexKind.Hnsw, VectorQuantizationKind.Float32);
        try
        {
            CollectionDefinition definition = Definition(profile, scenario);
            Stopwatch migration = Stopwatch.StartNew();
            using CollectionSearchIndex candidate = new(
                definition,
                VectorIndexKind.Hnsw,
                documents,
                persistedVectorIndex: null,
                diskAnnArtifactDirectory: Path.Combine(workspace, "migration"));
            migration.Stop();
            double recall = 0;
            for (int index = 0; index < queries.Length; index++)
            {
                SearchRequest request = new() { Mode = SearchMode.Vector, Vector = queries[index], Limit = profile.TopK };
                recall += candidate.Search(request, 4).Count(result => truth[index].Contains(result.Id)) / (double)profile.TopK;
            }

            return new EndToEndBenchmarkResult
            {
                Scenario = scenario.Name,
                IndexKind = VectorIndexKind.Hnsw.ToString(),
                Quantization = VectorQuantizationKind.Float32.ToString(),
                VectorCount = documents.Length,
                Dimension = profile.Dimension,
                BuildMilliseconds = migration.Elapsed.TotalMilliseconds,
                RecallAtK = recall / queries.Length,
                MigrationMilliseconds = migration.Elapsed.TotalMilliseconds,
                RequestCount = queries.Length,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(scenario, profile, exception);
        }
    }

    private static async Task<EndToEndBenchmarkResult> RunRaftCatchUpScenarioAsync(
        BenchmarkProfile profile,
        string workspace)
    {
        const string scenario = "Raft-Add-CatchUp";
        string storageRoot = Path.Combine(workspace, "raft-catch-up");
        Directory.CreateDirectory(storageRoot);
        int commandCount = profile.Name switch
        {
            "Smoke" => 5,
            "Standard" => 1_000,
            _ => 10_000,
        };
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(4);
        BenchmarkCommandApplier[] appliers = [new(), new(), new(), new()];
        RaftGroupNode?[] nodes = new RaftGroupNode?[4];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                nodes[index] = new RaftGroupNode(
                    RaftOptions(endpoints[index], endpoints[..3], storageRoot, index),
                    appliers[index]);
            }

            using CancellationTokenSource timeout = new(profile.Name == "Large"
                ? TimeSpan.FromMinutes(10)
                : TimeSpan.FromMinutes(2));
            await Task.WhenAll(nodes[..3].Select(node => node!.StartAsync(timeout.Token).AsTask())).ConfigureAwait(false);
            EndPoint elected = await nodes[0]!.WaitForLeaderAsync(TimeSpan.FromSeconds(20), timeout.Token).ConfigureAwait(false);
            RaftGroupNode leader = nodes[..3].Single(node => Equals(node!.LocalEndpoint, elected))!;
            for (int index = 0; index < commandCount; index++)
            {
                await leader.ReplicateAsync(
                    RaftCommandCodec.CatalogDelete(
                        DeterministicGuid(index, 0xA0),
                        "catalog",
                        DeterministicGuid(index, 0xB0),
                        "benchmark"),
                    timeout.Token).ConfigureAwait(false);
            }

            await WaitUntilAsync(
                () => appliers[..3].All(applier => applier.Count == commandCount),
                TimeSpan.FromSeconds(30),
                timeout.Token).ConfigureAwait(false);
            nodes[3] = new RaftGroupNode(
                RaftOptions(endpoints[3], [], storageRoot, 3) with { StartAsJoiningMember = true },
                appliers[3]);
            await nodes[3]!.StartAsync(timeout.Token).ConfigureAwait(false);

            Stopwatch catchUp = Stopwatch.StartNew();
            bool added = false;
            for (int attempt = 0; attempt < 3 && !added; attempt++)
            {
                EndPoint currentLeader = await nodes[0]!.WaitForLeaderAsync(TimeSpan.FromSeconds(20), timeout.Token)
                    .ConfigureAwait(false);
                leader = nodes[..3].Single(node => Equals(node!.LocalEndpoint, currentLeader))!;
                await WaitUntilAsync(() => leader.IsLeader, TimeSpan.FromSeconds(20), timeout.Token).ConfigureAwait(false);
                added = await leader.AddMemberAsync(endpoints[3], timeout.Token).ConfigureAwait(false);
                if (!added)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
                }
            }

            catchUp.Stop();
            if (!added)
            {
                throw new InvalidOperationException("DotNext rejected the joining benchmark member.");
            }

            await WaitUntilAsync(
                () => appliers[3].Count == commandCount,
                TimeSpan.FromSeconds(30),
                timeout.Token).ConfigureAwait(false);
            return new EndToEndBenchmarkResult
            {
                Scenario = scenario,
                IndexKind = "Raft",
                Quantization = "n/a",
                VectorCount = commandCount,
                BuildMilliseconds = catchUp.Elapsed.TotalMilliseconds,
                ThroughputQueriesPerSecond = commandCount / catchUp.Elapsed.TotalSeconds,
                RaftCatchUpMilliseconds = catchUp.Elapsed.TotalMilliseconds,
                DiskBytes = DirectorySize(storageRoot),
                RequestCount = commandCount,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new EndToEndBenchmarkResult
            {
                Scenario = scenario,
                IndexKind = "Raft",
                Quantization = "n/a",
                VectorCount = commandCount,
                RequestCount = commandCount,
                ErrorCount = 1,
                ErrorRate = 1,
                Failure = exception.GetType().Name + ": " + exception.Message,
            };
        }
        finally
        {
            foreach (RaftGroupNode? node in nodes)
            {
                if (node is not null)
                {
                    await node.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<EndToEndBenchmarkResult> RunServerScenarioAsync(
        DocumentRecord[] sourceDocuments,
        BenchmarkProfile profile,
        string workspace)
    {
        const string scenario = "Server-HTTP-Mixed-Saturation";
        int documentCount = profile.Name switch
        {
            "Smoke" => 200,
            "Standard" => 2_000,
            _ => 10_000,
        };
        DocumentRecord[] documents = sourceDocuments[..Math.Min(documentCount, sourceDocuments.Length)];
        float[][] queries = SelectQueries(documents, profile.QueryCount);
        HashSet<string>[] truth = BuildGroundTruth(documents, queries, profile.TopK);
        string repositoryRoot = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        string apiDirectory = Path.Combine(repositoryRoot, "src", "SlimVector.Api", "bin", configuration, "net10.0");
        string apiAssembly = Path.Combine(apiDirectory, "SlimVector.Api.dll");
        string storagePath = Path.Combine(workspace, "http-server-storage");
        Directory.CreateDirectory(storagePath);
        int port = AllocateLoopbackEndpoints(1)[0].Port;
        Uri baseAddress = new($"http://127.0.0.1:{port}", UriKind.Absolute);
        using Process process = new()
        {
            StartInfo = CreateServerStartInfo(apiAssembly, apiDirectory, baseAddress, storagePath),
        };
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        bool processStarted = false;
        try
        {
            if (!File.Exists(apiAssembly))
            {
                throw new FileNotFoundException("Build the SlimVector.Api Release project before running E2E benchmarks.", apiAssembly);
            }

            Stopwatch coldStart = Stopwatch.StartNew();
            if (!process.Start())
            {
                throw new InvalidOperationException("The SlimVector API benchmark process could not be started.");
            }

            processStarted = true;
            standardOutput = process.StandardOutput.ReadToEndAsync();
            standardError = process.StandardError.ReadToEndAsync();
            using HttpClient client = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };
            await WaitForServerAsync(client, process, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            coldStart.Stop();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long workingSetBefore = process.WorkingSet64;
            using ResourceSampler resources = new(process);

            ServerCreateCollectionRequest create = new()
            {
                Name = "server-benchmark",
                Dimension = profile.Dimension,
                Metric = DistanceMetric.Cosine,
                VectorIndex = new VectorIndexConfiguration { Kind = VectorIndexKind.Flat },
            };
            using (HttpResponseMessage response = await PostJsonAsync(
                       client,
                       "/api/v1/collections/",
                       create,
                       BenchmarkJsonContext.Default.ServerCreateCollectionRequest).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
            }

            Stopwatch ingestion = Stopwatch.StartNew();
            int ingestionBatch = 0;
            foreach (DocumentRecord[] batch in documents.Chunk(200))
            {
                ServerDocumentBatchRequest request = new()
                {
                    Atomic = true,
                    Documents = batch.Select(static document => new ServerDocumentInput
                    {
                        Id = document.Id,
                        Text = document.Text,
                        Vector = document.Vector,
                    }).ToArray(),
                };
                using HttpResponseMessage response = await PostJsonAsync(
                    client,
                    "/api/v1/collections/server-benchmark/documents/upsert",
                    request,
                    BenchmarkJsonContext.Default.ServerDocumentBatchRequest,
                    $"benchmark-ingest-{ingestionBatch++}").ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
            }

            ingestion.Stop();
            List<double> latencies = new(queries.Length);
            double recall = 0;
            int recallSamples = 0;
            Stopwatch searchWall = Stopwatch.StartNew();
            for (int index = 0; index < queries.Length; index++)
            {
                SearchMode mode = (index % 3) switch
                {
                    0 => SearchMode.Vector,
                    1 => SearchMode.Text,
                    _ => SearchMode.Hybrid,
                };
                ServerQueryRequest request = new()
                {
                    Text = mode == SearchMode.Vector ? null : "vector database benchmark",
                    Vector = mode == SearchMode.Text ? null : queries[index],
                    Mode = mode,
                    Limit = profile.TopK,
                    Include = [],
                };
                long started = Stopwatch.GetTimestamp();
                using HttpResponseMessage response = await PostJsonAsync(
                    client,
                    "/api/v1/collections/server-benchmark/documents/query",
                    request,
                    BenchmarkJsonContext.Default.ServerQueryRequest,
                    $"benchmark-query-{index}").ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                ServerQueryResponse result = await response.Content.ReadFromJsonAsync(
                        BenchmarkJsonContext.Default.ServerQueryResponse)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The HTTP benchmark returned an empty query response.");
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (mode == SearchMode.Vector)
                {
                    recall += result.Hits.Count(hit => truth[index].Contains(hit.Id)) / (double)profile.TopK;
                    recallSamples++;
                }
            }

            searchWall.Stop();
            (int backpressureRejections, int mixedReadSuccesses) = await RunBackpressureProbeAsync(
                client,
                documents,
                queries[0],
                profile.TopK).ConfigureAwait(false);
            (int rateLimitRejections, double retryAfterSeconds) = await RunRateLimitProbeAsync(client, queries[0], profile.TopK)
                .ConfigureAwait(false);
            if (backpressureRejections == 0)
            {
                throw new InvalidOperationException("The real HTTP backpressure probe did not produce a queue_saturated rejection.");
            }

            if (mixedReadSuccesses != 16)
            {
                throw new InvalidOperationException(
                    $"Only {mixedReadSuccesses} of 16 reads succeeded during the mixed read/write pressure probe.");
            }

            if (rateLimitRejections == 0 || retryAfterSeconds <= 0)
            {
                throw new InvalidOperationException("The real HTTP rate-limit probe did not produce a contractual 429 with Retry-After.");
            }

            resources.Stop();
            process.Refresh();
            return new EndToEndBenchmarkResult
            {
                Scenario = scenario,
                IndexKind = VectorIndexKind.Flat.ToString(),
                Quantization = VectorQuantizationKind.Float32.ToString(),
                VectorCount = documents.Length,
                Dimension = profile.Dimension,
                BuildMilliseconds = ingestion.Elapsed.TotalMilliseconds,
                IngestMilliseconds = ingestion.Elapsed.TotalMilliseconds,
                ColdLoadMilliseconds = coldStart.Elapsed.TotalMilliseconds,
                ThroughputQueriesPerSecond = queries.Length / searchWall.Elapsed.TotalSeconds,
                P50Milliseconds = Percentile(latencies, 0.50),
                P95Milliseconds = Percentile(latencies, 0.95),
                P99Milliseconds = Percentile(latencies, 0.99),
                RecallAtK = recall / recallSamples,
                CpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                CpuUtilization = resources.AverageCpuUtilization,
                PeakCpuUtilization = resources.PeakCpuUtilization,
                WorkingSetBytesDelta = process.WorkingSet64 - workingSetBefore,
                AverageWorkingSetBytes = resources.AverageWorkingSetBytes,
                PeakWorkingSetBytes = resources.PeakWorkingSetBytes,
                DiskBytes = DirectorySize(storagePath),
                BackpressureRejections = backpressureRejections,
                RateLimitRejections = rateLimitRejections,
                RateLimitRetryAfterSeconds = retryAfterSeconds,
                IdleWorkingSetBytes = workingSetBefore,
                RequestCount = queries.Length + 64 + 16 + 128,
                ErrorCount = backpressureRejections + rateLimitRejections,
                ErrorRate = (backpressureRejections + rateLimitRejections) / (double)(queries.Length + 64 + 16 + 128),
                MixedReadSuccesses = mixedReadSuccesses,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            string diagnostics = processStarted && process.HasExited && standardError is not null
                ? await standardError.ConfigureAwait(false)
                : string.Empty;
            return new EndToEndBenchmarkResult
            {
                Scenario = scenario,
                IndexKind = VectorIndexKind.Flat.ToString(),
                Quantization = VectorQuantizationKind.Float32.ToString(),
                VectorCount = documents.Length,
                Dimension = profile.Dimension,
                ErrorCount = 1,
                ErrorRate = 1,
                Failure = exception.GetType().Name + ": " + exception.Message +
                    (diagnostics.Length == 0 ? string.Empty : " | server: " + diagnostics.Trim()),
            };
        }
        finally
        {
            if (processStarted && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            if (standardOutput is not null)
            {
                _ = await standardOutput.ConfigureAwait(false);
            }

            if (standardError is not null)
            {
                _ = await standardError.ConfigureAwait(false);
            }
        }
    }

    private static async Task<(int Rejections, int ReadSuccesses)> RunBackpressureProbeAsync(
        HttpClient client,
        DocumentRecord[] documents,
        float[] query,
        int topK)
    {
        Task<bool>[] requests = Enumerable.Range(0, 64).Select(async index =>
        {
            DocumentRecord source = documents[index % documents.Length];
            ServerDocumentBatchRequest request = new()
            {
                Atomic = true,
                Documents = Enumerable.Range(0, 100).Select(documentIndex =>
                    new ServerDocumentInput
                    {
                        Id = $"pressure-{index}-{documentIndex}",
                        Text = source.Text,
                        Vector = source.Vector,
                    }).ToArray(),
            };
            using HttpResponseMessage response = await PostJsonAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/upsert",
                request,
                BenchmarkJsonContext.Default.ServerDocumentBatchRequest,
                $"benchmark-pressure-{index}").ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return false;
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return body.Contains("queue_saturated", StringComparison.Ordinal);
        }).ToArray();
        Task<bool>[] reads = Enumerable.Range(0, 16).Select(async index =>
        {
            ServerQueryRequest request = new()
            {
                Text = "vector database benchmark",
                Vector = query,
                Mode = SearchMode.Hybrid,
                Limit = topK,
                Include = [],
            };
            using HttpResponseMessage response = await PostJsonAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/query",
                request,
                BenchmarkJsonContext.Default.ServerQueryRequest,
                $"benchmark-mixed-read-{index}").ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            ServerQueryResponse result = await response.Content.ReadFromJsonAsync(BenchmarkJsonContext.Default.ServerQueryResponse)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The mixed read/write probe returned an empty response.");
            return result.Hits.Length > 0;
        }).ToArray();
        bool[] rejected = await Task.WhenAll(requests).ConfigureAwait(false);
        bool[] successfulReads = await Task.WhenAll(reads).ConfigureAwait(false);
        return (rejected.Count(static value => value), successfulReads.Count(static value => value));
    }

    private static async Task<(int Rejections, double RetryAfterSeconds)> RunRateLimitProbeAsync(
        HttpClient client,
        float[] query,
        int topK)
    {
        ServerQueryRequest request = new()
        {
            Vector = query,
            Mode = SearchMode.Vector,
            Limit = topK,
            Include = [],
        };
        int rejections = 0;
        double retryAfterSeconds = 0;
        for (int index = 0; index < 128; index++)
        {
            using HttpResponseMessage response = await PostJsonAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/query",
                request,
                BenchmarkJsonContext.Default.ServerQueryRequest,
                "benchmark-rate-probe").ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                response.Headers.TryGetValues("X-SlimVector-RateLimit-Kind", out IEnumerable<string>? kinds) &&
                kinds.Contains("contractual", StringComparer.Ordinal))
            {
                rejections++;
                double responseRetryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ??
                    (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate
                        ? Math.Max(0, (retryDate - DateTimeOffset.UtcNow).TotalSeconds)
                        : 0);
                retryAfterSeconds = Math.Max(retryAfterSeconds, responseRetryAfter);
            }
            else
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
            }
        }

        return (rejections, retryAfterSeconds);
    }

    private static HashSet<string>[] BuildGroundTruth(
        DocumentRecord[] documents,
        float[][] queries,
        int topK)
    {
        FlatVectorIndex exact = new(documents[0].Vector.Length, DistanceMetric.Cosine);
        foreach (DocumentRecord document in documents)
        {
            exact.Upsert(document.Id, document.Vector);
        }

        return queries.Select(query => exact.Search(query, topK).Select(static result => result.Id).ToHashSet(StringComparer.Ordinal)).ToArray();
    }

    private static CollectionDefinition Definition(BenchmarkProfile profile, BenchmarkIndexScenario scenario)
    {
        int listCount = Math.Min(profile.IvfLists, profile.VectorCount);
        int centroidCount = Math.Min(256, profile.VectorCount);
        return CollectionDefinition.Create(
            "benchmark-" + scenario.Name.ToLowerInvariant().Replace('-', '_'),
            profile.Dimension,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration
            {
                Kind = scenario.Kind,
                Quantization = scenario.Quantization,
                RerankCandidateMultiplier = 4,
                HnswM = profile.HnswDegree,
                HnswEfConstruction = profile.HnswConstruction,
                HnswEfSearch = profile.HnswSearch,
                IvfListCount = listCount,
                IvfProbeCount = Math.Min(profile.IvfProbes, listCount),
                IvfTrainingIterations = profile.TrainingIterations,
                PqSubvectorCount = profile.PqSubvectors,
                PqCentroidCount = centroidCount,
                PqTrainingIterations = profile.TrainingIterations,
                DiskAnnMaxDegree = profile.DiskAnnDegree,
                DiskAnnSearchListSize = profile.DiskAnnSearchList,
                DiskAnnBeamWidth = 4,
                DiskAnnDeltaThreshold = Math.Max(100, profile.VectorCount / 10),
            });
    }

    private static DocumentRecord[] CreateDocuments(BenchmarkProfile profile)
    {
        Random random = new(20260719);
        return Enumerable.Range(0, profile.VectorCount).Select(index => new DocumentRecord
        {
            Id = "doc-" + index.ToString(CultureInfo.InvariantCulture),
            Text = index % 5 == 0 ? "vector database benchmark" : "benchmark corpus document",
            Vector = Enumerable.Range(0, profile.Dimension).Select(_ => random.NextSingle() * 2 - 1).ToArray(),
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["bucket"] = MetadataValue.From((long)(index % 10)),
            },
            Version = 1,
        }).ToArray();
    }

    private static float[][] SelectQueries(DocumentRecord[] documents, int count) => Enumerable.Range(0, count)
        .Select(index => documents[(index * 7_919) % documents.Length].Vector)
        .ToArray();

    private static IEnumerable<BenchmarkIndexScenario> Scenarios(BenchmarkProfile profile)
    {
        yield return new BenchmarkIndexScenario("Flat-Float32", VectorIndexKind.Flat, VectorQuantizationKind.Float32);
        yield return new BenchmarkIndexScenario("Flat-Float16", VectorIndexKind.Flat, VectorQuantizationKind.Float16);
        yield return new BenchmarkIndexScenario("Flat-Int8", VectorIndexKind.Flat, VectorQuantizationKind.Int8);
        yield return new BenchmarkIndexScenario("Hnsw", VectorIndexKind.Hnsw, VectorQuantizationKind.Float32);
        yield return new BenchmarkIndexScenario("IvfFlat", VectorIndexKind.IvfFlat, VectorQuantizationKind.Float32);
        yield return new BenchmarkIndexScenario("IvfPq", VectorIndexKind.IvfPq, VectorQuantizationKind.Float32);
        yield return new BenchmarkIndexScenario("DiskAnn", VectorIndexKind.DiskAnn, VectorQuantizationKind.Float32);
    }

    private static BenchmarkProfile ParseProfile(string name) => name.ToLowerInvariant() switch
    {
        "smoke" => new BenchmarkProfile("Smoke", 1_000, 384, 20, 10, 32, 4, 8, 12, 64, 8, 64, 16, 64),
        "standard" => new BenchmarkProfile("Standard", 25_000, 768, 100, 10, 256, 8, 8, 16, 128, 16, 128, 32, 128),
        "large" => new BenchmarkProfile("Large", 250_000, 1_536, 200, 10, 1_024, 16, 16, 16, 200, 32, 256, 48, 192),
        _ => throw new ArgumentException("Benchmark profile must be Smoke, Standard, or Large.", nameof(name)),
    };

    private static BenchmarkEnvironment CaptureEnvironment(BenchmarkProfile profile, string outputRoot)
    {
        Assembly assembly = typeof(EndToEndBenchmarkRunner).Assembly;
        DriveInfo drive = new(Path.GetPathRoot(outputRoot) ?? outputRoot);
        return new BenchmarkEnvironment
        {
            Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                assembly.GetName().Version?.ToString() ?? "unknown",
            Profile = profile.Name,
            StartedAt = DateTimeOffset.UtcNow,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            MachineName = Environment.MachineName,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Commit = TryRun("git", ["rev-parse", "HEAD"], FindRepositoryRoot()) ?? "unknown",
            CpuModel = GetCpuModel(),
            DiskFormat = drive.DriveFormat,
            DiskTotalBytes = drive.TotalSize,
            DiskAvailableBytes = drive.AvailableFreeSpace,
            VectorCount = profile.VectorCount,
            Dimension = profile.Dimension,
            QueryCount = profile.QueryCount,
            TopK = profile.TopK,
            Configuration = new BenchmarkConfiguration
            {
                RandomSeed = 20260719,
                DistanceMetric = DistanceMetric.Cosine.ToString(),
                IndexScenarios = Scenarios(profile).Select(static scenario => scenario.Name).ToArray(),
                IvfLists = profile.IvfLists,
                IvfProbes = profile.IvfProbes,
                PqSubvectors = profile.PqSubvectors,
                TrainingIterations = profile.TrainingIterations,
                HnswConstruction = profile.HnswConstruction,
                HnswDegree = profile.HnswDegree,
                HnswSearch = profile.HnswSearch,
                DiskAnnDegree = profile.DiskAnnDegree,
                DiskAnnSearchList = profile.DiskAnnSearchList,
                ServerDocumentCount = profile.Name switch
                {
                    "Smoke" => 200,
                    "Standard" => 2_000,
                    _ => 10_000,
                },
                ServerSearchModes = [SearchMode.Vector.ToString(), SearchMode.Text.ToString(), SearchMode.Hybrid.ToString()],
                BackpressureQueueCapacity = 1,
                BackpressureProbeRequests = 64,
                DocumentsPerPressureRequest = 100,
                MixedReadRequests = 16,
                ClientTokensPerSecond = 1,
                ClientBurstCapacity = 50,
                RateLimitProbeRequests = 128,
            },
        };
    }

    private static string GetCpuModel()
    {
        if (OperatingSystem.IsMacOS())
        {
            return TryRun("sysctl", ["-n", "machdep.cpu.brand_string"], workingDirectory: null) ?? "unknown";
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? line = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(static value => value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            return line?.Split(':', 2)[^1].Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string? TryRun(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start() || !process.WaitForExit(5_000) || process.ExitCode != 0)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            return output.Length == 0 ? null : output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static BenchmarkRun? LoadBaseline(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        return JsonSerializer.Deserialize(stream, BenchmarkJsonContext.Default.BenchmarkRun);
    }

    private static EndToEndBenchmarkResult Failure(
        BenchmarkIndexScenario scenario,
        BenchmarkProfile profile,
        Exception exception) => new()
        {
            Scenario = scenario.Name,
            IndexKind = scenario.Kind.ToString(),
            Quantization = scenario.Quantization.ToString(),
            VectorCount = profile.VectorCount,
            Dimension = profile.Dimension,
            ErrorCount = 1,
            ErrorRate = 1,
            Failure = exception.GetType().Name + ": " + exception.Message,
        };

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        int index = Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length)
        : 0;

    private static long GetLohBytes()
    {
        ReadOnlySpan<GCGenerationInfo> generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Sanitize(string value) => new(value.Select(static character =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());

    private static ProcessStartInfo CreateServerStartInfo(
        string apiAssembly,
        string workingDirectory,
        Uri baseAddress,
        string storagePath)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(apiAssembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(baseAddress.AbsoluteUri);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["Storage__Path"] = storagePath;
        startInfo.Environment["Storage__FlushToDisk"] = "false";
        startInfo.Environment["DiskAnn__Path"] = Path.Combine(storagePath, "diskann");
        startInfo.Environment["Backup__Path"] = Path.Combine(storagePath, "backups");
        startInfo.Environment["Backpressure__GlobalQueueCapacity"] = "1";
        startInfo.Environment["Backpressure__PerCollectionQueueCapacity"] = "1";
        startInfo.Environment["Backpressure__PerShardQueueCapacity"] = "1";
        startInfo.Environment["Backpressure__PerClientQueueCapacity"] = "1";
        startInfo.Environment["Backpressure__MaximumConcurrentWrites"] = "1";
        startInfo.Environment["Backpressure__EnqueueTimeout"] = "00:00:00";
        startInfo.Environment["RateLimit__Enabled"] = "true";
        startInfo.Environment["RateLimit__Global__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Global__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Client__TokensPerSecond"] = "1";
        startInfo.Environment["RateLimit__Client__BurstCapacity"] = "50";
        startInfo.Environment["RateLimit__Collection__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Collection__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Read__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Read__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Write__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Write__BurstCapacity"] = "100000";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        return startInfo;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync<T>(
        HttpClient client,
        string requestUri,
        T value,
        JsonTypeInfo<T> typeInfo,
        string? clientId = null)
    {
        using JsonContent content = JsonContent.Create(value, typeInfo);
        using HttpRequestMessage request = new(HttpMethod.Post, requestUri) { Content = content };
        if (clientId is not null)
        {
            request.Headers.Add("X-SlimVector-Client-Id", clientId);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException(
                $"SlimVector returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
        }
    }

    private static async Task WaitForServerAsync(HttpClient client, Process process, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"SlimVector API exited with code {process.ExitCode} during startup.");
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health/live").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException($"SlimVector API did not become live within {timeout}.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SlimVector.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The SlimVector repository root could not be located.");
    }

    private static RaftGroupNodeOptions RaftOptions(
        IPEndPoint endpoint,
        IReadOnlyList<IPEndPoint> members,
        string storageRoot,
        int nodeIndex) => new()
        {
            GroupId = "catalog",
            LocalEndpoint = endpoint,
            Members = members,
            StoragePath = Path.Combine(storageRoot, "node-" + nodeIndex.ToString(CultureInfo.InvariantCulture)),
            LowerElectionTimeoutMilliseconds = 150,
            UpperElectionTimeoutMilliseconds = 350,
            RequestTimeout = TimeSpan.FromSeconds(2),
            SnapshotEveryEntries = 1_000,
            TransmissionBlockSize = 4 * 1024,
            WarmupRounds = 10,
        };

    private static IPEndPoint[] AllocateLoopbackEndpoints(int count)
    {
        IPEndPoint[] endpoints = new IPEndPoint[count];
        for (int index = 0; index < count; index++)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            endpoints[index] = (IPEndPoint)listener.LocalEndpoint;
        }

        return endpoints;
    }

    private static Guid DeterministicGuid(int value, byte prefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = prefix;
        BitConverter.TryWriteBytes(bytes[8..], value);
        return new Guid(bytes);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"The benchmark condition was not met within {timeout}.");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class BenchmarkCommandApplier : IRaftCommandApplier
{
    private readonly HashSet<Guid> _commands = [];
    private readonly Lock _sync = new();

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _commands.Count;
            }
        }
    }

    public ValueTask ApplyAsync(RaftCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _commands.Add(command.CommandId);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> CaptureSnapshotAsync(string groupId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Array.Empty<byte>());

    public ValueTask RestoreSnapshotAsync(
        string groupId,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private bool _stopped;
    private double _cpuTotal;
    private long _workingSetTotal;
    private int _samples;

    public ResourceSampler(Process process)
    {
        _process = process;
        _thread = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "SlimVector benchmark resource sampler",
        };
        _thread.Start();
    }

    public double AverageCpuUtilization => _samples == 0 ? 0 : _cpuTotal / _samples;

    public double PeakCpuUtilization { get; private set; }

    public long AverageWorkingSetBytes => _samples == 0 ? 0 : _workingSetTotal / _samples;

    public long PeakWorkingSetBytes { get; private set; }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopping = true;
        _thread.Join();
        _stopped = true;
    }

    public void Dispose() => Stop();

    private void SampleLoop()
    {
        long previousTimestamp = Stopwatch.GetTimestamp();
        TimeSpan previousCpu = _process.TotalProcessorTime;
        while (!_stopping)
        {
            Thread.Sleep(25);
            try
            {
                _process.Refresh();
                long now = Stopwatch.GetTimestamp();
                TimeSpan cpu = _process.TotalProcessorTime;
                double elapsedSeconds = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
                double utilization = elapsedSeconds <= 0
                    ? 0
                    : Math.Clamp(
                        (cpu - previousCpu).TotalSeconds / elapsedSeconds / Environment.ProcessorCount,
                        0,
                        1);
                long workingSet = _process.WorkingSet64;
                _cpuTotal += utilization;
                _workingSetTotal += workingSet;
                _samples++;
                PeakCpuUtilization = Math.Max(PeakCpuUtilization, utilization);
                PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, workingSet);
                previousTimestamp = now;
                previousCpu = cpu;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }
}

internal sealed record BenchmarkProfile(
    string Name,
    int VectorCount,
    int Dimension,
    int QueryCount,
    int TopK,
    int IvfLists,
    int IvfProbes,
    int PqSubvectors,
    int TrainingIterations,
    int HnswConstruction,
    int HnswDegree,
    int HnswSearch,
    int DiskAnnDegree,
    int DiskAnnSearchList);

internal sealed record BenchmarkIndexScenario(
    string Name,
    VectorIndexKind Kind,
    VectorQuantizationKind Quantization);

internal sealed record BenchmarkRun
{
    public int SchemaVersion { get; init; }

    public required BenchmarkEnvironment Environment { get; init; }

    public required IReadOnlyList<EndToEndBenchmarkResult> Results { get; init; }

    public BenchmarkRun? Baseline { get; init; }

    public double RegressionThreshold { get; init; } = 0.10;
}

internal sealed record BenchmarkEnvironment
{
    public required string Version { get; init; }

    public required string Profile { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required string Framework { get; init; }

    public int ProcessorCount { get; init; }

    public required string MachineName { get; init; }

    public bool ServerGc { get; init; }

    public long TotalAvailableMemoryBytes { get; init; }

    public required string Commit { get; init; }

    public required string CpuModel { get; init; }

    public required string DiskFormat { get; init; }

    public long DiskTotalBytes { get; init; }

    public long DiskAvailableBytes { get; init; }

    public int VectorCount { get; init; }

    public int Dimension { get; init; }

    public int QueryCount { get; init; }

    public int TopK { get; init; }

    public BenchmarkConfiguration? Configuration { get; init; }
}

internal sealed record BenchmarkConfiguration
{
    public int RandomSeed { get; init; }

    public required string DistanceMetric { get; init; }

    public required string[] IndexScenarios { get; init; }

    public int IvfLists { get; init; }

    public int IvfProbes { get; init; }

    public int PqSubvectors { get; init; }

    public int TrainingIterations { get; init; }

    public int HnswConstruction { get; init; }

    public int HnswDegree { get; init; }

    public int HnswSearch { get; init; }

    public int DiskAnnDegree { get; init; }

    public int DiskAnnSearchList { get; init; }

    public int ServerDocumentCount { get; init; }

    public required string[] ServerSearchModes { get; init; }

    public int BackpressureQueueCapacity { get; init; }

    public int BackpressureProbeRequests { get; init; }

    public int DocumentsPerPressureRequest { get; init; }

    public int MixedReadRequests { get; init; }

    public double ClientTokensPerSecond { get; init; }

    public double ClientBurstCapacity { get; init; }

    public int RateLimitProbeRequests { get; init; }
}

internal sealed record EndToEndBenchmarkResult
{
    public required string Scenario { get; init; }

    public required string IndexKind { get; init; }

    public required string Quantization { get; init; }

    public int VectorCount { get; init; }

    public int Dimension { get; init; }

    public double BuildMilliseconds { get; init; }

    public double PersistMilliseconds { get; init; }

    public double ColdLoadMilliseconds { get; init; }

    public double ThroughputQueriesPerSecond { get; init; }

    public double P50Milliseconds { get; init; }

    public double P95Milliseconds { get; init; }

    public double P99Milliseconds { get; init; }

    public double RecallAtK { get; init; }

    public double CpuSeconds { get; init; }

    public double CpuUtilization { get; init; }

    public double PeakCpuUtilization { get; init; }

    public long ManagedBytesDelta { get; init; }

    public long WorkingSetBytesDelta { get; init; }

    public long DiskBytes { get; init; }

    public long SnapshotBytes { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    public double? MigrationMilliseconds { get; init; }

    public double? RaftCatchUpMilliseconds { get; init; }

    public double? IngestMilliseconds { get; init; }

    public long PeakWorkingSetBytes { get; init; }

    public long AverageWorkingSetBytes { get; init; }

    public long ManagedBytesFreedAfterDispose { get; init; }

    public long LohBytesDelta { get; init; }

    public double GcPauseMilliseconds { get; init; }

    public double DiskWriteBytesPerSecond { get; init; }

    public int BackpressureRejections { get; init; }

    public int RateLimitRejections { get; init; }

    public double RateLimitRetryAfterSeconds { get; init; }

    public long IdleWorkingSetBytes { get; init; }

    public int RequestCount { get; init; }

    public int ErrorCount { get; init; }

    public double ErrorRate { get; init; }

    public int MixedReadSuccesses { get; init; }

    public string? Failure { get; init; }
}

internal sealed record ServerCreateCollectionRequest
{
    public required string Name { get; init; }

    public int Dimension { get; init; }

    public DistanceMetric Metric { get; init; }

    public required VectorIndexConfiguration VectorIndex { get; init; }
}

internal sealed record ServerDocumentInput
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public required float[] Vector { get; init; }
}

internal sealed record ServerDocumentBatchRequest
{
    public required ServerDocumentInput[] Documents { get; init; }

    public bool Atomic { get; init; }
}

internal sealed record ServerQueryRequest
{
    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public SearchMode Mode { get; init; }

    public int Limit { get; init; }

    public required string[] Include { get; init; }
}

internal sealed record ServerQueryResponse
{
    public required ServerQueryHit[] Hits { get; init; }
}

internal sealed record ServerQueryHit
{
    public required string Id { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkRun))]
[JsonSerializable(typeof(ServerCreateCollectionRequest))]
[JsonSerializable(typeof(ServerDocumentBatchRequest))]
[JsonSerializable(typeof(ServerQueryRequest))]
[JsonSerializable(typeof(ServerQueryResponse))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
