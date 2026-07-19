using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Raft;

namespace SlimVector.Application.Admission;

public enum AdmissionOperation
{
    Read,
    Write,
    Admin,
}

public enum AdmissionRejectionKind
{
    None,
    Contractual,
    Congestion,
}

public sealed record AdmissionRequest(
    string ClientId,
    string? Collection,
    AdmissionOperation Operation,
    double Cost = 1);

public sealed record AdmissionDecision(
    bool Allowed,
    AdmissionRejectionKind RejectionKind,
    string? Scope,
    TimeSpan RetryAfter,
    double AdaptiveRateRatio,
    double Pressure);

public sealed record AdmissionMetricsSnapshot(
    long Accepted,
    long ContractualRejections,
    long CongestionRejections,
    double AdaptiveRateRatio,
    double Pressure,
    string PressureReason);

public interface IAdmissionController
{
    AdmissionDecision TryAcquire(AdmissionRequest request);

    AdmissionMetricsSnapshot GetSnapshot();
}

public sealed class AdaptiveAdmissionController : IAdmissionController
{
    private readonly object _sync = new();
    private readonly RateLimitOptions _options;
    private readonly IWriteScheduler _scheduler;
    private readonly IConsensusCoordinator _consensus;
    private readonly OperationalMetrics _operations;
    private readonly TimeProvider _timeProvider;
    private readonly TokenBucket _globalShared;
    private readonly TokenBucket _globalReadReserve;
    private readonly TokenBucket _globalWriteReserve;
    private readonly TokenBucket _adaptiveShared;
    private readonly TokenBucket _adaptiveReadReserve;
    private readonly TokenBucket _adaptiveWriteReserve;
    private readonly TokenBucket _read;
    private readonly TokenBucket _write;
    private readonly TokenBucket _admin;
    private readonly Dictionary<string, TokenBucket> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenBucket> _collections = new(StringComparer.Ordinal);
    private long _lastCleanupTimestamp;
    private long _lastPressureTimestamp;
    private long _accepted;
    private long _contractualRejections;
    private long _congestionRejections;
    private double _adaptiveRateRatio = 1;
    private double _pressure;
    private string _pressureReason = "none";

    public AdaptiveAdmissionController(
        IOptions<RateLimitOptions> options,
        IWriteScheduler scheduler,
        IConsensusCoordinator consensus,
        OperationalMetrics operations,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _scheduler = scheduler;
        _consensus = consensus;
        _operations = operations;
        _timeProvider = timeProvider;
        long now = timeProvider.GetTimestamp();
        double sharedFraction = 1 - _options.ReservedReadFraction - _options.ReservedWriteFraction;
        _globalShared = CreateBucket(_options.Global, sharedFraction, now);
        _globalReadReserve = CreateBucket(_options.Global, _options.ReservedReadFraction, now);
        _globalWriteReserve = CreateBucket(_options.Global, _options.ReservedWriteFraction, now);
        _adaptiveShared = CreateBucket(_options.Global, sharedFraction, now);
        _adaptiveReadReserve = CreateBucket(_options.Global, _options.ReservedReadFraction, now);
        _adaptiveWriteReserve = CreateBucket(_options.Global, _options.ReservedWriteFraction, now);
        _read = CreateBucket(_options.Read, now);
        _write = CreateBucket(_options.Write, now);
        _admin = CreateBucket(_options.Admin, now);
        _lastCleanupTimestamp = now;
        _lastPressureTimestamp = now;
    }

