using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using MessagePack;
using SlimVector.Domain;

namespace SlimVector.Benchmarks;

internal static partial class ReliableBenchmarkWorker
{
    private static async Task<BenchmarkIterationResult> RunServerAsync(
        BenchmarkWorkerJob job,
        bool controlsOnly,
        bool saturationOnly = false)
    {
        ReliableIndexScenario scenario = job.Scenario ?? new ReliableIndexScenario
        {
            Name = "Flat-Float32",
            Kind = VectorIndexKind.Flat,
            Quantization = VectorQuantizationKind.Float32,
        };
        ReliableBenchmarkDataset dataset = ReliableBenchmarkDatasetFactory.Create(job.Profile, job.Dataset);
        string storagePath = Path.Combine(job.Workspace, "server-storage");
        Directory.CreateDirectory(storagePath);
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Uri address = new($"http://127.0.0.1:{port}");
        ProcessStartInfo startInfo = CreateServerStartInfo(job, address, storagePath, controlsOnly);
        using Process server = new() { StartInfo = startInfo };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        bool started = false;
        try
        {
            if (!server.Start())
            {
                throw new InvalidOperationException("The SlimVector API benchmark process could not be started.");
            }

            started = true;
            stdout = server.StandardOutput.ReadToEndAsync();
            stderr = server.StandardError.ReadToEndAsync();
            using HttpClient client = new() { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };
            List<OperationIterationResult> operations = [];
            using (ReliableOperationMeasurement startup = new("ServerProcessColdStart", 1, server, latencyUnit: "process-start"))
            {
                await WaitForServerAsync(client, server, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                operations.Add(startup.Complete(runtimeAfter: await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false)));
            }

            await CreateServerCollectionAsync(
                client,
                job.Profile,
                scenario,
                server,
                storagePath,
                operations,
                job.WireFormat).ConfigureAwait(false);
            DocumentRecord[] documents = dataset.Documents[..Math.Min(job.Profile.ServerDocumentCount, dataset.Documents.Length)];
            if (saturationOnly)
            {
                await InsertServerDocumentsAsync(
                    client,
                    documents,
                    server,
                    storagePath,
                    operations: null,
                    wireFormat: job.WireFormat).ConfigureAwait(false);
                SaturationResult saturation = await RunServerSaturationAsync(
                    client,
                    server,
                    storagePath,
                    dataset.Queries,
                    job.Profile,
                    job.WireFormat).ConfigureAwait(false);
                operations.AddRange(saturation.Operations);
                return new BenchmarkIterationResult
                {
                    Scenario = $"Server-Saturation-{scenario.Name}-{job.StorageMode}{ReliableBenchmarkRunner.WireFormatSuffix(job.WireFormat)}",
                    IndexKind = scenario.Kind.ToString(),
                    Quantization = scenario.Quantization.ToString(),
                    SearchTuning = scenario.SearchTuning,
                    StorageMode = job.StorageMode,
                    Iteration = job.Iteration,
                    ProcessId = Environment.ProcessId,
                    SuccessCount = saturation.Successes,
                    ErrorCount = saturation.Errors,
                    ExpectedRejectionCount = saturation.Rejections,
                    QueueSaturationRejections = saturation.QueueRejections,
                    CongestionRejections = saturation.CongestionRejections,
                    ContractualRateLimitRejections = saturation.RateLimitRejections,
                    OfferedCount = saturation.Offered,
                    CompletedCount = saturation.Completed,
                    MeetsSaturationSlo = saturation.Operations.All(static operation => operation.MeetsSaturationSlo == true),
                    CoordinatedOmissionCorrected = true,
                    MaxSustainableQps = saturation.MaxSustainableQps,
                    Operations = operations,
                };
            }

            if (controlsOnly)
            {
                await InsertServerDocumentsAsync(
                    client,
                    documents[..1],
                    server,
                    storagePath,
                    operations: null,
                    wireFormat: job.WireFormat).ConfigureAwait(false);
                await CreatePressureCollectionsAsync(
                    client,
                    job.Profile,
                    scenario,
                    count: 8,
                    wireFormat: job.WireFormat).ConfigureAwait(false);
                ServerControlResult controls = await RunServerControlsAsync(
                    client,
                    server,
                    storagePath,
                    documents,
                    dataset.Queries[0],
                    job.Profile.TopK,
                    job.WireFormat).ConfigureAwait(false);
                operations.AddRange(controls.Operations);
                return new BenchmarkIterationResult
                {
                    Scenario = $"Server-Control-{job.StorageMode}",
                    IndexKind = scenario.Kind.ToString(),
                    Quantization = scenario.Quantization.ToString(),
                    SearchTuning = scenario.SearchTuning,
                    StorageMode = job.StorageMode,
                    Iteration = job.Iteration,
                    ProcessId = Environment.ProcessId,
                    SuccessCount = controls.Successes,
                    ExpectedRejectionCount = controls.QueueRejections + controls.CongestionRejections + controls.RateLimitRejections,
                    QueueSaturationRejections = controls.QueueRejections,
                    CongestionRejections = controls.CongestionRejections,
                    ContractualRateLimitRejections = controls.RateLimitRejections,
                    Operations = operations,
                };
            }

            await InsertServerDocumentsAsync(client, documents, server, storagePath, operations, job.WireFormat).ConfigureAwait(false);
            (OperationIterationResult select, double recall) = await SelectServerDocumentsAsync(
                client,
                dataset,
                documents,
                server,
                storagePath,
                job.Profile.TopK,
                job.WireFormat).ConfigureAwait(false);
            operations.Add(select);
            int mutationCount = Math.Min(job.Profile.OperationCount, documents.Length);
            DocumentRecord[] mutations = documents[..mutationCount];
            operations.Add(await UpdateServerDocumentsAsync(
                client,
                mutations,
                server,
                storagePath,
                job.WireFormat).ConfigureAwait(false));
            operations.Add(await DeleteServerDocumentsAsync(
                client,
                mutations,
                server,
                storagePath,
                job.WireFormat).ConfigureAwait(false));
            return new BenchmarkIterationResult
            {
                Scenario = $"Server-{scenario.Name}-{job.StorageMode}{ReliableBenchmarkRunner.WireFormatSuffix(job.WireFormat)}",
                IndexKind = scenario.Kind.ToString(),
                Quantization = scenario.Quantization.ToString(),
                SearchTuning = scenario.SearchTuning,
                StorageMode = job.StorageMode,
                Iteration = job.Iteration,
                ProcessId = Environment.ProcessId,
                RecallAtK = recall,
                SuccessCount = documents.Length + dataset.Queries.Length + mutationCount * 2,
                Operations = operations,
            };
        }
        finally
        {
            if (started && !server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync().ConfigureAwait(false);
            }

            if (stdout is not null)
            {
                string content = await stdout.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    await File.WriteAllTextAsync(Path.Combine(job.Workspace, "server.stdout.log"), content).ConfigureAwait(false);
                }
            }

            if (stderr is not null)
            {
                string content = await stderr.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    await File.WriteAllTextAsync(Path.Combine(job.Workspace, "server.stderr.log"), content).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task CreatePressureCollectionsAsync(
        HttpClient client,
        ReliableBenchmarkProfile profile,
        ReliableIndexScenario scenario,
        int count,
        BenchmarkWireFormat wireFormat)
    {
        for (int index = 0; index < count; index++)
        {
            ServerCreateCollectionRequest request = new()
            {
                Name = "pressure-" + index.ToString(CultureInfo.InvariantCulture),
                Dimension = profile.Dimension,
                Metric = DistanceMetric.Cosine,
                VectorIndex = Definition(profile, scenario).VectorIndex,
            };
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/",
                request,
                BenchmarkJsonContext.Default.ServerCreateCollectionRequest,
                wireFormat).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
        }
    }

    private static async Task CreateServerCollectionAsync(
        HttpClient client,
        ReliableBenchmarkProfile profile,
        ReliableIndexScenario scenario,
        Process server,
        string storagePath,
        List<OperationIterationResult> operations,
        BenchmarkWireFormat wireFormat)
    {
        ServerCreateCollectionRequest request = new()
        {
            Name = "server-benchmark",
            Dimension = profile.Dimension,
            Metric = DistanceMetric.Cosine,
            VectorIndex = Definition(profile, scenario).VectorIndex,
        };
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            "CollectionCreate",
            1,
            server,
            latencyUnit: "http-request",
            artifactPath: storagePath,
            runtimeBefore: before);
        long started = Stopwatch.GetTimestamp();
        using HttpResponseMessage response = await PostPayloadAsync(
            client,
            "/api/v1/collections/",
            request,
            BenchmarkJsonContext.Default.ServerCreateCollectionRequest,
            wireFormat).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        RuntimeMetricsSnapshot after = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        operations.Add(measurement.Complete([Stopwatch.GetElapsedTime(started).TotalMilliseconds], runtimeAfter: after));
    }

