using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Routing;

public interface IDataNodeRpcClient
{
    ValueTask ReplicateAsync(
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default);
}

public interface IDataNodeRpcReceiver
{
    ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string? signature,
        CancellationToken cancellationToken = default);
}

public sealed record DataNodeRpcMetricsSnapshot(
    long OutgoingRequests,
    long IncomingRequests,
    long FailedRequests,
    long RoutingEpochRejections,
    long DurationMicroseconds);

public sealed class DataNodeRpcMetrics
{
    private long _outgoingRequests;
    private long _incomingRequests;
    private long _failedRequests;
    private long _routingEpochRejections;
    private long _durationMicroseconds;

    public void RecordOutgoing(TimeSpan elapsed, bool succeeded)
    {
        Interlocked.Increment(ref _outgoingRequests);
        Interlocked.Add(ref _durationMicroseconds, (long)elapsed.TotalMicroseconds);
        if (!succeeded)
        {
            Interlocked.Increment(ref _failedRequests);
        }
    }

    public void RecordIncoming() => Interlocked.Increment(ref _incomingRequests);

    public void RecordRoutingEpochRejection() => Interlocked.Increment(ref _routingEpochRejections);

    public DataNodeRpcMetricsSnapshot GetSnapshot() => new(
        Volatile.Read(ref _outgoingRequests),
        Volatile.Read(ref _incomingRequests),
        Volatile.Read(ref _failedRequests),
        Volatile.Read(ref _routingEpochRejections),
        Volatile.Read(ref _durationMicroseconds));
}

public sealed class DataNodeRpcClient : IDataNodeRpcClient
{
    private const string EndpointPath = "/internal/data/replicate";
    private readonly HttpClient _httpClient;
    private readonly IClusterTopologyStore _topologyStore;
    private readonly string _localNodeId;
    private readonly byte[]? _sharedSecret;
    private readonly DataNodeRpcMetrics _metrics;

    public DataNodeRpcClient(
        HttpClient httpClient,
        IClusterTopologyStore topologyStore,
        IOptions<RaftOptions> raftOptions,
        IOptions<ApiOptions> apiOptions,
        DataNodeRpcMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(topologyStore);
        ArgumentNullException.ThrowIfNull(raftOptions);
        ArgumentNullException.ThrowIfNull(apiOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        _httpClient = httpClient;
        _topologyStore = topologyStore;
        _localNodeId = raftOptions.Value.NodeId;
        _sharedSecret = string.IsNullOrEmpty(apiOptions.Value.AdminApiKey)
            ? null
            : Encoding.UTF8.GetBytes(apiOptions.Value.AdminApiKey);
        _metrics = metrics;
    }

    public async ValueTask ReplicateAsync(
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] payload = RaftCommandCodec.Serialize(command);
        ClusterTopology topology = await _topologyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, ClusterNodeDescriptor> nodes = topology.Nodes.ToDictionary(
            static node => node.NodeId,
            StringComparer.Ordinal);
        IEnumerable<string> targetNodeIds;
        if (string.Equals(command.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal))
        {
            targetNodeIds = topology.CatalogNodeIds.Length > 0
                ? topology.CatalogNodeIds
                : topology.Nodes
                    .Where(static node => node.State != ClusterNodeState.Removed)
                    .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
                    .Take(3)
                    .Select(static node => node.NodeId);
        }
        else
        {
            DataGroupDescriptor group = topology.DataGroups.FirstOrDefault(candidate =>
                    string.Equals(candidate.GroupId, command.GroupId, StringComparison.Ordinal) &&
                    candidate.State != DataGroupState.Removed)
                ?? throw new DomainException(
                    ErrorCodes.InvalidPlacement,
                    $"Data group '{command.GroupId}' is absent from topology epoch {topology.Epoch}.");
            targetNodeIds = group.Replicas.Select(static replica => replica.NodeId);
        }

        ClusterNodeDescriptor[] targets = targetNodeIds
            .Where(nodeId => !string.Equals(nodeId, _localNodeId, StringComparison.Ordinal))
            .Select(nodeId => nodes.GetValueOrDefault(nodeId))
            .Where(static node => node is not null && node.State is ClusterNodeState.Active or ClusterNodeState.Draining)
            .Cast<ClusterNodeDescriptor>()
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        Exception? lastFailure = null;
        foreach (ClusterNodeDescriptor target in targets)
        {
            long started = Stopwatch.GetTimestamp();
            bool succeeded = false;
            try
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    target.InternalEndpoint.TrimEnd('/') + EndpointPath)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                    Content = new ByteArrayContent(payload),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");
                if (_sharedSecret is not null)
                {
                    request.Headers.TryAddWithoutValidation(
                        "X-SlimVector-Signature",
                        Convert.ToHexStringLower(HMACSHA256.HashData(_sharedSecret, payload)));
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    succeeded = true;
                    return;
                }

                string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (detail.Contains(ErrorCodes.RoutingEpochMismatch, StringComparison.Ordinal))
                {
                    throw new DomainException(
                        ErrorCodes.RoutingEpochMismatch,
                        $"Data group '{command.GroupId}' rejected topology epoch for the routed write.");
                }

                lastFailure = new HttpRequestException(
                    $"Data node '{target.NodeId}' returned HTTP {(int)response.StatusCode}: {detail}",
                    inner: null,
                    response.StatusCode);
            }
            catch (DomainException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                lastFailure = exception;
            }
            finally
            {
                _metrics.RecordOutgoing(Stopwatch.GetElapsedTime(started), succeeded);
            }
        }

        throw new ConsensusUnavailableException(
            command.GroupId,
            ConsensusFailureKind.QuorumUnavailable,
            leaderEndpoint: null,
            lastFailure ?? new InvalidOperationException($"No remote replica is available for '{command.GroupId}'."));
    }
}