    public AdmissionDecision TryAcquire(AdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientId);
        if (!double.IsFinite(request.Cost) || request.Cost <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (!_options.Enabled)
        {
            Interlocked.Increment(ref _accepted);
            return new AdmissionDecision(true, AdmissionRejectionKind.None, null, TimeSpan.Zero, 1, 0);
        }

        WriteSchedulerSnapshot writes = _scheduler.GetSnapshot();
        OperationalMetricsSnapshot operations = _operations.GetSnapshot();
        IReadOnlyList<RaftGroupStatus> raft = _consensus.GetStatuses();
        lock (_sync)
        {
            long now = _timeProvider.GetTimestamp();
            CleanupIdleBuckets(now);
            UpdateAdaptiveRate(now, writes, operations, raft);
            TokenBucket client = GetBucket(_clients, request.ClientId, _options.Client, now);
            TokenBucket? collection = string.IsNullOrWhiteSpace(request.Collection)
                ? null
                : GetBucket(_collections, request.Collection, _options.Collection, now);
            TokenBucket operation = request.Operation switch
            {
                AdmissionOperation.Read => _read,
                AdmissionOperation.Write => _write,
                AdmissionOperation.Admin => _admin,
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
            (TokenBucket Bucket, string Scope)[] contractual = collection is null
                ? [(client, "client"), (operation, OperationScope(request.Operation))]
                : [(client, "client"), (collection, "collection"), (operation, OperationScope(request.Operation))];
            TimeSpan retryAfter = TimeSpan.Zero;
            string? rejectedScope = null;
            if (request.Operation is not AdmissionOperation.Admin)
            {
                TimeSpan globalRetry = GetCombinedRetryAfter(
                    _globalShared,
                    Reserve(_globalReadReserve, _globalWriteReserve, request.Operation),
                    request.Cost,
                    rateRatio: 1,
                    now);
                if (globalRetry > TimeSpan.Zero)
                {
                    retryAfter = globalRetry;
                    rejectedScope = "global";
                }
            }

            foreach ((TokenBucket bucket, string scope) in contractual)
            {
                TimeSpan retry = bucket.GetRetryAfter(request.Cost, rateRatio: 1, now, _timeProvider);
                if (retry > retryAfter)
                {
                    retryAfter = retry;
                    rejectedScope = scope;
                }
            }

            if (rejectedScope is not null)
            {
                _contractualRejections++;
                return new AdmissionDecision(
                    false,
                    AdmissionRejectionKind.Contractual,
                    rejectedScope,
                    retryAfter,
                    _adaptiveRateRatio,
                    _pressure);
            }

            TimeSpan congestionRetry = request.Operation is AdmissionOperation.Admin
                ? TimeSpan.Zero
                : GetCombinedRetryAfter(
                    _adaptiveShared,
                    Reserve(_adaptiveReadReserve, _adaptiveWriteReserve, request.Operation),
                    request.Cost,
                    _adaptiveRateRatio,
                    now);
            if (congestionRetry > TimeSpan.Zero)
            {
                _congestionRejections++;
                return new AdmissionDecision(
                    false,
                    AdmissionRejectionKind.Congestion,
                    _pressureReason,
                    congestionRetry,
                    _adaptiveRateRatio,
                    _pressure);
            }

            foreach ((TokenBucket bucket, _) in contractual)
            {
                bucket.Consume(request.Cost, rateRatio: 1, now, _timeProvider);
            }

            if (request.Operation is not AdmissionOperation.Admin)
            {
                ConsumeCombined(
                    _globalShared,
                    Reserve(_globalReadReserve, _globalWriteReserve, request.Operation),
                    request.Cost,
                    rateRatio: 1,
                    now);
                ConsumeCombined(
                    _adaptiveShared,
                    Reserve(_adaptiveReadReserve, _adaptiveWriteReserve, request.Operation),
                    request.Cost,
                    _adaptiveRateRatio,
                    now);
            }

            _accepted++;
            return new AdmissionDecision(
                true,
                AdmissionRejectionKind.None,
                null,
                TimeSpan.Zero,
                _adaptiveRateRatio,
                _pressure);
        }
    }

    public AdmissionMetricsSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new AdmissionMetricsSnapshot(
                _accepted,
                _contractualRejections,
                _congestionRejections,
                _adaptiveRateRatio,
                _pressure,
                _pressureReason);
        }
    }

    private void UpdateAdaptiveRate(
        long now,
        WriteSchedulerSnapshot writes,
        OperationalMetricsSnapshot operations,
        IReadOnlyList<RaftGroupStatus> raft)
    {
        double queuePressure = Math.Clamp(
            (double)writes.QueueDepth / Math.Max(1, _options.Global.BurstCapacity),
            0,
            1);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        double memoryPressure = memory.TotalAvailableMemoryBytes <= 0
            ? 0
            : Math.Clamp((double)GC.GetTotalMemory(forceFullCollection: false) / memory.TotalAvailableMemoryBytes, 0, 1);
        double replicationPressure = raft.Count == 0
            ? 0
            : Math.Clamp(raft.Max(static status => Math.Max(0, status.LastCommittedIndex - status.LastAppliedIndex)) / 1_000D, 0, 1);
        double rejectionPressure = writes.TotalWrites == 0
            ? 0
            : Math.Clamp((double)writes.RejectedWrites / writes.TotalWrites * 10, 0, 1);
        double errorPressure = operations.Searches == 0
            ? 0
            : Math.Clamp((double)operations.SearchFailures / operations.Searches * 10, 0, 1);
        (double Value, string Reason)[] signals =
        [
            (queuePressure, "write-queue"),
            (memoryPressure, "memory"),
            (replicationPressure, "raft-lag"),
            (rejectionPressure, "write-errors"),
            (errorPressure, "query-errors"),
            (_consensus.IsReady ? 0 : 1, "quorum"),
        ];
        (double Value, string Reason) strongest = signals.OrderByDescending(static signal => signal.Value).First();
        _pressure = strongest.Value;
        _pressureReason = strongest.Value <= 0 ? "none" : strongest.Reason;
        double target = Math.Clamp(1 - _pressure, _options.MinimumAdaptiveRateRatio, 1);
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastPressureTimestamp, now);
        _lastPressureTimestamp = now;
        if (target < _adaptiveRateRatio)
        {
            _adaptiveRateRatio = target;
        }
        else
        {
            double recovery = Math.Clamp(elapsed.TotalSeconds / _options.RecoveryWindow.TotalSeconds, 0, 1);
            _adaptiveRateRatio += (target - _adaptiveRateRatio) * recovery;
        }
    }

    private void CleanupIdleBuckets(long now)
    {
        if (_timeProvider.GetElapsedTime(_lastCleanupTimestamp, now) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastCleanupTimestamp = now;
        RemoveIdle(_clients, now);
        RemoveIdle(_collections, now);
    }

    private void RemoveIdle(Dictionary<string, TokenBucket> buckets, long now)
    {
        foreach (string key in buckets
                     .Where(pair => _timeProvider.GetElapsedTime(pair.Value.LastAccessTimestamp, now) >= _options.IdleBucketExpiration)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            buckets.Remove(key);
        }
    }

    private static TokenBucket GetBucket(
        Dictionary<string, TokenBucket> buckets,
        string key,
        TokenBucketOptions options,
        long now)
    {
        if (!buckets.TryGetValue(key, out TokenBucket? bucket))
        {
            bucket = CreateBucket(options, now);
            buckets.Add(key, bucket);
        }

        return bucket;
    }

    private static TokenBucket CreateBucket(TokenBucketOptions options, long now) =>
        new(options.TokensPerSecond, options.BurstCapacity, now);

    private static TokenBucket CreateBucket(TokenBucketOptions options, double fraction, long now) =>
        new(options.TokensPerSecond * fraction, options.BurstCapacity * fraction, now);

    private static TokenBucket Reserve(TokenBucket read, TokenBucket write, AdmissionOperation operation) => operation switch
    {
        AdmissionOperation.Read => read,
        AdmissionOperation.Write => write,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private TimeSpan GetCombinedRetryAfter(
        TokenBucket shared,
        TokenBucket reserved,
        double cost,
        double rateRatio,
        long now)
    {
        double available = shared.GetAvailable(rateRatio, now, _timeProvider) +
            reserved.GetAvailable(rateRatio, now, _timeProvider);
        if (available >= cost)
        {
            return TimeSpan.Zero;
        }

        double refillRate = (shared.TokensPerSecond + reserved.TokensPerSecond) * rateRatio;
        return TimeSpan.FromSeconds((cost - available) / Math.Max(double.Epsilon, refillRate));
    }

    private void ConsumeCombined(
        TokenBucket shared,
        TokenBucket reserved,
        double cost,
        double rateRatio,
        long now)
    {
        double sharedAvailable = shared.GetAvailable(rateRatio, now, _timeProvider);
        double fromShared = Math.Min(cost, sharedAvailable);
        shared.ConsumeAvailable(fromShared);
        reserved.GetAvailable(rateRatio, now, _timeProvider);
        reserved.ConsumeAvailable(cost - fromShared);
    }

    private static string OperationScope(AdmissionOperation operation) => operation switch
    {
        AdmissionOperation.Read => "operation:read",
        AdmissionOperation.Write => "operation:write",
        AdmissionOperation.Admin => "operation:admin",
        _ => "operation",
    };
}

internal sealed class TokenBucket
{
    private readonly double _tokensPerSecond;
    private readonly double _capacity;
    private double _tokens;
    private long _lastRefillTimestamp;

    public TokenBucket(double tokensPerSecond, double capacity, long now)
    {
        _tokensPerSecond = tokensPerSecond;
        _capacity = capacity;
        _tokens = capacity;
        _lastRefillTimestamp = now;
        LastAccessTimestamp = now;
    }

    public long LastAccessTimestamp { get; private set; }

    public double TokensPerSecond => _tokensPerSecond;

    public TimeSpan GetRetryAfter(double cost, double rateRatio, long now, TimeProvider timeProvider)
    {
        Refill(rateRatio, now, timeProvider);
        if (_tokens >= cost)
        {
            return TimeSpan.Zero;
        }

        double rate = _tokensPerSecond * rateRatio;
        return TimeSpan.FromSeconds((cost - _tokens) / Math.Max(double.Epsilon, rate));
    }

    public void Consume(double cost, double rateRatio, long now, TimeProvider timeProvider)
    {
        Refill(rateRatio, now, timeProvider);
        _tokens = Math.Max(0, _tokens - cost);
    }

    public double GetAvailable(double rateRatio, long now, TimeProvider timeProvider)
    {
        Refill(rateRatio, now, timeProvider);
        return _tokens;
    }

    public void ConsumeAvailable(double cost) => _tokens = Math.Max(0, _tokens - cost);

    private void Refill(double rateRatio, long now, TimeProvider timeProvider)
    {
        TimeSpan elapsed = timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
        _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _tokensPerSecond * rateRatio);
        _lastRefillTimestamp = now;
        LastAccessTimestamp = now;
    }
}
