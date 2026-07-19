using System.Diagnostics;

namespace SlimVector.Benchmarks;

internal sealed record RuntimeMetricsSnapshot
{
    public long ManagedBytes { get; init; }

    public long AllocatedBytes { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    public long LohBytes { get; init; }

    public double GcPauseMilliseconds { get; init; }

    public long StorageReadBytes { get; init; }

    public long StorageWrittenBytes { get; init; }

    public long StorageDurableFlushes { get; init; }
}

internal sealed class ReliableResourceSampler : IDisposable
{
    private readonly Process _process;
    private readonly Thread _thread;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly List<ResourceSample> _samples = [];
    private volatile bool _stopping;
    private bool _stopped;

    public ReliableResourceSampler(Process process)
    {
        _process = process;
        _thread = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "SlimVector reliable benchmark resource sampler",
        };
        _thread.Start();
    }

    public IReadOnlyList<ResourceSample> Samples => _samples;

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
                double seconds = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
                double coreEquivalent = seconds <= 0 ? 0 : Math.Max(0, (cpu - previousCpu).TotalSeconds / seconds);
                _samples.Add(new ResourceSample
                {
                    ElapsedMilliseconds = _elapsed.Elapsed.TotalMilliseconds,
                    CpuCoreEquivalent = coreEquivalent,
                    NormalizedCpuUtilization = Math.Clamp(coreEquivalent / Environment.ProcessorCount, 0, 1),
                    WorkingSetBytes = _process.WorkingSet64,
                    PrivateBytes = _process.PrivateMemorySize64,
                });
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

internal sealed class ReliableOperationMeasurement : IDisposable
{
    private readonly string _operation;
    private readonly string _latencyUnit;
    private readonly int _batchSize;
    private readonly int _itemCount;
    private readonly Process _process;
    private readonly string? _artifactPath;
    private readonly bool _capturesManagedRuntime;
    private readonly RuntimeMetricsSnapshot? _runtimeBefore;
    private readonly TimeSpan _cpuBefore;
    private readonly long _workingSetBefore;
    private readonly long _privateBytesBefore;
    private readonly long _managedBefore;
    private readonly long _allocatedBefore;
    private readonly int _gen0Before;
    private readonly int _gen1Before;
    private readonly int _gen2Before;
    private readonly long _lohBefore;
    private readonly TimeSpan _gcPauseBefore;
    private readonly long _artifactSizeBefore;
    private readonly ReliableResourceSampler _sampler;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private bool _completed;

