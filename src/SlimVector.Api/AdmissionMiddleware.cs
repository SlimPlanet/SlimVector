using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SlimVector.Application.Admission;
using SlimVector.Application.Configuration;

namespace SlimVector.Api;

internal sealed partial class AdmissionMiddleware
{
    private const string ClientIdHeader = "X-SlimVector-Client-Id";
    private readonly RequestDelegate _next;
    private readonly ApiOptions _options;
    private readonly ILogger<AdmissionMiddleware> _logger;

    public AdmissionMiddleware(
        RequestDelegate next,
        IOptions<ApiOptions> options,
        ILogger<AdmissionMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAdmissionController admission)
    {
        if (!context.Request.Path.StartsWithSegments(_options.RoutePrefix, out PathString remaining))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        AdmissionOperation operation = remaining.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
            ? AdmissionOperation.Admin
            : HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                ? AdmissionOperation.Read
                : AdmissionOperation.Write;
        string clientId = GetClientId(context);
        string? collection = GetCollection(remaining);
        double cost = 1 + Math.Max(0, context.Request.ContentLength.GetValueOrDefault()) / 65_536D;
        AdmissionDecision decision = admission.TryAcquire(new AdmissionRequest(clientId, collection, operation, cost));
        if (decision.Allowed)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        int retryAfterSeconds = Math.Max(1, checked((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)));
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-SlimVector-RateLimit-Kind"] = decision.RejectionKind.ToString().ToLowerInvariant();
        context.Response.Headers["X-SlimVector-RateLimit-Scope"] = decision.Scope ?? "unknown";
        ProblemDetails problem = new()
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = decision.RejectionKind == AdmissionRejectionKind.Contractual
                ? "Rate limit exceeded"
                : "Service is temporarily congested",
            Detail = $"Admission was rejected at scope '{decision.Scope ?? "unknown"}'. Retry after {retryAfterSeconds} second(s).",
            Instance = context.Request.Path,
            Type = "https://slimvector.dev/problems/rate_limited",
        };
        problem.Extensions["code"] = "rate_limited";
        problem.Extensions["rejectionKind"] = decision.RejectionKind.ToString().ToLowerInvariant();
        problem.Extensions["scope"] = decision.Scope;
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;
        problem.Extensions["adaptiveRateRatio"] = decision.AdaptiveRateRatio;
        problem.Extensions["pressure"] = decision.Pressure;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        AdmissionRejected(
            _logger,
            clientId,
            collection ?? string.Empty,
            operation,
            decision.RejectionKind,
            decision.Scope ?? "unknown",
            retryAfterSeconds);
        await ApiSerialization.WriteProblemAsync(context, problem, context.RequestAborted).ConfigureAwait(false);
    }

    private static string GetClientId(HttpContext context)
    {
        string supplied = context.Request.Headers[ClientIdHeader].ToString();
        if (!string.IsNullOrWhiteSpace(supplied) && supplied.Length <= 256)
        {
            return supplied;
        }

        return context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    private static string? GetCollection(PathString remaining)
    {
        string[] segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length >= 2 && string.Equals(segments[0], "collections", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[1])
            : null;
    }

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Admission rejected client {ClientId}, collection {Collection}, operation {Operation}, kind {Kind}, scope {Scope}; retry after {RetryAfterSeconds}s")]
    private static partial void AdmissionRejected(
        ILogger logger,
        string clientId,
        string collection,
        AdmissionOperation operation,
        AdmissionRejectionKind kind,
        string scope,
        int retryAfterSeconds);
}
