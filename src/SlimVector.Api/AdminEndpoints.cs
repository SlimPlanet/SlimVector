using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimVector.Api.Contracts;
using SlimVector.Application;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Placement;
using SlimVector.Application.Routing;
using SlimVector.Domain;
using SlimVector.Raft;

namespace SlimVector.Api;

internal static class AdminEndpoints
{
    private const string AdminKeyHeader = "X-SlimVector-Admin-Key";

    public static IEndpointRouteBuilder MapSlimVectorAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ApiOptions options = endpoints.ServiceProvider.GetRequiredService<IOptions<ApiOptions>>().Value;
        if (!options.AdminEndpointsEnabled)
        {
            return endpoints;
        }

        RouteGroupBuilder admin = endpoints.MapGroup($"{options.RoutePrefix}/admin");
        admin.WithRequestTimeout(ApiEndpoints.RequestTimeoutPolicyName);
        RouteGroupBuilder indexes = admin.MapGroup("/collections/{name}/index").WithTags("Index administration");
        indexes.MapGet("/", GetIndexStatusAsync).Produces<IndexMigrationResponse>();
        indexes.MapPost("/rollback", RollbackIndexAsync).Produces<AdminOperationResponse>();

        RouteGroupBuilder membership = admin.MapGroup("/cluster/membership").WithTags("Cluster membership");
        membership.MapGet("/", GetMembership).Produces<ClusterMembershipResponse>();
        membership.MapPost("/add", AddMemberAsync).Produces<AdminOperationResponse>();
        membership.MapPost("/promote", PromoteMemberAsync).Produces<AdminOperationResponse>();
        membership.MapPost("/demote", DemoteMemberAsync).Produces<AdminOperationResponse>();
        membership.MapPost("/remove", RemoveMemberAsync).Produces<AdminOperationResponse>();
        membership.MapPost("/transfer-leadership", TransferLeadershipAsync).Produces<AdminOperationResponse>();

        RouteGroupBuilder nodes = admin.MapGroup("/cluster/nodes").WithTags("Cluster nodes");
        nodes.MapGet("/topology", GetTopologyAsync).Produces<ClusterTopologyResponse>();
        nodes.MapPost("/join", JoinNodeAsync).Produces<ClusterTopologyResponse>();
        nodes.MapPost("/{nodeId}/drain", DrainNodeAsync).Produces<ClusterTopologyResponse>();
        nodes.MapDelete("/{nodeId}", RemoveNodeAsync).Produces<ClusterTopologyResponse>();
        nodes.MapGet("/rebalance/plan", PlanNodeRebalanceAsync).Produces<SharedNothingRebalancePlanResponse>();
        nodes.MapPost("/rebalance/approve", ApproveNodeRebalanceAsync).Produces<ClusterTopologyResponse>();

        RouteGroupBuilder groups = admin.MapGroup("/cluster/groups").WithTags("Data group administration");
        groups.MapPost("/prepare-replica", PrepareDataGroupReplicaAsync).Produces<AdminOperationResponse>();