    public ReliableOperationMeasurement(
        string operation,
        int itemCount,
        Process process,
        string latencyUnit = "operation",
        int batchSize = 1,
        string? artifactPath = null,
        RuntimeMetricsSnapshot? runtimeBefore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(latencyUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _operation = operation;
        _latencyUnit = latencyUnit;
        _batchSize = batchSize;
        _itemCount = itemCount;
        _process = process;
        _artifactPath = artifactPath;
        _runtimeBefore = runtimeBefore;
        process.Refresh();
        _capturesManagedRuntime = process.Id == Environment.ProcessId;
        _cpuBefore = process.TotalProcessorTime;
        _workingSetBefore = process.WorkingSet64;
        _privateBytesBefore = process.PrivateMemorySize64;
        _managedBefore = _capturesManagedRuntime ? GC.GetTotalMemory(forceFullCollection: false) : 0;
        _allocatedBefore = _capturesManagedRuntime ? GC.GetTotalAllocatedBytes(precise: false) : 0;
        _gen0Before = _capturesManagedRuntime ? GC.CollectionCount(0) : 0;
        _gen1Before = _capturesManagedRuntime ? GC.CollectionCount(1) : 0;
        _gen2Before = _capturesManagedRuntime ? GC.CollectionCount(2) : 0;
        _lohBefore = _capturesManagedRuntime ? GetLohBytes() : 0;
        _gcPauseBefore = _capturesManagedRuntime ? GC.GetTotalPauseDuration() : TimeSpan.Zero;
        _artifactSizeBefore = PathSize(artifactPath);
        _sampler = new ReliableResourceSampler(process);
    }

    public OperationIterationResult Complete(
        IReadOnlyList<double>? latencies = null,
        int errorCount = 0,
        int expectedRejectionCount = 0,
        RuntimeMetricsSnapshot? runtimeAfter = null)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        _wall.Stop();
        _sampler.Stop();
        _process.Refresh();
        TimeSpan cpuAfter = _process.TotalProcessorTime;
        long workingSetAfter = _process.WorkingSet64;
        long privateBytesAfter = _process.PrivateMemorySize64;
        double cpuSeconds = Math.Max(0, (cpuAfter - _cpuBefore).TotalSeconds);
        double wallSeconds = _wall.Elapsed.TotalSeconds;
        double coreEquivalent = wallSeconds <= 0 ? 0 : cpuSeconds / wallSeconds;
        ResourceSample[] samples = _sampler.Samples.ToArray();
        long? managedBefore = _capturesManagedRuntime ? _managedBefore : _runtimeBefore?.ManagedBytes;
        long? managedAfter = _capturesManagedRuntime
            ? GC.GetTotalMemory(forceFullCollection: false)
            : runtimeAfter?.ManagedBytes;
        long? allocated = _capturesManagedRuntime
            ? Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _allocatedBefore)
            : Delta(_runtimeBefore?.AllocatedBytes, runtimeAfter?.AllocatedBytes);
        int? gen0 = _capturesManagedRuntime ? GC.CollectionCount(0) - _gen0Before : IntDelta(_runtimeBefore?.Gen0Collections, runtimeAfter?.Gen0Collections);
        int? gen1 = _capturesManagedRuntime ? GC.CollectionCount(1) - _gen1Before : IntDelta(_runtimeBefore?.Gen1Collections, runtimeAfter?.Gen1Collections);
        int? gen2 = _capturesManagedRuntime ? GC.CollectionCount(2) - _gen2Before : IntDelta(_runtimeBefore?.Gen2Collections, runtimeAfter?.Gen2Collections);
        long? loh = _capturesManagedRuntime ? GetLohBytes() - _lohBefore : Delta(_runtimeBefore?.LohBytes, runtimeAfter?.LohBytes);
        double? gcPause = _capturesManagedRuntime
            ? (GC.GetTotalPauseDuration() - _gcPauseBefore).TotalMilliseconds
            : DoubleDelta(_runtimeBefore?.GcPauseMilliseconds, runtimeAfter?.GcPauseMilliseconds);
        double[] latencyValues = latencies?.ToArray() ?? [];
        return new OperationIterationResult
        {
            Operation = _operation,
            LatencyUnit = _latencyUnit,
            BatchSize = _batchSize,
            ItemCount = _itemCount,
            SampleCount = latencyValues.Length,
            WallMilliseconds = _wall.Elapsed.TotalMilliseconds,
            ThroughputPerSecond = wallSeconds <= 0 ? 0 : _itemCount / wallSeconds,
            P50Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencyValues, 0.50),
            P95Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencyValues, 0.95),
            P99Milliseconds = ReliableBenchmarkStatistics.QualifiedPercentile(latencyValues, 0.99),
            CpuSeconds = cpuSeconds,
            AverageCpuCoreEquivalent = coreEquivalent,
            NormalizedCpuUtilization = Math.Clamp(coreEquivalent / Environment.ProcessorCount, 0, 1),
            PeakNormalizedCpuUtilization = samples.Length < 2 ? null : samples.Max(static sample => sample.NormalizedCpuUtilization),
            WorkingSetBytesBefore = _workingSetBefore,
            WorkingSetBytesAfter = workingSetAfter,
            WorkingSetBytesDelta = workingSetAfter - _workingSetBefore,
            AverageWorkingSetBytes = samples.Length < 2 ? null : (long)samples.Average(static sample => sample.WorkingSetBytes),
            PeakWorkingSetBytes = samples.Length < 2 ? null : samples.Max(static sample => sample.WorkingSetBytes),
            PrivateBytesBefore = _privateBytesBefore,
            PrivateBytesAfter = privateBytesAfter,
            PrivateBytesDelta = privateBytesAfter - _privateBytesBefore,
            ManagedBytesBefore = managedBefore,
            ManagedBytesAfter = managedAfter,
            ManagedBytesDelta = Delta(managedBefore, managedAfter),
            AllocatedBytes = allocated,
            Gen0Collections = gen0,
            Gen1Collections = gen1,
            Gen2Collections = gen2,
            LohBytesDelta = loh,
            GcPauseMilliseconds = gcPause,
            ArtifactSizeDeltaBytes = PathSize(_artifactPath) - _artifactSizeBefore,
            StorageReadBytes = Delta(_runtimeBefore?.StorageReadBytes, runtimeAfter?.StorageReadBytes),
            StorageWrittenBytes = Delta(_runtimeBefore?.StorageWrittenBytes, runtimeAfter?.StorageWrittenBytes),
            StorageDurableFlushes = Delta(_runtimeBefore?.StorageDurableFlushes, runtimeAfter?.StorageDurableFlushes),
            ErrorCount = errorCount,
            ExpectedRejectionCount = expectedRejectionCount,
            LatencySamplesMilliseconds = latencyValues,
            ResourceSamples = samples,
        };
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _wall.Stop();
            _sampler.Dispose();
            _completed = true;
        }
    }

    private static long? Delta(long? before, long? after) => before.HasValue && after.HasValue ? after.Value - before.Value : null;

    private static int? IntDelta(int? before, int? after) => before.HasValue && after.HasValue ? after.Value - before.Value : null;

    private static double? DoubleDelta(double? before, double? after) => before.HasValue && after.HasValue ? after.Value - before.Value : null;

    private static long GetLohBytes()
    {
        ReadOnlySpan<GCGenerationInfo> generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static long PathSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length)
            : 0;
    }
}
