using System.Net;
using Microsoft.Extensions.Options;
using SlimVector.Domain;

namespace SlimVector.Application.Configuration;

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add("Storage:Path is required.");
        }

        if (options.MaximumSegmentsBeforeCompaction < 2)
        {
            failures.Add("Storage:MaximumSegmentsBeforeCompaction must be at least 2.");
        }

        return Result(failures);
    }

    private static ValidateOptionsResult Result(List<string> failures) => failures.Count == 0
        ? ValidateOptionsResult.Success
        : ValidateOptionsResult.Fail(failures);
}

internal sealed class CollectionsOptionsValidator : IValidateOptions<CollectionsOptions>
{
    public ValidateOptionsResult Validate(string? name, CollectionsOptions options) =>
        ValidationHelpers.PositiveDurationsAndCounts(
            (options.IdleTimeout, "Collections:IdleTimeout"),
            (options.EvictionSweepInterval, "Collections:EvictionSweepInterval"),
            (options.MaximumOpenCollections, "Collections:MaximumOpenCollections"));
}

internal sealed class VectorIndexOptionsValidator : IValidateOptions<VectorIndexOptions>
{
    public ValidateOptionsResult Validate(string? name, VectorIndexOptions options)
    {
        List<string> failures = [];
        ValidationHelpers.RequirePositive(options.AutoHnswThreshold, "VectorIndex:AutoHnswThreshold", failures);
        ValidationHelpers.RequireRange(options.HybridCandidateMultiplier, 1, 100, "VectorIndex:HybridCandidateMultiplier", failures);
        ValidationHelpers.RequireRange(options.MaximumSearchLimit, 1, 100_000, "VectorIndex:MaximumSearchLimit", failures);
        return ValidationHelpers.Result(failures);
    }
}