public sealed class DataNodeRpcReceiver : IDataNodeRpcReceiver
{
    private readonly ILocalRaftCommandReplicator _replicator;
    private readonly ILocalRaftGroupManager _groups;
    private readonly IStorageEngine _catalog;
    private readonly byte[]? _sharedSecret;
    private readonly DataNodeRpcMetrics _metrics;

    public DataNodeRpcReceiver(
        ILocalRaftCommandReplicator replicator,
        ILocalRaftGroupManager groups,
        IStorageEngine catalog,
        IOptions<ApiOptions> apiOptions,
        DataNodeRpcMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(replicator);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(apiOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        _replicator = replicator;
        _groups = groups;
        _catalog = catalog;
        _sharedSecret = string.IsNullOrEmpty(apiOptions.Value.AdminApiKey)
            ? null
            : Encoding.UTF8.GetBytes(apiOptions.Value.AdminApiKey);
        _metrics = metrics;
    }

    public async ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        ValidateSignature(payload.Span, signature);
        RaftCommandEnvelope command = RaftCommandCodec.Deserialize(payload.Span);
        if (command.Kind is not RaftCommandKind.DataBatch and not RaftCommandKind.ShardBatch and
            not RaftCommandKind.ShardTransfer and not RaftCommandKind.CatalogUpsert and
            not RaftCommandKind.CatalogDelete and not RaftCommandKind.TopologyReplace)
        {
            throw new InvalidDataException("The internal replication command kind is not supported.");
        }

        bool catalogCommand = string.Equals(command.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal);
        if (!catalogCommand && !_groups.GetHostedDataGroupIds().Contains(command.GroupId, StringComparer.Ordinal))
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"This node does not host data group '{command.GroupId}'.");
        }

        foreach (DataBatchCommand batch in catalogCommand ? [] : ReadBatches(command))
        {
            CollectionDefinition requested = RaftCommandCodec.ToDomain(batch.Collection);
            CollectionDefinition current = await _catalog.GetCollectionAsync(requested.Name, cancellationToken)
                .ConfigureAwait(false) ?? throw new DomainException(
                ErrorCodes.CollectionNotFound,
                $"Collection '{requested.Name}' is not present in the local catalog.");
            if (current.Id != requested.Id || current.Placement?.Epoch != batch.RoutingEpoch)
            {
                _metrics.RecordRoutingEpochRejection();
                throw new DomainException(
                    ErrorCodes.RoutingEpochMismatch,
                    $"Write epoch {batch.RoutingEpoch} does not match collection epoch {current.Placement?.Epoch ?? 0}.");
            }

            foreach (RaftStorageOperation operation in batch.Operations)
            {
                if (command.Kind == RaftCommandKind.ShardTransfer)
                {
                    continue;
                }

                ShardRoute route = current.Placement?.Resolve(current.Id, operation.Id) ??
                    new ShardRoute(0, command.GroupId, batch.RoutingEpoch);
                if (!string.Equals(route.DataGroupId, command.GroupId, StringComparison.Ordinal))
                {
                    _metrics.RecordRoutingEpochRejection();
                    throw new DomainException(
                        ErrorCodes.RoutingEpochMismatch,
                        $"Document '{operation.Id}' is now assigned to data group '{route.DataGroupId}'.");
                }
            }
        }

        await _replicator.ReplicateLocalAsync(command, cancellationToken).ConfigureAwait(false);
        _metrics.RecordIncoming();
    }

    private static DataBatchCommand[] ReadBatches(RaftCommandEnvelope command) => command.Kind switch
    {
        RaftCommandKind.DataBatch when command.DataBatch is not null => [command.DataBatch],
        RaftCommandKind.ShardBatch when command.ShardBatch is not null => command.ShardBatch.Batches,
        RaftCommandKind.ShardTransfer when command.ShardTransfer is not null => [command.ShardTransfer],
        _ => throw new InvalidDataException("The data command payload is incomplete."),
    };

    private void ValidateSignature(ReadOnlySpan<byte> payload, string? signature)
    {
        if (_sharedSecret is null)
        {
            return;
        }

        byte[] expected = HMACSHA256.HashData(_sharedSecret, payload);
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new UnauthorizedAccessException("The internal RPC signature is invalid.");
        }

        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new UnauthorizedAccessException("The internal RPC signature is invalid.");
        }
    }
}
