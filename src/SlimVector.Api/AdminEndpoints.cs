using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimVector.Api.Contracts;
using SlimVector.Application;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Placement;
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
