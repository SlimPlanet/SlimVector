using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;

namespace SlimVector.Application.Placement;

public interface ISharedNothingRebalanceCoordinator
{
    ValueTask<SharedNothingRebalancePlan> PlanAsync(
        string? drainNodeId = null,
        CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> ApproveAsync(Guid planId, CancellationToken cancellationToken = default);
}

public sealed class SharedNothingRebalanceCoordinator : BackgroundService, ISharedNothingRebalanceCoordinator
{
    private readonly IClusterTopologyService _topology;
    private readonly ISharedNothingPlacementPlanner _planner;
    private readonly HttpClient _httpClient;
    private readonly ApiOptions _apiOptions;
    private readonly RebalancingOptions _rebalancingOptions;
    private readonly ConcurrentDictionary<Guid, SharedNothingRebalancePlan> _plans = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SharedNothingRebalanceCoordinator(
        IClusterTopologyService topology,
        ISharedNothingPlacementPlanner planner,
        IHttpClientFactory httpClientFactory,
        IOptions<ApiOptions> apiOptions,
        IOptions<RebalancingOptions> rebalancingOptions)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _topology = topology;
        _planner = planner;
        _httpClient = httpClientFactory.CreateClient("SlimVector.ClusterAdministration");
        _apiOptions = apiOptions.Value;
        _rebalancingOptions = rebalancingOptions.Value;
    }

    public async ValueTask<SharedNothingRebalancePlan> PlanAsync(
        string? drainNodeId = null,
        CancellationToken cancellationToken = default)
    {
        ClusterTopology topology = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
        SharedNothingRebalancePlan plan = _planner.Plan(topology, drainNodeId);
        _plans[plan.PlanId] = plan;
        return plan;
    }

    public async ValueTask<ClusterTopology> ApproveAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        if (!_plans.TryRemove(planId, out SharedNothingRebalancePlan? plan))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, $"Rebalance plan '{planId}' was not found or expired.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology current = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
            if (current.Epoch != plan.TopologyEpoch)
            {
                throw new DomainException(
                    ErrorCodes.RoutingEpochMismatch,
                    $"Rebalance plan epoch {plan.TopologyEpoch} does not match topology epoch {current.Epoch}.");
            }

            Dictionary<string, DataGroupDescriptor> groups = current.DataGroups.ToDictionary(
                static group => group.GroupId,
                StringComparer.Ordinal);
            foreach (DataGroupDescriptor created in plan.GroupsToCreate)
            {
                groups.Add(created.GroupId, created with { State = DataGroupState.Active });
            }

