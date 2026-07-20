using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Routing;

public interface ILocalCatalogSnapshotExchange
{
    ValueTask<byte[]> CaptureAsync(bool requireLeaderBarrier, CancellationToken cancellationToken = default);

    ValueTask InstallAsync(
        ReadOnlyMemory<byte> snapshot,
        string? signature,
        CancellationToken cancellationToken = default);

    string? Sign(ReadOnlySpan<byte> snapshot);

    bool Validate(ReadOnlySpan<byte> snapshot, string? signature);
}

public interface ICatalogCacheSynchronizer
{
    ValueTask RefreshAsync(CancellationToken cancellationToken = default);

    ValueTask SeedNodeAsync(string internalEndpoint, CancellationToken cancellationToken = default);
}

public sealed class LocalCatalogSnapshotExchange : ILocalCatalogSnapshotExchange
{
    private readonly IConsensusCoordinator _localConsensus;
    private readonly StorageRaftCommandApplier _applier;
    private readonly byte[]? _sharedSecret;

    public LocalCatalogSnapshotExchange(
        IConsensusCoordinator localConsensus,
        StorageRaftCommandApplier applier,
        IOptions<ApiOptions> apiOptions)
    {
        ArgumentNullException.ThrowIfNull(localConsensus);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(apiOptions);
        _localConsensus = localConsensus;
        _applier = applier;
        _sharedSecret = string.IsNullOrEmpty(apiOptions.Value.AdminApiKey)
            ? null
            : Encoding.UTF8.GetBytes(apiOptions.Value.AdminApiKey);
    }