        RouteGroupBuilder rebalance = admin.MapGroup("/cluster/rebalance").WithTags("Cluster rebalancing");
        rebalance.MapGet("/plan", PlanRebalanceAsync).Produces<RebalancePlanResponse>();
        rebalance.MapPost("/approve", ApproveRebalanceAsync).Produces<PlacementControllerResponse>();
        rebalance.MapGet("/status", GetRebalanceStatusAsync).Produces<PlacementControllerResponse>();
        rebalance.MapPost("/advance", AdvanceRebalanceAsync).Produces<PlacementControllerResponse>();
        rebalance.MapPost("/pause", PauseRebalance).Produces<AdminOperationResponse>();
        rebalance.MapPost("/resume", ResumeRebalance).Produces<AdminOperationResponse>();
        return endpoints;
    }

    private static async Task<IResult> PlanRebalanceAsync(
        string? drainDataGroupId,
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        RebalancePlan plan = await controller.PlanAsync(drainDataGroupId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(plan));
    }

    private static async Task<IResult> ApproveRebalanceAsync(
        RebalanceApprovalRequest request,
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        PlacementControllerStatus status = await controller.ApproveAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(status));
    }

    private static async Task<IResult> GetRebalanceStatusAsync(
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        return TypedResults.Ok(ToResponse(await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<IResult> AdvanceRebalanceAsync(
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        return TypedResults.Ok(ToResponse(await controller.AdvanceAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static IResult PauseRebalance(
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        controller.Pause();
        return TypedResults.Ok(new AdminOperationResponse { Status = "paused" });
    }

    private static IResult ResumeRebalance(
        HttpContext context,
        IPlacementController controller,
        IOptions<ApiOptions> options)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        controller.ResumeProcessing();
        return TypedResults.Ok(new AdminOperationResponse { Status = "resumed" });
    }

    private static async Task<IResult> GetIndexStatusAsync(
        string name,
        HttpContext context,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        IndexMigrationStatus status = await database.GetIndexStatusAsync(name, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(status));
    }

    private static async Task<IResult> RollbackIndexAsync(
        string name,
        HttpContext context,
        ISlimVectorDatabase database,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        bool rolledBack = await database.RollbackIndexAsync(name, cancellationToken).ConfigureAwait(false);
        return rolledBack
            ? TypedResults.Ok(new AdminOperationResponse { Status = "rolled-back" })
            : TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "No rollback generation is available",
                type: "https://slimvector.dev/problems/index_rollback_unavailable");
    }

    private static IResult GetMembership(
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> options)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        return TypedResults.Ok(new ClusterMembershipResponse
        {
            Groups = membership.GetMembershipStatuses().Select(static status => new GroupMembershipResponse
            {
                GroupId = status.GroupId,
                ChangeState = status.ChangeState,
                ChangeEndpoint = status.ChangeEndpoint,
                Members = status.Members.Select(static member => new RaftMemberResponse
                {
                    Endpoint = member.Endpoint,
                    IsLeader = member.IsLeader,
                    IsRemote = member.IsRemote,
                    TransportStatus = member.TransportStatus,
                    MatchIndex = member.MatchIndex,
                    ReplicationLag = member.ReplicationLag,
                }).ToArray(),
            }).ToArray(),
        });
    }

    private static async Task<IResult> GetTopologyAsync(
        HttpContext context,
        IClusterTopologyService topology,
        IOptions<ApiOptions> apiOptions,
        IOptions<DataPlacementOptions> placementOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        ClusterTopology current = await topology.GetAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(current, placementOptions.Value));
    }

    private static async Task<IResult> JoinNodeAsync(
        ClusterNodeJoinRequest request,
        HttpContext context,
        IClusterTopologyService topology,
        ICatalogCacheSynchronizer catalogCache,
        ISharedNothingRebalanceCoordinator rebalanceCoordinator,
        IOptions<ApiOptions> apiOptions,
        IOptions<DataPlacementOptions> placementOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        ClusterTopology current = await topology.JoinAsync(
            new ClusterNodeRegistration(
                request.NodeId,
                request.ApiEndpoint,
                request.InternalEndpoint,
                request.RaftHost,
                request.Zone,
                request.CapacityBytes,
                request.RaftPortStart,
                request.RaftPortCount,
                request.Roles),
            cancellationToken).ConfigureAwait(false);
        await catalogCache.SeedNodeAsync(request.InternalEndpoint, cancellationToken).ConfigureAwait(false);
        SharedNothingRebalancePlan plan = await rebalanceCoordinator.PlanAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(current, placementOptions.Value, plan));
    }

    private static async Task<IResult> DrainNodeAsync(
        string nodeId,
        HttpContext context,
        IClusterTopologyService topology,
        IOptions<ApiOptions> apiOptions,
        IOptions<DataPlacementOptions> placementOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        ClusterTopology current = await topology.DrainAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(current, placementOptions.Value));
    }

    private static async Task<IResult> RemoveNodeAsync(
        string nodeId,
        HttpContext context,
        IClusterTopologyService topology,
        IOptions<ApiOptions> apiOptions,
        IOptions<DataPlacementOptions> placementOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        ClusterTopology current = await topology.RemoveAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(current, placementOptions.Value));
    }

    private static async Task<IResult> PlanNodeRebalanceAsync(
        string? drainNodeId,
        HttpContext context,
        ISharedNothingRebalanceCoordinator coordinator,
        IOptions<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        SharedNothingRebalancePlan plan = await coordinator
            .PlanAsync(drainNodeId, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(plan));
    }

    private static async Task<IResult> ApproveNodeRebalanceAsync(
        RebalanceApprovalRequest request,
        HttpContext context,
        ISharedNothingRebalanceCoordinator coordinator,
        IOptions<ApiOptions> apiOptions,
        IOptions<DataPlacementOptions> placementOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        ClusterTopology topology = await coordinator.ApproveAsync(request.PlanId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToResponse(topology, placementOptions.Value));
    }

    private static async Task<IResult> PrepareDataGroupReplicaAsync(
        PrepareDataGroupReplicaRequest request,
        HttpContext context,
        ILocalDataGroupProvisioner provisioner,
        IOptions<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        await provisioner.PrepareJoiningReplicaAsync(
            request.GroupId,
            request.RaftEndpoint,
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new AdminOperationResponse { Status = "prepared" });
    }

    private static Task<IResult> AddMemberAsync(
        MembershipChangeRequest request,
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> apiOptions,
        IOptions<ClusterMembershipOptions> membershipOptions,
        CancellationToken cancellationToken) => ChangeMembershipAsync(
            request,
            context,
            apiOptions.Value,
            membershipOptions.Value,
            (endpoint, token) => membership.AddMemberAsync(request.GroupId, endpoint, token),
            "added",
            cancellationToken);

    private static Task<IResult> PromoteMemberAsync(
        MembershipChangeRequest request,
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> apiOptions,
        IOptions<ClusterMembershipOptions> membershipOptions,
        CancellationToken cancellationToken) => ChangeMembershipAsync(
            request,
            context,
            apiOptions.Value,
            membershipOptions.Value,
            (endpoint, token) => membership.PromoteMemberAsync(request.GroupId, endpoint, token),
            "promoted",
            cancellationToken);

    private static Task<IResult> DemoteMemberAsync(
        MembershipChangeRequest request,
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> apiOptions,
        IOptions<ClusterMembershipOptions> membershipOptions,
        CancellationToken cancellationToken) => ChangeMembershipAsync(
            request,
            context,
            apiOptions.Value,
            membershipOptions.Value,
            (endpoint, token) => membership.DemoteMemberAsync(
                request.GroupId,
                endpoint,
                membershipOptions.Value.MinimumVotingMembers,
                token),
            "demoting",
            cancellationToken);

    private static Task<IResult> RemoveMemberAsync(
        MembershipChangeRequest request,
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> apiOptions,
        IOptions<ClusterMembershipOptions> membershipOptions,
        CancellationToken cancellationToken) => ChangeMembershipAsync(
            request,
            context,
            apiOptions.Value,
            membershipOptions.Value,
            (endpoint, token) => membership.RemoveMemberAsync(
                request.GroupId,
                endpoint,
                membershipOptions.Value.MinimumVotingMembers,
                token),
            "removed",
            cancellationToken);

    private static async Task<IResult> TransferLeadershipAsync(
        MembershipChangeRequest request,
        HttpContext context,
        IClusterMembershipCoordinator membership,
        IOptions<ApiOptions> apiOptions,
        IOptions<ClusterMembershipOptions> membershipOptions,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(membershipOptions.Value.OperationTimeout);
        await membership.TransferLeadershipAsync(request.GroupId, timeout.Token).ConfigureAwait(false);
        return TypedResults.Ok(new AdminOperationResponse { Status = "leadership-transferred" });
    }

    private static async Task<IResult> ChangeMembershipAsync(
        MembershipChangeRequest request,
        HttpContext context,
        ApiOptions apiOptions,
        ClusterMembershipOptions membershipOptions,
        Func<IPEndPoint, CancellationToken, ValueTask> change,
        string status,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, apiOptions);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (!membershipOptions.Enabled)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Dynamic membership is disabled",
                type: "https://slimvector.dev/problems/membership_disabled");
        }

        if (request.Endpoint is null || !RaftOptionsValidator.TryParseEndpoint(request.Endpoint, out IPEndPoint? endpoint))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A valid IP endpoint is required",
                type: "https://slimvector.dev/problems/invalid_member_endpoint");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(membershipOptions.OperationTimeout);
        await change(endpoint!, timeout.Token).ConfigureAwait(false);
        return TypedResults.Ok(new AdminOperationResponse { Status = status });
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? Authorize(
        HttpContext context,
        ApiOptions options)
    {
        string supplied = context.Request.Headers[AdminKeyHeader].ToString();
        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.AdminApiKey);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        bool authorized = suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        CryptographicOperations.ZeroMemory(suppliedBytes);
        CryptographicOperations.ZeroMemory(expectedBytes);
        return authorized
            ? null
            : TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Administrator authentication required",
                type: "https://slimvector.dev/problems/admin_authentication_required",
                extensions: new Dictionary<string, object?> { ["code"] = "admin_authentication_required" });
    }

    private static IndexMigrationResponse ToResponse(IndexMigrationStatus status) => new()
    {
        ActiveKind = status.ActiveKind,
        ActiveGeneration = status.ActiveGeneration,
        PreviousGeneration = status.PreviousGeneration,
        State = status.State,
        Reason = status.Reason,
        LastMigration = status.LastMigration,
    };

    private static RebalancePlanResponse ToResponse(RebalancePlan plan) => new()
    {
        PlanId = plan.PlanId,
        CreatedAt = plan.CreatedAt,
        DryRun = plan.DryRun,
        Actions = plan.Actions.Select(static action => new RebalanceActionResponse
        {
            OperationId = action.OperationId,
            CollectionId = action.CollectionId,
            CollectionName = action.CollectionName,
            ShardId = action.ShardId,
            SourceDataGroupId = action.SourceDataGroupId,
            TargetDataGroupId = action.TargetDataGroupId,
            Reason = action.Reason,
        }).ToArray(),
    };

    private static ClusterTopologyResponse ToResponse(
        ClusterTopology topology,
        DataPlacementOptions options,
        SharedNothingRebalancePlan? suggestedPlan = null)
    {
        long rawBytes = topology.Nodes
            .Where(static node => node.State is ClusterNodeState.Active or ClusterNodeState.Draining &&
                node.Roles.Contains("data", StringComparer.OrdinalIgnoreCase))
            .Sum(static node => node.CapacityBytes);
        long reservedBytes = (long)Math.Ceiling(rawBytes * options.ReserveRatio);
        return new ClusterTopologyResponse
        {
            FormatVersion = topology.FormatVersion,
            Epoch = topology.Epoch,
            CatalogNodeIds = topology.CatalogNodeIds,
            Nodes = topology.Nodes.OrderBy(static node => node.NodeId, StringComparer.Ordinal)
                .Select(static node => new ClusterNodeResponse
                {
                    NodeId = node.NodeId,
                    ApiEndpoint = node.ApiEndpoint,
                    InternalEndpoint = node.InternalEndpoint,
                    RaftHost = node.RaftHost,
                    Zone = node.Zone,
                    CapacityBytes = node.CapacityBytes,
                    UsedBytes = node.UsedBytes,
                    AssignedBytes = node.AssignedBytes,
                    RaftPortStart = node.RaftPortStart,
                    RaftPortCount = node.RaftPortCount,
                    State = node.State,
                    LastSeenAt = node.LastSeenAt,
                    Roles = node.Roles,
                }).ToArray(),
            DataGroups = topology.DataGroups.OrderBy(static group => group.GroupId, StringComparer.Ordinal)
                .Select(ToResponse).ToArray(),
            ReplicaMoves = topology.ReplicaMoves.OrderBy(static move => move.UpdatedAt)
                .Select(static move => new ReplicaMoveStatusResponse
                {
                    OperationId = move.OperationId,
                    PlanId = move.PlanId,
                    GroupId = move.GroupId,
                    SourceNodeId = move.SourceNodeId,
                    TargetNodeId = move.TargetNodeId,
                    EstimatedBytes = move.EstimatedBytes,
                    State = move.State,
                    LastError = move.LastError,
                    UpdatedAt = move.UpdatedAt,
                }).ToArray(),
            Capacity = new ClusterCapacityResponse
            {
                RawBytes = rawBytes,
                ReservedBytes = reservedBytes,
                EstimatedUsableBytes = Math.Max(0, rawBytes - reservedBytes) / options.ReplicationFactor,
                ReplicationFactor = options.ReplicationFactor,
            },
            SuggestedRebalancePlan = suggestedPlan is null ? null : ToResponse(suggestedPlan),
        };
    }

    private static DataGroupTopologyResponse ToResponse(DataGroupDescriptor group) => new()
    {
        GroupId = group.GroupId,
        Generation = group.Generation,
        ReplicationFactor = group.ReplicationFactor,
        EstimatedBytes = group.EstimatedBytes,
        State = group.State,
        Replicas = group.Replicas.Select(static replica => new DataGroupReplicaResponse
        {
            NodeId = replica.NodeId,
            RaftEndpoint = replica.RaftEndpoint,
            ObservedReplicationLag = replica.ObservedReplicationLag,
            Healthy = replica.Healthy,
        }).ToArray(),
    };

    private static SharedNothingRebalancePlanResponse ToResponse(SharedNothingRebalancePlan plan) => new()
    {
        PlanId = plan.PlanId,
        TopologyEpoch = plan.TopologyEpoch,
        CreatedAt = plan.CreatedAt,
        DrainNodeId = plan.DrainNodeId,
        GroupsToCreate = plan.GroupsToCreate.Select(ToResponse).ToArray(),
        ReplicaMoves = plan.ReplicaMoves.Select(static move => new ReplicaRelocationResponse
        {
            GroupId = move.GroupId,
            SourceNodeId = move.SourceNodeId,
            TargetNodeId = move.TargetNodeId,
            TargetRaftEndpoint = move.TargetRaftEndpoint,
            EstimatedBytes = move.EstimatedBytes,
            Reason = move.Reason,
        }).ToArray(),
        CapacityChanges = plan.AssignedBytesBefore.Keys
            .Union(plan.AssignedBytesAfter.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(nodeId => new NodeCapacityChangeResponse
            {
                NodeId = nodeId,
                AssignedBytesBefore = plan.AssignedBytesBefore.GetValueOrDefault(nodeId),
                AssignedBytesAfter = plan.AssignedBytesAfter.GetValueOrDefault(nodeId),
            }).ToArray(),
    };

    private static PlacementControllerResponse ToResponse(PlacementControllerStatus status) => new()
    {
        Paused = status.Paused,
        Moves = status.Moves.Select(static move => new ShardMoveResponse
        {
            OperationId = move.OperationId,
            CollectionId = move.CollectionId,
            CollectionName = move.CollectionName,
            ShardId = move.ShardId,
            SourceDataGroupId = move.SourceDataGroupId,
            TargetDataGroupId = move.TargetDataGroupId,
            State = move.State,
            RoutingEpoch = move.RoutingEpoch,
            SnapshotVersion = move.SnapshotVersion,
            ReplayedThroughVersion = move.ReplayedThroughVersion,
        }).ToArray(),
    };
}
