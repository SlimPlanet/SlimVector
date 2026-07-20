using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;

namespace SlimVector.Api;

internal sealed partial class ApiExceptionHandler : IExceptionHandler
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ApiExceptionHandler> _logger;
    private readonly RaftOptions _raftOptions;

    public ApiExceptionHandler(
        IHostEnvironment environment,
        ILogger<ApiExceptionHandler> logger,
        IOptions<RaftOptions> raftOptions)
    {
        _environment = environment;
        _logger = logger;
        _raftOptions = raftOptions.Value;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Uri? leaderApiEndpoint = exception is ConsensusUnavailableException consensusFailure
            ? ResolveLeaderApiEndpoint(consensusFailure)
            : null;
        (int status, string code, string title) = exception switch
        {
            DomainException domain when domain.Code is ErrorCodes.CollectionNotFound or ErrorCodes.DocumentNotFound =>
                (StatusCodes.Status404NotFound, domain.Code, "Resource not found"),
            DomainException domain when domain.Code == ErrorCodes.MembershipMemberNotFound =>
                (StatusCodes.Status404NotFound, domain.Code, "Raft member or group not found"),
            DomainException domain when domain.Code is ErrorCodes.CollectionAlreadyExists or ErrorCodes.DocumentAlreadyExists =>
                (StatusCodes.Status409Conflict, domain.Code, "Resource conflict"),
            DomainException domain when domain.Code == ErrorCodes.MembershipConflict =>
                (StatusCodes.Status409Conflict, domain.Code, "Unsafe or conflicting membership change"),
            DomainException domain when domain.Code == ErrorCodes.QueueSaturated =>
                (StatusCodes.Status429TooManyRequests, domain.Code, "Service saturated"),
            DomainException domain when domain.Code == ErrorCodes.RequestTooLarge =>
                (StatusCodes.Status413PayloadTooLarge, domain.Code, "Request body too large"),
            DomainException domain when domain.Code == ErrorCodes.ReadOnlySecondary =>
                (StatusCodes.Status403Forbidden, domain.Code, "Secondary is read-only"),
            DomainException domain =>
                (StatusCodes.Status400BadRequest, domain.Code, "Invalid request"),
            ConsensusUnavailableException consensus when consensus.Kind == ConsensusFailureKind.NotLeader && leaderApiEndpoint is not null =>
                (StatusCodes.Status307TemporaryRedirect, "not_leader", "Redirecting to Raft leader"),
            ConsensusUnavailableException consensus when consensus.Kind == ConsensusFailureKind.NotLeader =>
                (StatusCodes.Status503ServiceUnavailable, "not_leader", "Raft leader required"),
            ConsensusUnavailableException =>
                (StatusCodes.Status503ServiceUnavailable, "quorum_unavailable", "Raft quorum unavailable"),
            BadHttpRequestException or System.Text.Json.JsonException =>
                (StatusCodes.Status400BadRequest, "invalid_request", "Invalid request"),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (StatusCodes.Status499ClientClosedRequest, "request_cancelled", "Request cancelled"),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Internal server error"),
        };

        if (status >= 500)
        {
            LogUnhandledFailure(_logger, httpContext.TraceIdentifier, exception);
        }

        ProblemDetails problem = new()
        {
            Status = status,
            Title = title,
            Detail = status < 500 || _environment.IsDevelopment() ? exception.Message : "An unexpected error occurred.",
            Instance = httpContext.Request.Path,
            Type = $"https://slimvector.dev/problems/{code}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (exception is ConsensusUnavailableException consensusException)
        {
            problem.Extensions["raftGroup"] = consensusException.GroupId;
            problem.Extensions["leaderEndpoint"] = consensusException.LeaderEndpoint;
            problem.Extensions["leaderApiEndpoint"] = leaderApiEndpoint?.AbsoluteUri;
            httpContext.Response.Headers.RetryAfter = "1";
        }

        if (leaderApiEndpoint is not null)
        {
            UriBuilder location = new(leaderApiEndpoint)
            {
                Path = httpContext.Request.PathBase.Add(httpContext.Request.Path),
                Query = httpContext.Request.QueryString.HasValue
                    ? httpContext.Request.QueryString.Value![1..]
                    : string.Empty,
            };
            httpContext.Response.Headers.Location = location.Uri.AbsoluteUri;
        }

        await ApiSerialization.WriteProblemAsync(httpContext, problem, cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Unhandled API failure. TraceId={TraceId}")]
    private static partial void LogUnhandledFailure(ILogger logger, string traceId, Exception exception);

    private Uri? ResolveLeaderApiEndpoint(ConsensusUnavailableException exception)
    {
        if (exception.Kind != ConsensusFailureKind.NotLeader || exception.LeaderEndpoint is null ||
            !IPEndPoint.TryParse(exception.LeaderEndpoint, out IPEndPoint? parsedLeader) ||
            !RaftOptionsValidator.TryParseEndpoint(_raftOptions.PublicEndpoint, out IPEndPoint? parsedLocalRaft))
        {
            return null;
        }

        IPEndPoint leader = parsedLeader!;
        IPEndPoint localRaft = parsedLocalRaft!;
        if (leader.Address.Equals(localRaft.Address) && leader.Port >= localRaft.Port &&
            leader.Port <= localRaft.Port + _raftOptions.DataGroupCount)
        {
            return null;
        }

        for (int index = 0; index < _raftOptions.Members.Length && index < _raftOptions.MemberApiEndpoints.Length; index++)
        {
            if (RaftOptionsValidator.TryParseEndpoint(_raftOptions.Members[index], out IPEndPoint? member) &&
                member is not null && member.Address.Equals(leader.Address) && leader.Port >= member.Port &&
                leader.Port <= member.Port + _raftOptions.DataGroupCount)
            {
                return new Uri(_raftOptions.MemberApiEndpoints[index].TrimEnd('/') + '/', UriKind.Absolute);
            }
        }

        return null;
    }
}