    public async ValueTask<byte[]> CaptureAsync(
        bool requireLeaderBarrier,
        CancellationToken cancellationToken = default)
    {
        if (requireLeaderBarrier)
        {
            await _localConsensus.ApplyReadBarrierAsync(
                collectionId: null,
                ReadConsistency.Leader,
                cancellationToken).ConfigureAwait(false);
        }

        return await _applier.CaptureSnapshotAsync(MultiRaftNode.CatalogGroupId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask InstallAsync(
        ReadOnlyMemory<byte> snapshot,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        if (!Validate(snapshot.Span, signature))
        {
            throw new UnauthorizedAccessException("The catalog snapshot signature is invalid.");
        }

        await _applier.RestoreSnapshotAsync(MultiRaftNode.CatalogGroupId, snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    public string? Sign(ReadOnlySpan<byte> snapshot) => _sharedSecret is null
        ? null
        : Convert.ToHexStringLower(HMACSHA256.HashData(_sharedSecret, snapshot));

    public bool Validate(ReadOnlySpan<byte> snapshot, string? signature)
    {
        if (_sharedSecret is null)
        {
            return true;
        }

        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature ?? string.Empty);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected = HMACSHA256.HashData(_sharedSecret, snapshot);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed partial class CatalogCacheSynchronizer : BackgroundService, ICatalogCacheSynchronizer
{
    private const string SnapshotPath = "/internal/catalog/snapshot";
    private readonly HttpClient _httpClient;
    private readonly IClusterTopologyStore _topologyStore;
    private readonly ILocalCatalogSnapshotExchange _exchange;
    private readonly RaftOptions _raftOptions;
    private readonly TimeSpan _interval;
    private readonly ILogger<CatalogCacheSynchronizer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogCacheSynchronizer(
        HttpClient httpClient,
        IClusterTopologyStore topologyStore,
        ILocalCatalogSnapshotExchange exchange,
        IOptions<RaftOptions> raftOptions,
        IOptions<RebalancingOptions> rebalancingOptions,
        ILogger<CatalogCacheSynchronizer> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(topologyStore);
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(raftOptions);
        ArgumentNullException.ThrowIfNull(rebalancingOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _topologyStore = topologyStore;
        _exchange = exchange;
        _raftOptions = raftOptions.Value;
        _interval = rebalancingOptions.Value.ReconcileInterval;
        _logger = logger;
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_raftOptions.Mode == ExecutionMode.SingleNode)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology topology = await _topologyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            string[] catalogNodeIds = ResolveCatalogNodeIds(topology);
            if (catalogNodeIds.Contains(_raftOptions.NodeId, StringComparer.Ordinal))
            {
                return;
            }

            Dictionary<string, ClusterNodeDescriptor> nodes = topology.Nodes.ToDictionary(
                static node => node.NodeId,
                StringComparer.Ordinal);
            Exception? lastFailure = null;
            foreach (string nodeId in catalogNodeIds)
            {
                if (!nodes.TryGetValue(nodeId, out ClusterNodeDescriptor? node) ||
                    node.State == ClusterNodeState.Removed)
                {
                    continue;
                }

                try
                {
                    using HttpRequestMessage request = new(
                        HttpMethod.Get,
                        node.InternalEndpoint.TrimEnd('/') + SnapshotPath)
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                    };
                    using HttpResponseMessage response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    byte[] snapshot = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    string? signature = response.Headers.TryGetValues("X-SlimVector-Signature", out IEnumerable<string>? values)
                        ? values.FirstOrDefault()
                        : null;
                    await _exchange.InstallAsync(snapshot, signature, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or
                                                   TaskCanceledException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                }
            }

            throw new ConsensusUnavailableException(
                MultiRaftNode.CatalogGroupId,
                ConsensusFailureKind.QuorumUnavailable,
                leaderEndpoint: null,
                lastFailure ?? new InvalidOperationException("No catalog cache source is reachable."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SeedNodeAsync(
        string internalEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalEndpoint);
        byte[] snapshot;
        try
        {
            snapshot = await _exchange.CaptureAsync(
                requireLeaderBarrier: IsLocalCatalogMember(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConsensusUnavailableException exception) when (exception.Kind == ConsensusFailureKind.NotLeader)
        {
            snapshot = await DownloadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, internalEndpoint.TrimEnd('/') + SnapshotPath)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new ByteArrayContent(snapshot),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");
        string? signature = _exchange.Sign(snapshot);
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-SlimVector-Signature", signature);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_raftOptions.Mode == ExecutionMode.SingleNode)
        {
            return;
        }

        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ConsensusUnavailableException or HttpRequestException or IOException)
            {
                LogRefreshFailure(_logger, exception.Message);
            }
        }
    }

    private bool IsLocalCatalogMember()
    {
        ClusterTopology topology = _topologyStore.GetSnapshot();
        return ResolveCatalogNodeIds(topology).Contains(_raftOptions.NodeId, StringComparer.Ordinal);
    }

    private static string[] ResolveCatalogNodeIds(ClusterTopology topology) => topology.CatalogNodeIds.Length > 0
        ? topology.CatalogNodeIds
        : topology.Nodes
            .Where(static node => node.State != ClusterNodeState.Removed)
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .Take(3)
            .Select(static node => node.NodeId)
            .ToArray();

    private async ValueTask<byte[]> DownloadSnapshotAsync(CancellationToken cancellationToken)
    {
        ClusterTopology topology = await _topologyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, ClusterNodeDescriptor> nodes = topology.Nodes.ToDictionary(
            static node => node.NodeId,
            StringComparer.Ordinal);
        Exception? lastFailure = null;
        foreach (string nodeId in ResolveCatalogNodeIds(topology))
        {
            if (string.Equals(nodeId, _raftOptions.NodeId, StringComparison.Ordinal) ||
                !nodes.TryGetValue(nodeId, out ClusterNodeDescriptor? node) ||
                node.State == ClusterNodeState.Removed)
            {
                continue;
            }

            try
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Get,
                    node.InternalEndpoint.TrimEnd('/') + SnapshotPath)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                };
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                byte[] snapshot = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                string? signature = response.Headers.TryGetValues("X-SlimVector-Signature", out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null;
                if (!_exchange.Validate(snapshot, signature))
                {
                    throw new UnauthorizedAccessException("The catalog snapshot signature is invalid.");
                }

                return snapshot;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or
                                               TaskCanceledException or UnauthorizedAccessException)
            {
                lastFailure = exception;
            }
        }

        throw new ConsensusUnavailableException(
            MultiRaftNode.CatalogGroupId,
            ConsensusFailureKind.QuorumUnavailable,
            leaderEndpoint: null,
            lastFailure ?? new InvalidOperationException("No remote catalog voter is reachable."));
    }

    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning, Message = "Catalog cache refresh failed: {Message}")]
    private static partial void LogRefreshFailure(ILogger logger, string message);

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