    private static async Task InsertServerDocumentsAsync(
        HttpClient client,
        DocumentRecord[] documents,
        Process server,
        string storagePath,
        List<OperationIterationResult>? operations,
        BenchmarkWireFormat wireFormat)
    {
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            "HttpInsert",
            documents.Length,
            server,
            latencyUnit: "http-batch",
            batchSize: 200,
            artifactPath: storagePath,
            runtimeBefore: before);
        List<double> latencies = [];
        int batchIndex = 0;
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
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/upsert",
                request,
                BenchmarkJsonContext.Default.ServerDocumentBatchRequest,
                wireFormat,
                "benchmark-ingest-" + batchIndex++.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        RuntimeMetricsSnapshot after = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        OperationIterationResult result = measurement.Complete(latencies, runtimeAfter: after);
        operations?.Add(result);
    }

    private static async Task<(OperationIterationResult Operation, double Recall)> SelectServerDocumentsAsync(
        HttpClient client,
        ReliableBenchmarkDataset dataset,
        DocumentRecord[] indexedDocuments,
        Process server,
        string storagePath,
        int topK,
        BenchmarkWireFormat wireFormat)
    {
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            "HttpSelectMixed",
            dataset.Queries.Length,
            server,
            latencyUnit: "http-request",
            artifactPath: storagePath,
            runtimeBefore: before);
        List<double> latencies = [];
        HashSet<string>[] serverTruth = ReliableBenchmarkDatasetFactory.BuildTruth(indexedDocuments, dataset.Queries, topK);
        double recall = 0;
        int recallSamples = 0;
        for (int index = 0; index < dataset.Queries.Length; index++)
        {
            SearchMode mode = (index % 3) switch
            {
                0 => SearchMode.Vector,
                1 => SearchMode.Text,
                _ => SearchMode.Hybrid,
            };
            ServerQueryRequest request = new()
            {
                Text = mode == SearchMode.Vector ? null : "vector retrieval",
                Vector = mode == SearchMode.Text ? null : dataset.Queries[index],
                Mode = mode,
                Limit = topK,
                Include = [],
            };
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/query",
                request,
                BenchmarkJsonContext.Default.ServerQueryRequest,
                wireFormat,
                "benchmark-query-" + index.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            ServerQueryResponse result = await ReadPayloadAsync(
                response,
                BenchmarkJsonContext.Default.ServerQueryResponse,
                wireFormat).ConfigureAwait(false);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (mode == SearchMode.Vector)
            {
                recall += result.Hits.Count(hit => serverTruth[index].Contains(hit.Id)) / (double)topK;
                recallSamples++;
            }
        }

        RuntimeMetricsSnapshot after = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        return (measurement.Complete(latencies, runtimeAfter: after), recallSamples == 0 ? 0 : recall / recallSamples);
    }

    private static async Task<OperationIterationResult> UpdateServerDocumentsAsync(
        HttpClient client,
        DocumentRecord[] documents,
        Process server,
        string storagePath,
        BenchmarkWireFormat wireFormat)
    {
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            "HttpUpdate",
            documents.Length,
            server,
            latencyUnit: "http-batch",
            batchSize: 200,
            artifactPath: storagePath,
            runtimeBefore: before);
        List<double> latencies = [];
        int batchIndex = 0;
        foreach (DocumentRecord[] batch in documents.Chunk(200))
        {
            ServerDocumentUpdateBatchRequest request = new()
            {
                Atomic = true,
                Documents = batch.Select(static document => new ServerDocumentUpdateInput
                {
                    Id = document.Id,
                    Text = document.Text + " updated",
                    Vector = document.Vector.Select(static value => value * 0.98F).ToArray(),
                }).ToArray(),
            };
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await SendPayloadAsync(
                client,
                HttpMethod.Patch,
                "/api/v1/collections/server-benchmark/documents/",
                request,
                BenchmarkJsonContext.Default.ServerDocumentUpdateBatchRequest,
                wireFormat,
                "benchmark-update-" + batchIndex++.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return measurement.Complete(latencies, runtimeAfter: await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false));
    }

    private static async Task<OperationIterationResult> DeleteServerDocumentsAsync(
        HttpClient client,
        DocumentRecord[] documents,
        Process server,
        string storagePath,
        BenchmarkWireFormat wireFormat)
    {
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            "HttpDelete",
            documents.Length,
            server,
            latencyUnit: "http-batch",
            batchSize: 200,
            artifactPath: storagePath,
            runtimeBefore: before);
        List<double> latencies = [];
        int batchIndex = 0;
        foreach (DocumentRecord[] batch in documents.Chunk(200))
        {
            ServerDocumentDeleteRequest request = new()
            {
                Atomic = true,
                Ids = batch.Select(static document => document.Id).ToArray(),
            };
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/delete",
                request,
                BenchmarkJsonContext.Default.ServerDocumentDeleteRequest,
                wireFormat,
                "benchmark-delete-" + batchIndex++.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return measurement.Complete(latencies, runtimeAfter: await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false));
    }

    private static async Task<ServerControlResult> RunServerControlsAsync(
        HttpClient client,
        Process server,
        string storagePath,
        DocumentRecord[] documents,
        float[] query,
        int topK,
        BenchmarkWireFormat wireFormat)
    {
        RuntimeMetricsSnapshot pressureBefore = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement pressureMeasurement = new(
            "HttpMixedBackpressure",
            56,
            server,
            latencyUnit: "http-request",
            artifactPath: storagePath,
            runtimeBefore: pressureBefore);
        TaskCompletionSource<bool> pressureStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ControlRequestResult>[] writes = Enumerable.Range(0, 40).Select(async index =>
        {
            DocumentRecord source = documents[index % documents.Length];
            ServerDocumentBatchRequest request = new()
            {
                Atomic = true,
                Documents = Enumerable.Range(0, 20).Select(documentIndex => new ServerDocumentInput
                {
                    Id = $"pressure-{index}-{documentIndex}",
                    Text = source.Text,
                    Vector = source.Vector,
                }).ToArray(),
            };
            await pressureStart.Task.ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                $"/api/v1/collections/pressure-{index % 8}/documents/upsert",
                request,
                BenchmarkJsonContext.Default.ServerDocumentBatchRequest,
                wireFormat,
                "benchmark-pressure").ConfigureAwait(false);
            double latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                return new ControlRequestResult("success", latency);
            }

            string body = await ReadDiagnosticBodyAsync(response).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && body.Contains("queue_saturated", StringComparison.Ordinal))
            {
                return new ControlRequestResult("queue", latency);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                response.Headers.TryGetValues("X-SlimVector-RateLimit-Kind", out IEnumerable<string>? kinds) &&
                kinds.Contains("congestion", StringComparer.Ordinal))
            {
                return new ControlRequestResult("congestion", latency);
            }

            throw new HttpRequestException($"Unexpected pressure response {(int)response.StatusCode}: {body}");
        }).ToArray();
        Task<ControlRequestResult>[] reads = Enumerable.Range(0, 16).Select(async index =>
        {
            ServerQueryRequest request = new()
            {
                Text = "vector retrieval",
                Vector = query,
                Mode = SearchMode.Hybrid,
                Limit = topK,
                Include = [],
            };
            await pressureStart.Task.ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/query",
                request,
                BenchmarkJsonContext.Default.ServerQueryRequest,
                wireFormat,
                "benchmark-mixed-read-" + index.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            return new ControlRequestResult("success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }).ToArray();
        pressureStart.SetResult(true);
        ControlRequestResult[] pressure = await Task.WhenAll(writes.Concat(reads)).ConfigureAwait(false);
        int queue = pressure.Count(static result => result.Kind == "queue");
        int congestion = pressure.Count(static result => result.Kind == "congestion");
        RuntimeMetricsSnapshot pressureAfter = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        OperationIterationResult pressureOperation = pressureMeasurement.Complete(
            pressure.Select(static result => result.LatencyMilliseconds).ToArray(),
            expectedRejectionCount: queue + congestion,
            runtimeAfter: pressureAfter) with
        {
            QueueSaturationRejections = queue,
            CongestionRejections = congestion,
        };

        RuntimeMetricsSnapshot rateBefore = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement rateMeasurement = new(
            "HttpRateLimit",
            128,
            server,
            latencyUnit: "http-request",
            artifactPath: storagePath,
            runtimeBefore: rateBefore);
        int rateLimit = 0;
        int rateSuccess = 0;
        List<double> rateLatencies = [];
        for (int index = 0; index < 128; index++)
        {
            ServerQueryRequest request = new() { Vector = query, Mode = SearchMode.Vector, Limit = topK, Include = [] };
            long started = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await PostPayloadAsync(
                client,
                "/api/v1/collections/server-benchmark/documents/query",
                request,
                BenchmarkJsonContext.Default.ServerQueryRequest,
                wireFormat,
                "benchmark-rate-probe").ConfigureAwait(false);
            rateLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                response.Headers.TryGetValues("X-SlimVector-RateLimit-Kind", out IEnumerable<string>? kinds) &&
                kinds.Contains("contractual", StringComparer.Ordinal))
            {
                rateLimit++;
            }
            else
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                rateSuccess++;
            }
        }

        OperationIterationResult rateOperation = rateMeasurement.Complete(
            rateLatencies,
            expectedRejectionCount: rateLimit,
            runtimeAfter: await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false)) with
        {
            ContractualRateLimitRejections = rateLimit,
        };
        if (queue + congestion == 0 || rateLimit == 0)
        {
            throw new InvalidOperationException(
                $"The server control benchmark observed queue={queue}, congestion={congestion}, rate-limit={rateLimit}; " +
                "at least one backpressure and one contractual rejection are required.");
        }

        return new ServerControlResult(
            [pressureOperation, rateOperation],
            pressure.Count(static result => result.Kind == "success") + rateSuccess,
            queue,
            congestion,
            rateLimit);
    }

    private static async Task<SaturationResult> RunServerSaturationAsync(
        HttpClient client,
        Process server,
        string storagePath,
        IReadOnlyList<float[]> queries,
        ReliableBenchmarkProfile profile,
        BenchmarkWireFormat wireFormat)
    {
        if (profile.SaturationWarmupSeconds > 0)
        {
            _ = await RunOpenLoopStageAsync(
                client,
                server,
                storagePath,
                queries,
                profile.TopK,
                profile.SaturationRatesPerSecond[0],
                profile.SaturationWarmupSeconds,
                profile,
                "SaturationWarmup",
                wireFormat).ConfigureAwait(false);
        }

        List<OperationIterationResult> operations = [];
        foreach (int rate in profile.SaturationRatesPerSecond)
        {
            OpenLoopStageResult stage = await RunOpenLoopStageAsync(
                client,
                server,
                storagePath,
                queries,
                profile.TopK,
                rate,
                profile.SaturationStageSeconds,
                profile,
                $"SaturationOpenLoop-{rate.ToString(CultureInfo.InvariantCulture)}qps",
                wireFormat).ConfigureAwait(false);
            operations.Add(stage.Operation);
        }

        OperationIterationResult[] measured = operations.ToArray();
        double? maximumSustainable = measured
            .Where(static operation => operation.MeetsSaturationSlo == true)
            .Max(static operation => operation.OfferedRatePerSecond);
        return new SaturationResult(
            measured,
            measured.Sum(static operation => operation.CompletedCount - operation.ErrorCount - operation.ExpectedRejectionCount),
            measured.Sum(static operation => operation.ErrorCount),
            measured.Sum(static operation => operation.ExpectedRejectionCount),
            measured.Sum(static operation => operation.QueueSaturationRejections),
            measured.Sum(static operation => operation.CongestionRejections),
            measured.Sum(static operation => operation.ContractualRateLimitRejections),
            measured.Sum(static operation => operation.OfferedCount),
            measured.Sum(static operation => operation.CompletedCount),
            maximumSustainable);
    }

    private static async Task<OpenLoopStageResult> RunOpenLoopStageAsync(
        HttpClient client,
        Process server,
        string storagePath,
        IReadOnlyList<float[]> queries,
        int topK,
        int offeredRate,
        int durationSeconds,
        ReliableBenchmarkProfile profile,
        string operationName,
        BenchmarkWireFormat wireFormat)
    {
        int offered = checked(offeredRate * durationSeconds);
        double[] latencies = new double[offered];
        int successes = 0;
        int queueRejections = 0;
        int congestionRejections = 0;
        int rateLimitRejections = 0;
        int errors = 0;
        RuntimeMetricsSnapshot before = await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false);
        using ReliableOperationMeasurement measurement = new(
            operationName,
            offered,
            server,
            latencyUnit: "scheduled-http-request",
            artifactPath: storagePath,
            runtimeBefore: before);
        Channel<ScheduledRequest> channel = Channel.CreateUnbounded<ScheduledRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        int workerCount = Math.Clamp(Environment.ProcessorCount * 8, 32, 256);
        Task[] workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            await foreach (ScheduledRequest scheduled in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    ServerQueryRequest request = new()
                    {
                        Vector = queries[scheduled.Sequence % queries.Count],
                        Mode = SearchMode.Vector,
                        Limit = topK,
                        Include = [],
                    };
                    using HttpResponseMessage response = await PostPayloadAsync(
                        client,
                        "/api/v1/collections/server-benchmark/documents/query",
                        request,
                        BenchmarkJsonContext.Default.ServerQueryRequest,
                        wireFormat,
                        "benchmark-saturation").ConfigureAwait(false);
                    latencies[scheduled.Sequence] = Stopwatch.GetElapsedTime(
                        scheduled.IntendedTimestamp,
                        Stopwatch.GetTimestamp()).TotalMilliseconds;
                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref successes);
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        string body = await ReadDiagnosticBodyAsync(response).ConfigureAwait(false);
                        string? kind = response.Headers.TryGetValues(
                            "X-SlimVector-RateLimit-Kind",
                            out IEnumerable<string>? kinds)
                            ? kinds.FirstOrDefault()
                            : null;
                        if (body.Contains("queue_saturated", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref queueRejections);
                        }
                        else if (string.Equals(kind, "congestion", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref congestionRejections);
                        }
                        else if (string.Equals(kind, "contractual", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref rateLimitRejections);
                        }
                        else
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
                catch (HttpRequestException)
                {
                    latencies[scheduled.Sequence] = Stopwatch.GetElapsedTime(
                        scheduled.IntendedTimestamp,
                        Stopwatch.GetTimestamp()).TotalMilliseconds;
                    Interlocked.Increment(ref errors);
                }
                catch (TaskCanceledException)
                {
                    latencies[scheduled.Sequence] = Stopwatch.GetElapsedTime(
                        scheduled.IntendedTimestamp,
                        Stopwatch.GetTimestamp()).TotalMilliseconds;
                    Interlocked.Increment(ref errors);
                }
            }
        }).ToArray();

        long start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        double ticksPerRequest = Stopwatch.Frequency / (double)offeredRate;
        for (int sequence = 0; sequence < offered; sequence++)
        {
            long intended = start + (long)Math.Round(sequence * ticksPerRequest);
            await DelayUntilAsync(intended).ConfigureAwait(false);
            await channel.Writer.WriteAsync(new ScheduledRequest(sequence, intended)).ConfigureAwait(false);
        }

        channel.Writer.Complete();
        await Task.WhenAll(workers).ConfigureAwait(false);
        int rejections = queueRejections + congestionRejections + rateLimitRejections;
        int completed = successes + rejections + errors;
        double? p99 = ReliableBenchmarkStatistics.QualifiedPercentile(latencies, 0.99);
        bool meetsSlo = errors == 0 && completed == offered &&
            rejections / (double)offered <= profile.SaturationMaximumRejectionRatio &&
            p99.HasValue && p99.Value <= profile.SaturationMaximumP99Milliseconds;
        OperationIterationResult operation = measurement.Complete(
            latencies,
            errorCount: errors,
            expectedRejectionCount: rejections,
            runtimeAfter: await ScrapeRuntimeMetricsAsync(client).ConfigureAwait(false)) with
        {
            OfferedCount = offered,
            CompletedCount = completed,
            OfferedRatePerSecond = offeredRate,
            MeetsSaturationSlo = meetsSlo,
            CoordinatedOmissionCorrected = true,
            QueueSaturationRejections = queueRejections,
            CongestionRejections = congestionRejections,
            ContractualRateLimitRejections = rateLimitRejections,
        };
        return new OpenLoopStageResult(operation);
    }

    private static async Task DelayUntilAsync(long intendedTimestamp)
    {
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            if (now >= intendedTimestamp)
            {
                return;
            }

            double remainingMilliseconds = (intendedTimestamp - now) * 1_000D / Stopwatch.Frequency;
            if (remainingMilliseconds > 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remainingMilliseconds - 1)).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private static async Task<RuntimeMetricsSnapshot> ScrapeRuntimeMetricsAsync(HttpClient client)
    {
        string metrics = await client.GetStringAsync("/metrics").ConfigureAwait(false);
        return new RuntimeMetricsSnapshot
        {
            ManagedBytes = (long)ReadMetric(metrics, "slimvector_managed_memory_bytes"),
            AllocatedBytes = (long)ReadMetric(metrics, "slimvector_managed_allocated_bytes_total"),
            Gen0Collections = (int)ReadMetric(metrics, "slimvector_gc_collections_total", "generation=\"0\""),
            Gen1Collections = (int)ReadMetric(metrics, "slimvector_gc_collections_total", "generation=\"1\""),
            Gen2Collections = (int)ReadMetric(metrics, "slimvector_gc_collections_total", "generation=\"2\""),
            GcPauseMilliseconds = ReadMetric(metrics, "slimvector_gc_pause_seconds_total") * 1_000,
            LohBytes = (long)ReadMetric(metrics, "slimvector_gc_heap_bytes", "generation=\"loh\""),
            StorageReadBytes = (long)ReadMetric(metrics, "slimvector_storage_read_bytes_total"),
            StorageWrittenBytes = (long)ReadMetric(metrics, "slimvector_storage_written_bytes_total"),
            StorageDurableFlushes = (long)ReadMetric(metrics, "slimvector_storage_durable_flushes_total"),
        };
    }

    private static double ReadMetric(string metrics, string name, string? label = null)
    {
        string line = metrics.Split('\n').First(candidate =>
            (candidate.StartsWith(name + " ", StringComparison.Ordinal) || candidate.StartsWith(name + "{", StringComparison.Ordinal)) &&
            (label is null || candidate.Contains(label, StringComparison.Ordinal)));
        return double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
    }

    private static ProcessStartInfo CreateServerStartInfo(
        BenchmarkWorkerJob job,
        Uri address,
        string storagePath,
        bool controlsOnly)
    {
        string root = FindRepositoryRootForServer();
        string assembly = Path.Combine(root, "src", "SlimVector.Api", "bin", "Release", "net10.0", "SlimVector.Api.dll");
        if (!File.Exists(assembly))
        {
            throw new FileNotFoundException("Build the SlimVector.Api Release project before running E2E benchmarks.", assembly);
        }

        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(address.AbsoluteUri);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["Storage__Path"] = storagePath;
        startInfo.Environment["Storage__FlushToDisk"] = (job.StorageMode == BenchmarkStorageMode.Durable).ToString();
        startInfo.Environment["DiskAnn__Path"] = Path.Combine(storagePath, "diskann");
        startInfo.Environment["Backup__Path"] = Path.Combine(storagePath, "backups");
        startInfo.Environment["Backpressure__GlobalQueueCapacity"] = controlsOnly ? "1" : "1024";
        startInfo.Environment["Backpressure__PerCollectionQueueCapacity"] = controlsOnly ? "1" : "512";
        startInfo.Environment["Backpressure__PerShardQueueCapacity"] = controlsOnly ? "1" : "512";
        startInfo.Environment["Backpressure__PerClientQueueCapacity"] = controlsOnly ? "1" : "256";
        startInfo.Environment["Backpressure__MaximumConcurrentWrites"] = controlsOnly ? "1" : "16";
        startInfo.Environment["Backpressure__EnqueueTimeout"] = controlsOnly ? "00:00:00" : "00:00:05";
        startInfo.Environment["AdaptiveBatching__MinimumBatchSize"] = controlsOnly ? "16" : "1";
        startInfo.Environment["AdaptiveBatching__MaximumBatchSize"] = "256";
        startInfo.Environment["AdaptiveBatching__MinimumWindow"] = controlsOnly ? "00:00:00.100" : "00:00:00";
        startInfo.Environment["AdaptiveBatching__MaximumWindow"] = controlsOnly ? "00:00:00.100" : "00:00:00.050";
        startInfo.Environment["RateLimit__Enabled"] = controlsOnly ? "true" : "false";
        startInfo.Environment["RateLimit__Client__TokensPerSecond"] = "1";
        startInfo.Environment["RateLimit__Client__BurstCapacity"] = "50";
        startInfo.Environment["RateLimit__Global__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Global__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Collection__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Collection__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Read__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Read__BurstCapacity"] = "100000";
        startInfo.Environment["RateLimit__Write__TokensPerSecond"] = "100000";
        startInfo.Environment["RateLimit__Write__BurstCapacity"] = "100000";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        return startInfo;
    }

    private static async Task<HttpResponseMessage> PostPayloadAsync<T>(
        HttpClient client,
        string requestUri,
        T value,
        JsonTypeInfo<T> typeInfo,
        BenchmarkWireFormat wireFormat,
        string? clientId = null) => await SendPayloadAsync(
            client,
            HttpMethod.Post,
            requestUri,
            value,
            typeInfo,
            wireFormat,
            clientId).ConfigureAwait(false);

    private static async Task<HttpResponseMessage> SendPayloadAsync<T>(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        T value,
        JsonTypeInfo<T> typeInfo,
        BenchmarkWireFormat wireFormat,
        string? clientId = null)
    {
        using HttpRequestMessage request = new(method, requestUri);
        if (wireFormat == BenchmarkWireFormat.MessagePack)
        {
            byte[] payload = MessagePackSerializer.Serialize(value, BenchmarkMessagePack.Options);
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(BenchmarkMessagePack.MediaType);
            request.Headers.Accept.ParseAdd(BenchmarkMessagePack.MediaType);
        }
        else
        {
            request.Content = JsonContent.Create(value, typeInfo);
        }

        if (clientId is not null)
        {
            request.Headers.Add("X-SlimVector-Client-Id", clientId);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadPayloadAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        BenchmarkWireFormat wireFormat)
    {
        if (wireFormat == BenchmarkWireFormat.MessagePack)
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await MessagePackSerializer.DeserializeAsync<T>(stream, BenchmarkMessagePack.Options).ConfigureAwait(false)
                ?? throw new InvalidDataException("The server response was empty.");
        }

        return await response.Content.ReadFromJsonAsync(typeInfo).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server response was empty.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await ReadDiagnosticBodyAsync(response).ConfigureAwait(false);
            throw new HttpRequestException($"SlimVector returned HTTP {(int)response.StatusCode}: {body}");
        }
    }

    private static async Task<string> ReadDiagnosticBodyAsync(HttpResponseMessage response)
    {
        if (string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            BenchmarkMessagePack.MediaType,
            StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return MessagePackSerializer.ConvertToJson(bytes, BenchmarkMessagePack.Options);
        }

        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    private static string FindRepositoryRootForServer()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SlimVector.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The SlimVector repository root could not be located.");
    }

    private sealed record ControlRequestResult(string Kind, double LatencyMilliseconds);

    private readonly record struct ScheduledRequest(int Sequence, long IntendedTimestamp);

    private sealed record OpenLoopStageResult(OperationIterationResult Operation);

    private sealed record SaturationResult(
        IReadOnlyList<OperationIterationResult> Operations,
        int Successes,
        int Errors,
        int Rejections,
        int QueueRejections,
        int CongestionRejections,
        int RateLimitRejections,
        int Offered,
        int Completed,
        double? MaxSustainableQps);

    private sealed record ServerControlResult(
        IReadOnlyList<OperationIterationResult> Operations,
        int Successes,
        int QueueRejections,
        int CongestionRejections,
        int RateLimitRejections);
}