            HashSet<string> relocatingGroups = plan.ReplicaMoves.Select(static move => move.GroupId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string groupId in relocatingGroups)
            {
                groups[groupId] = groups[groupId] with { State = DataGroupState.Relocating };
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            ReplicaMoveDescriptor[] moves = [
                .. current.ReplicaMoves.Where(static move => move.State != ReplicaMoveState.Completed),
                .. plan.ReplicaMoves.Select(move => new ReplicaMoveDescriptor
                {
                    OperationId = Guid.NewGuid(),
                    PlanId = plan.PlanId,
                    GroupId = move.GroupId,
                    SourceNodeId = move.SourceNodeId,
                    TargetNodeId = move.TargetNodeId,
                    TargetRaftEndpoint = move.TargetRaftEndpoint,
                    EstimatedBytes = move.EstimatedBytes,
                    State = ReplicaMoveState.Planned,
                    UpdatedAt = now,
                }),
            ];
            ClusterTopology staged = current with
            {
                Epoch = checked(current.Epoch + 1),
                Nodes = current.Nodes.Select(node => plan.AssignedBytesAfter.TryGetValue(node.NodeId, out long bytes)
                    ? node with { AssignedBytes = bytes }
                    : node).ToArray(),
                DataGroups = groups.Values.OrderBy(static group => group.GroupId, StringComparer.Ordinal).ToArray(),
                ReplicaMoves = moves,
            };
            staged = await _topology.ReplaceAsync(staged, cancellationToken).ConfigureAwait(false);
            return staged;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMovesAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_rebalancingOptions.ReconcileInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private async ValueTask ProcessPendingMovesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology topology = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
            foreach (ReplicaMoveDescriptor move in topology.ReplicaMoves
                         .Where(static move => move.State != ReplicaMoveState.Completed)
                         .OrderBy(static move => move.UpdatedAt))
            {
                try
                {
                    await ExecuteMoveAsync(move.OperationId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await UpdateMoveStateAsync(
                        move.OperationId,
                        ReplicaMoveState.Failed,
                        exception.Message.Length <= 1_024 ? exception.Message : exception.Message[..1_024],
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ExecuteMoveAsync(Guid operationId, CancellationToken cancellationToken)
    {
        ClusterTopology topology = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
        ReplicaMoveDescriptor move = GetMove(topology, operationId);
        DataGroupDescriptor group = topology.DataGroups.Single(candidate =>
            string.Equals(candidate.GroupId, move.GroupId, StringComparison.Ordinal));
        Dictionary<string, ClusterNodeDescriptor> nodes = topology.Nodes.ToDictionary(
            static node => node.NodeId,
            StringComparer.Ordinal);
        ClusterNodeDescriptor target = nodes[move.TargetNodeId];

        await UpdateMoveStateAsync(operationId, ReplicaMoveState.PreparingTarget, null, cancellationToken).ConfigureAwait(false);
        await PostAsync(
            target.ApiEndpoint,
            "/admin/cluster/groups/prepare-replica",
            new PrepareReplicaRpcRequest { GroupId = move.GroupId, RaftEndpoint = move.TargetRaftEndpoint },
            ClusterAdministrationJsonContext.Default.PrepareReplicaRpcRequest,
            cancellationToken).ConfigureAwait(false);

        await UpdateMoveStateAsync(operationId, ReplicaMoveState.CatchingUp, null, cancellationToken).ConfigureAwait(false);
        GroupMembershipRpcResponse membership = await GetGroupMembershipAsync(
            group,
            move,
            nodes,
            cancellationToken).ConfigureAwait(false);
        if (!membership.Members.Any(member => SameEndpoint(member.Endpoint, move.TargetRaftEndpoint)))
        {
            string leaderApi = ResolveLeaderApi(membership, group, move, nodes);
            await PostMembershipAsync(
                leaderApi,
                "/admin/cluster/membership/add",
                move.GroupId,
                move.TargetRaftEndpoint,
                cancellationToken).ConfigureAwait(false);
        }

        await UpdateMoveStateAsync(operationId, ReplicaMoveState.Promoting, null, cancellationToken).ConfigureAwait(false);
        membership = await GetGroupMembershipAsync(group, move, nodes, cancellationToken).ConfigureAwait(false);
        if (membership.Members.Any(member => member.IsLeader && SameEndpoint(member.Endpoint, group.Replicas.Single(
                replica => string.Equals(replica.NodeId, move.SourceNodeId, StringComparison.Ordinal)).RaftEndpoint)))
        {
            await PostMembershipAsync(
                nodes[move.SourceNodeId].ApiEndpoint,
                "/admin/cluster/membership/transfer-leadership",
                move.GroupId,
                endpoint: null,
                cancellationToken).ConfigureAwait(false);
        }

        await UpdateMoveStateAsync(operationId, ReplicaMoveState.RemovingSource, null, cancellationToken).ConfigureAwait(false);
        membership = await GetGroupMembershipAsync(group, move, nodes, cancellationToken).ConfigureAwait(false);
        DataGroupReplica source = group.Replicas.Single(replica =>
            string.Equals(replica.NodeId, move.SourceNodeId, StringComparison.Ordinal));
        if (membership.Members.Any(member => SameEndpoint(member.Endpoint, source.RaftEndpoint)))
        {
            string leaderApi = ResolveLeaderApi(membership, group, move, nodes);
            await PostMembershipAsync(
                leaderApi,
                "/admin/cluster/membership/remove",
                move.GroupId,
                source.RaftEndpoint,
                cancellationToken).ConfigureAwait(false);
        }

        await CompleteMoveAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GroupMembershipRpcResponse> GetGroupMembershipAsync(
        DataGroupDescriptor group,
        ReplicaMoveDescriptor move,
        Dictionary<string, ClusterNodeDescriptor> nodes,
        CancellationToken cancellationToken)
    {
        foreach (string nodeId in group.Replicas.Select(static replica => replica.NodeId).Append(move.TargetNodeId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                ClusterMembershipRpcResponse response = await GetAsync(
                    nodes[nodeId].ApiEndpoint,
                    "/admin/cluster/membership/",
                    ClusterAdministrationJsonContext.Default.ClusterMembershipRpcResponse,
                    cancellationToken).ConfigureAwait(false);
                GroupMembershipRpcResponse? result = response.Groups.FirstOrDefault(candidate =>
                    string.Equals(candidate.GroupId, group.GroupId, StringComparison.Ordinal));
                if (result is not null)
                {
                    return result;
                }
            }
            catch (HttpRequestException)
            {
                // Try another replica API; the group remains available with quorum.
            }
        }

        throw new InvalidOperationException($"No membership view is available for data group '{group.GroupId}'.");
    }

    private async ValueTask UpdateMoveStateAsync(
        Guid operationId,
        ReplicaMoveState state,
        string? error,
        CancellationToken cancellationToken)
    {
        ClusterTopology current = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
        _ = GetMove(current, operationId);
        ClusterTopology next = current with
        {
            Epoch = checked(current.Epoch + 1),
            ReplicaMoves = current.ReplicaMoves.Select(move => move.OperationId == operationId
                ? move with { State = state, LastError = error, UpdatedAt = DateTimeOffset.UtcNow }
                : move).ToArray(),
        };
        await _topology.ReplaceAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteMoveAsync(Guid operationId, CancellationToken cancellationToken)
    {
        ClusterTopology current = await _topology.GetAsync(cancellationToken).ConfigureAwait(false);
        ReplicaMoveDescriptor move = GetMove(current, operationId);
        DataGroupDescriptor group = current.DataGroups.Single(candidate =>
            string.Equals(candidate.GroupId, move.GroupId, StringComparison.Ordinal));
        DataGroupReplica[] replicas = [
            .. group.Replicas.Where(replica => !string.Equals(replica.NodeId, move.SourceNodeId, StringComparison.Ordinal)),
            new DataGroupReplica { NodeId = move.TargetNodeId, RaftEndpoint = move.TargetRaftEndpoint },
        ];
        ClusterTopology next = current with
        {
            Epoch = checked(current.Epoch + 1),
            DataGroups = current.DataGroups.Select(candidate => string.Equals(candidate.GroupId, group.GroupId, StringComparison.Ordinal)
                ? group with
                {
                    Generation = checked(group.Generation + 1),
                    State = DataGroupState.Active,
                    Replicas = replicas,
                }
                : candidate).ToArray(),
            ReplicaMoves = current.ReplicaMoves.Select(candidate => candidate.OperationId == operationId
                ? candidate with
                {
                    State = ReplicaMoveState.Completed,
                    LastError = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
                : candidate).ToArray(),
        };
        await _topology.ReplaceAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PostMembershipAsync(
        string apiEndpoint,
        string path,
        string groupId,
        string? endpoint,
        CancellationToken cancellationToken) => await PostAsync(
        apiEndpoint,
        path,
        new MembershipChangeRpcRequest { GroupId = groupId, Endpoint = endpoint },
        ClusterAdministrationJsonContext.Default.MembershipChangeRpcRequest,
        cancellationToken).ConfigureAwait(false);

    private async ValueTask PostAsync<T>(
        string apiEndpoint,
        string path,
        T payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, apiEndpoint, path);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Cluster administration request returned HTTP {(int)response.StatusCode}: {detail}",
                inner: null,
                response.StatusCode);
        }
    }

    private async ValueTask<T> GetAsync<T>(
        string apiEndpoint,
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, apiEndpoint, path);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The cluster membership response is empty.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string apiEndpoint, string path)
    {
        HttpRequestMessage request = new(
            method,
            apiEndpoint.TrimEnd('/') + _apiOptions.RoutePrefix.TrimEnd('/') + path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        if (!string.IsNullOrEmpty(_apiOptions.AdminApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-SlimVector-Admin-Key", _apiOptions.AdminApiKey);
        }

        return request;
    }

    private static string ResolveLeaderApi(
        GroupMembershipRpcResponse membership,
        DataGroupDescriptor group,
        ReplicaMoveDescriptor move,
        Dictionary<string, ClusterNodeDescriptor> nodes)
    {
        string endpoint = membership.Members.FirstOrDefault(static member => member.IsLeader)?.Endpoint ??
            throw new InvalidOperationException($"Data group '{group.GroupId}' currently has no leader.");
        DataGroupReplica? replica = group.Replicas.FirstOrDefault(candidate => SameEndpoint(candidate.RaftEndpoint, endpoint));
        if (replica is not null)
        {
            return nodes[replica.NodeId].ApiEndpoint;
        }

        if (SameEndpoint(move.TargetRaftEndpoint, endpoint))
        {
            return nodes[move.TargetNodeId].ApiEndpoint;
        }

        throw new InvalidOperationException($"Leader endpoint '{endpoint}' is not present in the replica move.");
    }

    private static ReplicaMoveDescriptor GetMove(ClusterTopology topology, Guid operationId) =>
        topology.ReplicaMoves.FirstOrDefault(move => move.OperationId == operationId) ??
        throw new DomainException(ErrorCodes.InvalidPlacement, $"Replica move '{operationId}' no longer exists.");

    private static bool SameEndpoint(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

}

internal sealed record PrepareReplicaRpcRequest
{
    public required string GroupId { get; init; }

    public required string RaftEndpoint { get; init; }
}

internal sealed record MembershipChangeRpcRequest
{
    public required string GroupId { get; init; }

    public string? Endpoint { get; init; }
}

internal sealed record ClusterMembershipRpcResponse
{
    public GroupMembershipRpcResponse[] Groups { get; init; } = [];
}

internal sealed record GroupMembershipRpcResponse
{
    public string GroupId { get; init; } = string.Empty;

    public RaftMemberRpcResponse[] Members { get; init; } = [];
}

internal sealed record RaftMemberRpcResponse
{
    public string Endpoint { get; init; } = string.Empty;

    public bool IsLeader { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PrepareReplicaRpcRequest))]
[JsonSerializable(typeof(MembershipChangeRpcRequest))]
[JsonSerializable(typeof(ClusterMembershipRpcResponse))]
internal sealed partial class ClusterAdministrationJsonContext : JsonSerializerContext;
