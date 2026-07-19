using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Raft;
using SlimVector.Raft.Commands;

namespace SlimVector.Benchmarks;

internal static partial class ReliableBenchmarkWorker
{
    public static async Task<int> RunAsync(string jobPath, string resultPath)
    {
        BenchmarkWorkerJob job;
        await using (FileStream stream = File.OpenRead(jobPath))
        {
            job = await JsonSerializer.DeserializeAsync(stream, ReliableBenchmarkJsonContext.Default.BenchmarkWorkerJob)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The benchmark worker job is empty.");
        }

        BenchmarkIterationResult result;
        int? coldLoadProcessId = null;
        try
        {
            (result, coldLoadProcessId) = job.Kind switch
            {
                BenchmarkJobKind.Index => await RunIndexAsync(job).ConfigureAwait(false),
                BenchmarkJobKind.ColdLoad => (RunColdLoad(job), null),
                BenchmarkJobKind.ServerCrud => (await RunServerAsync(job, controlsOnly: false).ConfigureAwait(false), null),
                BenchmarkJobKind.ServerControl => (await RunServerAsync(job, controlsOnly: true).ConfigureAwait(false), null),
                BenchmarkJobKind.Migration => (RunMigration(job), null),
                BenchmarkJobKind.Raft => (await RunRaftAsync(job).ConfigureAwait(false), null),
                _ => throw new InvalidOperationException($"Unsupported benchmark worker kind '{job.Kind}'."),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            result = Failure(job, exception);
        }

        BenchmarkWorkerEnvelope envelope = new()
        {
            ProcessId = Environment.ProcessId,
            ColdLoadProcessId = coldLoadProcessId,
            Result = result with { ProcessId = Environment.ProcessId, ColdLoadProcessId = coldLoadProcessId },
        };
        await ReliableBenchmarkProcess.WriteAtomicAsync(
            resultPath,
            envelope,
            ReliableBenchmarkJsonContext.Default.BenchmarkWorkerEnvelope).ConfigureAwait(false);
        return result.Failure is null ? 0 : 1;
    }

    private static async Task<(BenchmarkIterationResult Result, int? ColdLoadProcessId)> RunIndexAsync(BenchmarkWorkerJob job)
    {
        ReliableIndexScenario scenario = job.Scenario ?? throw new InvalidDataException("An index worker requires a scenario.");
        ReliableBenchmarkDataset dataset = ReliableBenchmarkDatasetFactory.Create(job.Profile, job.Dataset);
        string artifactPath = Path.Combine(job.Workspace, "index-artifacts");
        Directory.CreateDirectory(artifactPath);
        CollectionDefinition definition = Definition(job.Profile, scenario);
        List<OperationIterationResult> operations = [];
        Process process = Process.GetCurrentProcess();
        CollectionSearchIndex index;
        using (ReliableOperationMeasurement measurement = new("IndexBuild", dataset.Documents.Length, process, artifactPath: artifactPath))
        {
            index = new CollectionSearchIndex(
                definition,
                scenario.Kind,
                dataset.Documents,
                persistedVectorIndex: null,
                diskAnnArtifactDirectory: artifactPath);
            operations.Add(measurement.Complete());
        }

        double recall = 0;
        byte[] snapshot;
        int mutationCount;
        using (index)
        {
            _ = index.Search(
                new SearchRequest { Mode = SearchMode.Vector, Vector = dataset.Queries[0], Limit = job.Profile.TopK },
                4);
            List<double> searchLatencies = new(dataset.Queries.Length);
            using (ReliableOperationMeasurement measurement = new("SelectVector", dataset.Queries.Length, process))
            {
                for (int queryIndex = 0; queryIndex < dataset.Queries.Length; queryIndex++)
                {
                    SearchRequest request = new()
                    {
                        Mode = SearchMode.Vector,
                        Vector = dataset.Queries[queryIndex],
                        Limit = job.Profile.TopK,
                    };
                    long started = Stopwatch.GetTimestamp();
                    IReadOnlyList<HybridRankedResult> found = index.Search(request, 4);
                    searchLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    recall += found.Count(result => dataset.Truth[queryIndex].Contains(result.Id)) / (double)job.Profile.TopK;
                }

                operations.Add(measurement.Complete(searchLatencies));
            }

            DocumentRecord[] inserted = CreateOperationDocuments(job.Profile, dataset.Documents);
            mutationCount = inserted.Length;
            operations.Add(MeasureMutation("Insert", inserted, process, document => index.Upsert(document)));
            DocumentRecord[] updated = inserted.Select(static document => document with
            {
                Text = document.Text + " updated",
                Vector = document.Vector.Select(static value => value * 0.95F + 0.01F).ToArray(),
                Version = document.Version + 1,
            }).ToArray();
            operations.Add(MeasureMutation("Update", updated, process, document => index.Upsert(document)));
            operations.Add(MeasureDelete(updated, process, index));

            using (ReliableOperationMeasurement measurement = new("SerializeSnapshot", dataset.Documents.Length, process))
            {
                snapshot = index.Serialize(dataset.Documents);
                operations.Add(measurement.Complete());
            }
        }

        string snapshotPath = Path.Combine(job.Workspace, "snapshot.bin");
        using (ReliableOperationMeasurement measurement = new(
                   "DurableSnapshotWrite",
                   snapshot.Length,
                   process,
                   latencyUnit: "byte",
                   artifactPath: snapshotPath))
        {
            await using FileStream stream = new(
                snapshotPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.WriteAsync(snapshot).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            operations.Add(measurement.Complete());
        }

        BenchmarkWorkerJob coldJob = job with
        {
            Kind = BenchmarkJobKind.ColdLoad,
            Workspace = Path.Combine(job.Workspace, "cold-load"),
            SnapshotPath = snapshotPath,
        };
        Directory.CreateDirectory(coldJob.Workspace);
        BenchmarkWorkerEnvelope cold = await ReliableBenchmarkProcess.RunWorkerAsync(coldJob).ConfigureAwait(false);
        OperationIterationResult coldOperation = cold.Result.Operations.Single(static operation =>
            string.Equals(operation.Operation, "ProcessColdLoad", StringComparison.Ordinal));
        operations.Add(coldOperation);
        return (new BenchmarkIterationResult
        {
            Scenario = scenario.Name,
            IndexKind = scenario.Kind.ToString(),
            Quantization = scenario.Quantization.ToString(),
            SearchTuning = scenario.SearchTuning,
            Iteration = job.Iteration,
            ProcessId = Environment.ProcessId,
            ColdLoadProcessId = cold.ProcessId,
            RecallAtK = recall / dataset.Queries.Length,
            SuccessCount = dataset.Queries.Length + mutationCount * 3,
            Operations = operations,
        }, cold.ProcessId);
    }

    private static BenchmarkIterationResult RunColdLoad(BenchmarkWorkerJob job)
    {
        ReliableIndexScenario scenario = job.Scenario ?? throw new InvalidDataException("A cold-load worker requires a scenario.");
        string snapshotPath = job.SnapshotPath ?? throw new InvalidDataException("A cold-load worker requires a snapshot path.");
        ReliableBenchmarkDataset dataset = ReliableBenchmarkDatasetFactory.Create(
            job.Profile,
            job.Dataset,
            includeQueriesAndTruth: false);
        Directory.CreateDirectory(job.Workspace);
        Process process = Process.GetCurrentProcess();
        OperationIterationResult operation;
        using (ReliableOperationMeasurement measurement = new(
                   "ProcessColdLoad",
                   dataset.Documents.Length,
                   process,
                   artifactPath: job.Workspace))
        {
            byte[] snapshot = File.ReadAllBytes(snapshotPath);
            using CollectionSearchIndex index = new(
                Definition(job.Profile, scenario),
                scenario.Kind,
                dataset.Documents,
                snapshot,
                diskAnnArtifactDirectory: Path.Combine(job.Workspace, "index-artifacts"));
            operation = measurement.Complete();
        }

        return new BenchmarkIterationResult
        {
            Scenario = scenario.Name + "-ColdLoad",
            IndexKind = scenario.Kind.ToString(),
            Quantization = scenario.Quantization.ToString(),
            SearchTuning = scenario.SearchTuning,
            Iteration = job.Iteration,
            ProcessId = Environment.ProcessId,
            SuccessCount = dataset.Documents.Length,
            Operations = [operation],
        };
    }

    private static BenchmarkIterationResult RunMigration(BenchmarkWorkerJob job)
    {
        ReliableBenchmarkDataset dataset = ReliableBenchmarkDatasetFactory.Create(job.Profile, job.Dataset);
        ReliableIndexScenario scenario = new()
        {
            Name = "AutoMigration-FlatToHnsw",
            Kind = VectorIndexKind.Hnsw,
            Quantization = VectorQuantizationKind.Float32,
            SearchTuning = 64,
        };
        string artifactPath = Path.Combine(job.Workspace, "migration");
        Directory.CreateDirectory(artifactPath);
        Process process = Process.GetCurrentProcess();
        CollectionSearchIndex candidate;
        OperationIterationResult operation;
        using (ReliableOperationMeasurement measurement = new("AutoIndexMigration", dataset.Documents.Length, process, artifactPath: artifactPath))
        {
            candidate = new CollectionSearchIndex(
                Definition(job.Profile, scenario),
                scenario.Kind,
                dataset.Documents,
                persistedVectorIndex: null,
                diskAnnArtifactDirectory: artifactPath);
            operation = measurement.Complete();
        }

        double recall = 0;
        using (candidate)
        {
            for (int index = 0; index < dataset.Queries.Length; index++)
            {
                IReadOnlyList<HybridRankedResult> found = candidate.Search(
                    new SearchRequest { Mode = SearchMode.Vector, Vector = dataset.Queries[index], Limit = job.Profile.TopK },
                    4);
                recall += found.Count(result => dataset.Truth[index].Contains(result.Id)) / (double)job.Profile.TopK;
            }
        }

        return new BenchmarkIterationResult
        {
            Scenario = scenario.Name,
            IndexKind = scenario.Kind.ToString(),
            Quantization = scenario.Quantization.ToString(),
            SearchTuning = scenario.SearchTuning,
            Iteration = job.Iteration,
            ProcessId = Environment.ProcessId,
            RecallAtK = recall / dataset.Queries.Length,
            SuccessCount = dataset.Documents.Length,
            Operations = [operation],
        };
    }

    private static async Task<BenchmarkIterationResult> RunRaftAsync(BenchmarkWorkerJob job)
    {
        const string scenario = "Raft-Add-CatchUp";
        string storageRoot = Path.Combine(job.Workspace, "raft-catch-up");
        Directory.CreateDirectory(storageRoot);
        int commandCount = job.Profile.Name switch
        {
            "Smoke" => 5,
            "Standard" => 1_000,
            _ => 10_000,
        };
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(4);
        ReliableBenchmarkCommandApplier[] appliers = [new(), new(), new(), new()];
        RaftGroupNode?[] nodes = new RaftGroupNode?[4];
        try
        {
            for (int index = 0; index < 3; index++)
            {
                nodes[index] = new RaftGroupNode(
                    RaftOptions(endpoints[index], endpoints[..3], storageRoot, index),
                    appliers[index]);
            }

            using CancellationTokenSource timeout = new(job.Profile.Name == "Large"
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
            Process process = Process.GetCurrentProcess();
            OperationIterationResult operation;
            using (ReliableOperationMeasurement measurement = new("RaftAddCatchUp", commandCount, process, artifactPath: storageRoot))
            {
                bool added = false;
                for (int attempt = 0; attempt < 3 && !added; attempt++)
                {
                    EndPoint currentLeader = await nodes[0]!.WaitForLeaderAsync(TimeSpan.FromSeconds(20), timeout.Token)
                        .ConfigureAwait(false);
                    leader = nodes[..3].Single(node => Equals(node!.LocalEndpoint, currentLeader))!;
                    await WaitUntilAsync(() => leader.IsLeader, TimeSpan.FromSeconds(20), timeout.Token).ConfigureAwait(false);
                    added = await leader.AddMemberAsync(endpoints[3], timeout.Token).ConfigureAwait(false);
                }

                if (!added)
                {
                    throw new InvalidOperationException("DotNext rejected the joining benchmark member.");
                }

                await WaitUntilAsync(
                    () => appliers[3].Count == commandCount,
                    TimeSpan.FromSeconds(30),
                    timeout.Token).ConfigureAwait(false);
                operation = measurement.Complete();
            }

            return new BenchmarkIterationResult
            {
                Scenario = scenario,
                IndexKind = "Raft",
                Quantization = "n/a",
                Iteration = job.Iteration,
                ProcessId = Environment.ProcessId,
                SuccessCount = commandCount,
                Operations = [operation],
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

    private static OperationIterationResult MeasureMutation(
        string operation,
        DocumentRecord[] documents,
        Process process,
        Action<DocumentRecord> mutate)
    {
        const int batchSize = 16;
        List<double> latencies = [];
        using ReliableOperationMeasurement measurement = new(
            operation,
            documents.Length,
            process,
            latencyUnit: "batch",
            batchSize: batchSize);
        foreach (DocumentRecord[] batch in documents.Chunk(batchSize))
        {
            long started = Stopwatch.GetTimestamp();
            foreach (DocumentRecord document in batch)
            {
                mutate(document);
            }

            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return measurement.Complete(latencies);
    }

    private static OperationIterationResult MeasureDelete(
        DocumentRecord[] documents,
        Process process,
        CollectionSearchIndex index)
    {
        return MeasureMutation("Delete", documents, process, document =>
        {
            if (!index.Remove(document.Id))
            {
                throw new InvalidDataException($"The benchmark could not remove '{document.Id}'.");
            }
        });
    }

    private static DocumentRecord[] CreateOperationDocuments(
        ReliableBenchmarkProfile profile,
        DocumentRecord[] documents) => Enumerable.Range(0, profile.OperationCount)
        .Select(index => documents[index % documents.Length] with
        {
            Id = "operation-" + index.ToString(CultureInfo.InvariantCulture),
            Text = "benchmark insert update delete document " + index.ToString(CultureInfo.InvariantCulture),
            Vector = documents[index % documents.Length].Vector.Select(static value => value * 0.99F + 0.001F).ToArray(),
            Version = 1,
        }).ToArray();

    internal static CollectionDefinition Definition(ReliableBenchmarkProfile profile, ReliableIndexScenario scenario)
    {
        int listCount = Math.Min(profile.IvfLists, profile.VectorCount);
        int tuning = scenario.SearchTuning ?? scenario.Kind switch
        {
            VectorIndexKind.Hnsw => 64,
            VectorIndexKind.IvfFlat or VectorIndexKind.IvfPq => Math.Min(4, listCount),
            VectorIndexKind.DiskAnn => 64,
            _ => 0,
        };
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
                HnswEfSearch = scenario.Kind == VectorIndexKind.Hnsw ? tuning : 64,
                IvfListCount = listCount,
                IvfProbeCount = scenario.Kind is VectorIndexKind.IvfFlat or VectorIndexKind.IvfPq
                    ? Math.Clamp(tuning, 1, listCount)
                    : Math.Min(4, listCount),
                IvfTrainingIterations = profile.TrainingIterations,
                PqSubvectorCount = profile.PqSubvectors,
                PqCentroidCount = Math.Min(256, profile.VectorCount),
                PqTrainingIterations = profile.TrainingIterations,
                DiskAnnMaxDegree = profile.DiskAnnDegree,
                DiskAnnSearchListSize = scenario.Kind == VectorIndexKind.DiskAnn ? tuning : 64,
                DiskAnnBeamWidth = 4,
                DiskAnnDeltaThreshold = Math.Max(100, profile.VectorCount / 10),
            });
    }

    private static BenchmarkIterationResult Failure(BenchmarkWorkerJob job, Exception exception)
    {
        string scenario = job.Kind switch
        {
            BenchmarkJobKind.ServerCrud => $"Server-{job.Scenario!.Name}-{job.StorageMode}",
            BenchmarkJobKind.ServerControl => $"Server-Control-{job.StorageMode}",
            BenchmarkJobKind.Migration => "AutoMigration-FlatToHnsw",
            BenchmarkJobKind.Raft => "Raft-Add-CatchUp",
            _ => job.Scenario?.Name ?? job.Kind.ToString(),
        };
        return new BenchmarkIterationResult
        {
            Scenario = scenario,
            IndexKind = job.Scenario?.Kind.ToString() ?? job.Kind.ToString(),
            Quantization = job.Scenario?.Quantization.ToString() ?? "n/a",
            SearchTuning = job.Scenario?.SearchTuning,
            StorageMode = job.Kind is BenchmarkJobKind.ServerCrud or BenchmarkJobKind.ServerControl ? job.StorageMode : null,
            Iteration = job.Iteration,
            ProcessId = Environment.ProcessId,
            ErrorCount = 1,
            Failure = exception.GetType().Name + ": " + exception.Message,
        };
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

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
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

internal static class ReliableBenchmarkProcess
{
    public static async Task<BenchmarkWorkerEnvelope> RunWorkerAsync(BenchmarkWorkerJob job)
    {
        Directory.CreateDirectory(job.Workspace);
        string jobPath = Path.Combine(job.Workspace, "worker-job.json");
        string resultPath = Path.Combine(job.Workspace, "worker-result.json");
        await WriteAtomicAsync(jobPath, job, ReliableBenchmarkJsonContext.Default.BenchmarkWorkerJob).ConfigureAwait(false);
        string benchmarkAssembly = Path.Combine(
            AppContext.BaseDirectory,
            (Assembly.GetExecutingAssembly().GetName().Name ?? "SlimVector.Benchmarks") + ".dll");
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = FindRepositoryRoot(),
        };
        startInfo.ArgumentList.Add(benchmarkAssembly);
        startInfo.ArgumentList.Add("--e2e");
        startInfo.ArgumentList.Add("--worker-job");
        startInfo.ArgumentList.Add(jobPath);
        startInfo.ArgumentList.Add("--worker-result");
        startInfo.ArgumentList.Add(resultPath);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The benchmark worker could not be started.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(job.Profile.Name == "Large"
            ? TimeSpan.FromHours(2)
            : TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Benchmark worker {process.Id} exceeded its timeout.");
        }

        string standardOutput = await stdout.ConfigureAwait(false);
        string standardError = await stderr.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            await File.WriteAllTextAsync(Path.Combine(job.Workspace, "worker.stdout.log"), standardOutput).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            await File.WriteAllTextAsync(Path.Combine(job.Workspace, "worker.stderr.log"), standardError).ConfigureAwait(false);
        }

        if (!File.Exists(resultPath))
        {
            throw new InvalidOperationException(
                $"Benchmark worker {process.Id} exited with code {process.ExitCode} without a result: {standardError.Trim()}");
        }

        await using FileStream resultStream = File.OpenRead(resultPath);
        return await JsonSerializer.DeserializeAsync(resultStream, ReliableBenchmarkJsonContext.Default.BenchmarkWorkerEnvelope)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The benchmark worker result is empty.");
    }

    public static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, typeInfo).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
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
}

internal sealed class ReliableBenchmarkCommandApplier : IRaftCommandApplier
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