internal sealed class AutoIndexOptionsValidator : IValidateOptions<AutoIndexOptions>
{
    public ValidateOptionsResult Validate(string? name, AutoIndexOptions options)
    {
        List<string> failures = [];
        if (options.AllowedIndexes.Length == 0 || options.AllowedIndexes.Contains(VectorIndexKind.Auto) ||
            options.AllowedIndexes.Any(static kind => !Enum.IsDefined(kind)))
        {
            failures.Add("AutoIndex:AllowedIndexes must contain concrete index kinds.");
        }

        if (options.AssessmentInterval <= TimeSpan.Zero || options.MinimumMigrationInterval < options.AssessmentInterval)
        {
            failures.Add("AutoIndex migration interval must be at least the positive assessment interval.");
        }

        if (options.HysteresisRatio is < 0 or > 1 || options.MinimumRecall is <= 0 or > 1 ||
            options.MinimumPerformanceGain is < 0 or > 1 || options.DiskAnnMemoryRatio is <= 0 or > 1 ||
            options.HighUpdateRatio is < 0 or > 1)
        {
            failures.Add("AutoIndex ratios must be within their documented zero-to-one ranges.");
        }

        if (options.ValidationSampleSize < 1 || options.HnswMinimumVectors < 1 ||
            options.IvfMinimumVectors < options.HnswMinimumVectors || options.PqMinimumVectors < options.IvfMinimumVectors)
        {
            failures.Add("AutoIndex validation size and ascending vector-count thresholds are invalid.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class HnswOptionsValidator : IValidateOptions<HnswOptions>
{
    public ValidateOptionsResult Validate(string? name, HnswOptions options) => ValidateIndex(new VectorIndexConfiguration
    {
        HnswM = options.M,
        HnswEfConstruction = options.EfConstruction,
        HnswEfSearch = options.EfSearch,
    });

    private static ValidateOptionsResult ValidateIndex(VectorIndexConfiguration configuration)
    {
        try
        {
            DomainValidation.ValidateVectorIndex(configuration);
            return ValidateOptionsResult.Success;
        }
        catch (DomainException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}

internal sealed class IvfOptionsValidator : IValidateOptions<IvfOptions>
{
    public ValidateOptionsResult Validate(string? name, IvfOptions options)
    {
        List<string> failures = [];
        if (options.ListCount is < 1 or > 65_536 || options.ProbeCount < 1 ||
            options.ProbeCount > options.ListCount || options.TrainingIterations is < 1 or > 1_000)
        {
            failures.Add("IVF list count, probe count, or training iterations are invalid.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class PqOptionsValidator : IValidateOptions<PqOptions>
{
    public ValidateOptionsResult Validate(string? name, PqOptions options)
    {
        List<string> failures = [];
        if (options.SubvectorCount is < 1 or > 1_024 || options.CentroidCount is < 2 or > 256 ||
            options.TrainingIterations is < 1 or > 1_000 || options.RerankCandidateMultiplier is < 1 or > 100)
        {
            failures.Add("PQ subvector, centroid, training, or re-rank configuration is invalid.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class DiskAnnOptionsValidator : IValidateOptions<DiskAnnOptions>
{
    public ValidateOptionsResult Validate(string? name, DiskAnnOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add("DiskAnn:Path is required.");
        }

        if (options.MaxDegree is < 2 or > 512 || options.SearchListSize < options.MaxDegree ||
            options.SearchListSize > 16_384 || options.BeamWidth is < 1 or > 256 || options.DeltaThreshold < 1)
        {
            failures.Add("DiskANN graph and delta configuration is invalid.");
        }

        if (options.PageSize < 512 || !System.Numerics.BitOperations.IsPow2((uint)options.PageSize) ||
            options.CachePages < 1 || options.RetainedGenerations < 2)
        {
            failures.Add("DiskANN page size must be a power of two and cache/generation retention must be positive.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class TextIndexOptionsValidator : IValidateOptions<TextIndexOptions>
{
    public ValidateOptionsResult Validate(string? name, TextIndexOptions options)
    {
        List<string> failures = [];
        if (options.Bm25K1 <= 0)
        {
            failures.Add("TextIndex:Bm25K1 must be positive.");
        }

        if (options.Bm25B is < 0 or > 1)
        {
            failures.Add("TextIndex:Bm25B must be between 0 and 1.");
        }

        ValidationHelpers.RequirePositive(options.MaximumTermsPerDocument, "TextIndex:MaximumTermsPerDocument", failures);
        return ValidationHelpers.Result(failures);
    }
}

internal sealed class MetadataIndexOptionsValidator : IValidateOptions<MetadataIndexOptions>
{
    public ValidateOptionsResult Validate(string? name, MetadataIndexOptions options) =>
        options.MaximumFilterDepth is >= 1 and <= 256
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("MetadataIndex:MaximumFilterDepth must be between 1 and 256.");
}

public sealed class RaftOptionsValidator : IValidateOptions<RaftOptions>
{
    public ValidateOptionsResult Validate(string? name, RaftOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.NodeId))
        {
            failures.Add("Raft:NodeId is required.");
        }

        if (!TryParseEndpoint(options.PublicEndpoint, out IPEndPoint? localEndpoint))
        {
            failures.Add("Raft:PublicEndpoint must contain an IP address and TCP port.");
        }

        if (!Uri.TryCreate(options.PublicApiEndpoint, UriKind.Absolute, out Uri? localApiEndpoint) ||
            localApiEndpoint.Scheme is not "http" and not "https")
        {
            failures.Add("Raft:PublicApiEndpoint must be an absolute HTTP(S) URI.");
        }

        if (options.Mode == ExecutionMode.Cluster && options.JoinExistingCluster && options.Members.Length != 0)
        {
            failures.Add("Raft:Members must be empty when Raft:JoinExistingCluster is true; membership is installed through Raft consensus.");
        }
        else if (options.Mode == ExecutionMode.Cluster && options.JoinExistingCluster && options.MemberApiEndpoints.Length != 0)
        {
            failures.Add("Raft:MemberApiEndpoints must be empty when Raft:JoinExistingCluster is true.");
        }
        else if (options.Mode == ExecutionMode.Cluster && !options.JoinExistingCluster && options.Members.Length < 3)
        {
            failures.Add("Raft:Members must contain at least three members in cluster mode.");
        }

        else if (options.Mode == ExecutionMode.Cluster && !options.JoinExistingCluster)
        {
            IPEndPoint[] members = options.Members
                .Select(member => TryParseEndpoint(member, out IPEndPoint? endpoint) ? endpoint : null)
                .Where(static endpoint => endpoint is not null)
                .Cast<IPEndPoint>()
                .ToArray();
            if (members.Length != options.Members.Length)
            {
                failures.Add("Every Raft:Members entry must contain an IP address and TCP port.");
            }
            else if (localEndpoint is not null && !members.Contains(localEndpoint))
            {
                failures.Add("Raft:Members must contain Raft:PublicEndpoint in cluster mode.");
            }
            else if (members.Distinct().Count() != members.Length)
            {
                failures.Add("Raft:Members entries must be unique.");
            }


            if (options.MemberApiEndpoints.Length != options.Members.Length ||
                options.MemberApiEndpoints.Any(endpoint =>
                    !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme is not "http" and not "https"))
            {
                failures.Add("Raft:MemberApiEndpoints must contain one absolute HTTP(S) URI per Raft member.");
            }
            else if (localApiEndpoint is not null && !options.MemberApiEndpoints.Contains(
                         localApiEndpoint.AbsoluteUri.TrimEnd('/'),
                         StringComparer.OrdinalIgnoreCase) &&
                     !options.MemberApiEndpoints.Contains(options.PublicApiEndpoint, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add("Raft:MemberApiEndpoints must contain Raft:PublicApiEndpoint in cluster mode.");
            }
        }

        if (options.DataGroupCount is < 1 or > 128)
        {
            failures.Add("Raft:DataGroupCount must be between 1 and 128.");
        }

        if (localEndpoint is not null && localEndpoint.Port + options.DataGroupCount > IPEndPoint.MaxPort)
        {
            failures.Add("Raft:PublicEndpoint does not leave enough consecutive ports for all groups.");
        }

        if (options.SnapshotEveryEntries < 1 || options.TransmissionBlockSize < 1_024)
        {
            failures.Add("Raft snapshot and transmission settings must be positive.");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero || options.ElectionTimeout <= options.HeartbeatInterval * 2)
        {
            failures.Add("Raft:ElectionTimeout must exceed twice Raft:HeartbeatInterval.");
        }

        return ValidationHelpers.Result(failures);
    }

    public static bool TryParseEndpoint(string value, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (IPEndPoint.TryParse(value, out IPEndPoint? direct))
        {
            endpoint = direct;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort ||
            !IPAddress.TryParse(uri.Host, out IPAddress? address))
        {
            return false;
        }

        endpoint = new IPEndPoint(address, uri.Port);
        return true;
    }
}

internal sealed class ClusterMembershipOptionsValidator : IValidateOptions<ClusterMembershipOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusterMembershipOptions options)
    {
        List<string> failures = [];
        if (options.WarmupRounds < 1 || options.MaximumCatchUpLagEntries < 0 || options.MinimumVotingMembers < 1)
        {
            failures.Add("ClusterMembership round, lag, and minimum-voter settings are invalid.");
        }

        if (options.CatchUpTimeout <= TimeSpan.Zero || options.OperationTimeout <= TimeSpan.Zero ||
            options.OperationTimeout > options.CatchUpTimeout)
        {
            failures.Add("ClusterMembership timeouts must be positive and operation timeout may not exceed catch-up timeout.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class RebalancingOptionsValidator : IValidateOptions<RebalancingOptions>
{
    public ValidateOptionsResult Validate(string? name, RebalancingOptions options)
    {
        List<string> failures = [];
        if (options.MaximumConcurrentMoves is < 1 or > 64 || options.ReconcileInterval <= TimeSpan.Zero ||
            options.Cooldown < TimeSpan.Zero || options.MinimumImprovementRatio is < 0 or > 1)
        {
            failures.Add("Rebalancing move limits, intervals, cooldown, or improvement ratio are invalid.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class GeoReplicationOptionsValidator : IValidateOptions<GeoReplicationOptions>
{
    public ValidateOptionsResult Validate(string? name, GeoReplicationOptions options)
    {
        List<string> failures = [];
        if (options.Enabled && !Uri.TryCreate(options.SecondaryEndpoint, UriKind.Absolute, out _))
        {
            failures.Add("GeoReplication:SecondaryEndpoint must be an absolute URI when replication is enabled.");
        }

        if ((options.Enabled || options.AcceptIncoming) && options.SharedSecret.Length < 32)
        {
            failures.Add("GeoReplication:SharedSecret must contain at least 32 characters when replication is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.OriginId) || string.IsNullOrWhiteSpace(options.StatePath))
        {
            failures.Add("GeoReplication origin id and state path are required.");
        }

        if (options.RetryMinimumDelay <= TimeSpan.Zero || options.RetryMaximumDelay < options.RetryMinimumDelay)
        {
            failures.Add("GeoReplication retry delays are inconsistent.");
        }

        ValidationHelpers.RequirePositive(options.QueueCapacity, "GeoReplication:QueueCapacity", failures);
        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("GeoReplication:RequestTimeout must be positive.");
        }
        return ValidationHelpers.Result(failures);
    }
}

internal sealed class AdaptiveBatchingOptionsValidator : IValidateOptions<AdaptiveBatchingOptions>
{
    public ValidateOptionsResult Validate(string? name, AdaptiveBatchingOptions options)
    {
        List<string> failures = [];
        if (options.MinimumBatchSize < 1 || options.MaximumBatchSize < options.MinimumBatchSize)
        {
            failures.Add("AdaptiveBatching batch-size bounds are inconsistent.");
        }

        if (options.MinimumWindow < TimeSpan.Zero || options.MaximumWindow < options.MinimumWindow)
        {
            failures.Add("AdaptiveBatching window bounds are inconsistent.");
        }

        if (options.MaximumBatchBytes < 1)
        {
            failures.Add("AdaptiveBatching:MaximumBatchBytes must be positive.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class BackpressureOptionsValidator : IValidateOptions<BackpressureOptions>
{
    public ValidateOptionsResult Validate(string? name, BackpressureOptions options)
    {
        List<string> failures = [];
        ValidationHelpers.RequirePositive(options.GlobalQueueCapacity, "Backpressure:GlobalQueueCapacity", failures);
        ValidationHelpers.RequirePositive(options.PerCollectionQueueCapacity, "Backpressure:PerCollectionQueueCapacity", failures);
        ValidationHelpers.RequirePositive(options.PerShardQueueCapacity, "Backpressure:PerShardQueueCapacity", failures);
        ValidationHelpers.RequirePositive(options.PerClientQueueCapacity, "Backpressure:PerClientQueueCapacity", failures);
        ValidationHelpers.RequirePositive(options.MaximumConcurrentWrites, "Backpressure:MaximumConcurrentWrites", failures);
        if (options.EnqueueTimeout < TimeSpan.Zero)
        {
            failures.Add("Backpressure:EnqueueTimeout may not be negative.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        List<string> failures = [];
        ValidateBucket(options.Global, "Global", failures);
        ValidateBucket(options.Client, "Client", failures);
        ValidateBucket(options.Collection, "Collection", failures);
        ValidateBucket(options.Read, "Read", failures);
        ValidateBucket(options.Write, "Write", failures);
        ValidateBucket(options.Admin, "Admin", failures);
        if (options.ReservedReadFraction is < 0 or >= 1 || options.ReservedWriteFraction is < 0 or >= 1 ||
            options.ReservedReadFraction + options.ReservedWriteFraction >= 1)
        {
            failures.Add("RateLimit reserved fractions must be non-negative and sum to less than one.");
        }

        if (options.IdleBucketExpiration <= TimeSpan.Zero || options.RecoveryWindow <= TimeSpan.Zero ||
            options.MinimumAdaptiveRateRatio is <= 0 or > 1)
        {
            failures.Add("RateLimit expiration, recovery, and minimum adaptive ratio are invalid.");
        }

        return ValidationHelpers.Result(failures);
    }

    private static void ValidateBucket(TokenBucketOptions bucket, string name, List<string> failures)
    {
        if (!double.IsFinite(bucket.TokensPerSecond) || bucket.TokensPerSecond <= 0 ||
            !double.IsFinite(bucket.BurstCapacity) || bucket.BurstCapacity < bucket.TokensPerSecond)
        {
            failures.Add($"RateLimit:{name} token rate must be positive and burst capacity must be at least the rate.");
        }
    }
}

internal sealed class BackupOptionsValidator : IValidateOptions<BackupOptions>
{
    public ValidateOptionsResult Validate(string? name, BackupOptions options)
    {
        List<string> failures = [];
        if (options.Provider == BackupProvider.FileSystem && string.IsNullOrWhiteSpace(options.Path))
        {
            failures.Add("Backup:Path is required for filesystem backups.");
        }

        if (options.Provider == BackupProvider.S3 &&
            (string.IsNullOrWhiteSpace(options.S3Endpoint) || string.IsNullOrWhiteSpace(options.S3Bucket) ||
             string.IsNullOrWhiteSpace(options.S3AccessKey) || string.IsNullOrWhiteSpace(options.S3SecretKey)))
        {
            failures.Add("Backup S3 endpoint, bucket, access key and secret key are required for S3 backups.");
        }

        else if (options.Provider == BackupProvider.S3 &&
            !Uri.TryCreate(options.S3Endpoint, UriKind.Absolute, out _))
        {
            failures.Add("Backup:S3Endpoint must be an absolute URI.");
        }

        if (options.Encrypt && !IsValidEncryptionKey(options.EncryptionKey))
        {
            failures.Add("Backup:EncryptionKey must be a base64-encoded 256-bit key when encryption is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Prefix) || options.Prefix.Contains("..", StringComparison.Ordinal))
        {
            failures.Add("Backup:Prefix must be non-empty and may not contain '..'.");
        }

        if (options.Interval <= TimeSpan.Zero || options.RetentionCount < 1)
        {
            failures.Add("Backup interval and retention must be positive.");
        }

        return ValidationHelpers.Result(failures);
    }

    private static bool IsValidEncryptionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.RoutePrefix) || !options.RoutePrefix.StartsWith('/'))
        {
            failures.Add("Api:RoutePrefix must start with '/'.");
        }

        ValidationHelpers.RequirePositive(options.MaximumBatchSize, "Api:MaximumBatchSize", failures);
        if (options.MaximumRequestBodyBytes < 1 || options.RequestTimeout <= TimeSpan.Zero)
        {
            failures.Add("Api request limits must be positive.");
        }

        if (options.AdminEndpointsEnabled && options.AdminApiKey.Length < 32)
        {
            failures.Add("Api:AdminApiKey must contain at least 32 characters when admin endpoints are enabled.");
        }

        return ValidationHelpers.Result(failures);
    }
}

internal sealed class ObservabilityOptionsValidator : IValidateOptions<ObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options) =>
        !string.IsNullOrWhiteSpace(options.ServiceName) && options.ServiceName.Length <= 128 &&
        options.ServiceName.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        options.SlowQueryThreshold > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Observability service name must use letters, digits, '.', '_' or '-', and the slow-query threshold must be positive.");
}

internal static class ValidationHelpers
{
    public static ValidateOptionsResult PositiveDurationsAndCounts(
        (TimeSpan Value, string Name) first,
        (TimeSpan Value, string Name) second,
        (int Value, string Name) third)
    {
        List<string> failures = [];
        if (first.Value <= TimeSpan.Zero)
        {
            failures.Add($"{first.Name} must be positive.");
        }

        if (second.Value <= TimeSpan.Zero)
        {
            failures.Add($"{second.Name} must be positive.");
        }

        RequirePositive(third.Value, third.Name, failures);
        return Result(failures);
    }

    public static void RequirePositive(int value, string name, List<string> failures)
    {
        if (value < 1)
        {
            failures.Add($"{name} must be positive.");
        }
    }

    public static void RequireRange(int value, int minimum, int maximum, string name, List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }

    public static ValidateOptionsResult Result(List<string> failures) => failures.Count == 0
        ? ValidateOptionsResult.Success
        : ValidateOptionsResult.Fail(failures);
}
