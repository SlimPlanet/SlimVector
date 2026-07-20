using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SlimVector.Protocol;

[assembly: MessagePackKnownFormatter(typeof(JsonElementMessagePackFormatter))]
[assembly: MessagePackKnownFormatter(typeof(VectorIndexConfigurationMessagePackFormatter))]

namespace SlimVector.Api;

[GeneratedMessagePackResolver]
internal sealed partial class ApiMessagePackResolver;

internal static class ApiSerialization
{
    public const string MessagePackMediaType = "application/vnd.msgpack";

    public static MessagePackSerializerOptions MessagePackOptions => MessagePackOptionsCache.Value;

    public static bool IsMessagePackRequest(HttpRequest request) =>
        HasMediaType(request.ContentType, MessagePackMediaType);

    public static bool WantsMessagePack(HttpRequest request)
    {
        IList<MediaTypeHeaderValue>? accepted = request.GetTypedHeaders().Accept;
        if (accepted is null || accepted.Count == 0)
        {
            return false;
        }

        double messagePackQuality = QualityFor(accepted, MessagePackMediaType);
        double jsonQuality = Math.Max(
            QualityFor(accepted, "application/json"),
            QualityFor(accepted, "*/*"));
        return messagePackQuality > 0 && messagePackQuality >= jsonQuality;
    }

    public static IResult Ok<T>(T value) => new NegotiatedResult<T>(value, StatusCodes.Status200OK, location: null);

    public static IResult Created<T>(string location, T value) =>
        new NegotiatedResult<T>(value, StatusCodes.Status201Created, location);

    public static async Task WriteAsync<T>(
        HttpContext context,
        T value,
        int statusCode,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        if (WantsMessagePack(context.Request))
        {
            context.Response.ContentType = MessagePackMediaType;
            await MessagePackSerializer.SerializeAsync(
                context.Response.Body,
                value,
                MessagePackOptions,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonTypeInfo<T> typeInfo = GetJsonTypeInfo<T>();
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteProblemAsync(
        HttpContext context,
        ProblemDetails problem,
        CancellationToken cancellationToken)
    {
        if (WantsMessagePack(context.Request))
        {
            MessagePackProblemDetails wire = new()
            {
                Type = problem.Type,
                Title = problem.Title,
                Status = problem.Status,
                Detail = problem.Detail,
                Instance = problem.Instance,
                Code = ExtensionString(problem, "code"),
                TraceId = ExtensionString(problem, "traceId"),
                RaftGroup = ExtensionString(problem, "raftGroup"),
                LeaderEndpoint = ExtensionString(problem, "leaderEndpoint"),
                LeaderApiEndpoint = ExtensionString(problem, "leaderApiEndpoint"),
                RejectionKind = ExtensionString(problem, "rejectionKind"),
                Scope = ExtensionString(problem, "scope"),
                RetryAfterSeconds = ExtensionInt32(problem, "retryAfterSeconds"),
                AdaptiveRateRatio = ExtensionDouble(problem, "adaptiveRateRatio"),
                Pressure = ExtensionDouble(problem, "pressure"),
            };
            await WriteAsync(context, wire, problem.Status ?? StatusCodes.Status500InternalServerError, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            ApiJsonContext.Default.ProblemDetails,
            cancellationToken).ConfigureAwait(false);
    }

    public static JsonTypeInfo<T> GetJsonTypeInfo<T>() => JsonTypeInfoCache<T>.Value;

    private static class MessagePackOptionsCache
    {
        public static MessagePackSerializerOptions Value { get; } =
            MessagePackSerializerOptions.Standard
                .WithResolver(CompositeResolver.Create(
                    [new JsonElementMessagePackFormatter(), new VectorIndexConfigurationMessagePackFormatter()],
                    [ApiMessagePackResolver.Instance, StandardResolver.Instance]))
                .WithSecurity(MessagePackSecurity.UntrustedData);
    }

    private static class JsonTypeInfoCache<T>
    {
        public static JsonTypeInfo<T> Value { get; } =
            ApiJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
            ?? throw new InvalidOperationException($"No generated JSON contract exists for '{typeof(T)}'.");
    }

    private static double QualityFor(IEnumerable<MediaTypeHeaderValue> accepted, string mediaType) => accepted
        .Where(item => string.Equals(item.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase))
        .Select(static item => item.Quality ?? 1)
        .DefaultIfEmpty(0)
        .Max();

    private static bool HasMediaType(string? value, string expected) =>
        MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? parsed) &&
        string.Equals(parsed.MediaType.Value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? ExtensionString(ProblemDetails problem, string key) =>
        problem.Extensions.TryGetValue(key, out object? value) ? value?.ToString() : null;

    private static int? ExtensionInt32(ProblemDetails problem, string key) =>
        problem.Extensions.TryGetValue(key, out object? value) && value is int integer ? integer : null;

    private static double? ExtensionDouble(ProblemDetails problem, string key) =>
        problem.Extensions.TryGetValue(key, out object? value) && value is double number ? number : null;
}

internal readonly record struct NegotiatedBody<T>(T Value)
{
    public static async ValueTask<NegotiatedBody<T>> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);
        T? value;
        try
        {
            value = ApiSerialization.IsMessagePackRequest(context.Request)
                ? await MessagePackSerializer.DeserializeAsync<T>(
                    context.Request.Body,
                    ApiSerialization.MessagePackOptions,
                    context.RequestAborted).ConfigureAwait(false)
                : await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    ApiSerialization.GetJsonTypeInfo<T>(),
                    context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MessagePackSerializationException or JsonException)
        {
            throw new BadHttpRequestException($"The {parameter.Name ?? "request"} body is invalid.", exception);
        }

        return new NegotiatedBody<T>(value ?? throw new BadHttpRequestException(
            $"The {parameter.Name ?? "request"} body is empty."));
    }
}

internal sealed class NegotiatedResult<T>(T value, int statusCode, string? location) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        if (!ApiSerialization.WantsMessagePack(httpContext.Request))
        {
            return location is null
                ? TypedResults.Ok(value).ExecuteAsync(httpContext)
                : TypedResults.Created(location, value).ExecuteAsync(httpContext);
        }

        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }

        return ApiSerialization.WriteAsync(httpContext, value, statusCode, httpContext.RequestAborted);
    }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record MessagePackProblemDetails
{
    [Key("type")]
    public string? Type { get; init; }

    [Key("title")]
    public string? Title { get; init; }

    [Key("status")]
    public int? Status { get; init; }

    [Key("detail")]
    public string? Detail { get; init; }

    [Key("instance")]
    public string? Instance { get; init; }

    [Key("code")]
    public string? Code { get; init; }

    [Key("traceId")]
    public string? TraceId { get; init; }

    [Key("raftGroup")]
    public string? RaftGroup { get; init; }

    [Key("leaderEndpoint")]
    public string? LeaderEndpoint { get; init; }

    [Key("leaderApiEndpoint")]
    public string? LeaderApiEndpoint { get; init; }

    [Key("rejectionKind")]
    public string? RejectionKind { get; init; }

    [Key("scope")]
    public string? Scope { get; init; }

    [Key("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }

    [Key("adaptiveRateRatio")]
    public double? AdaptiveRateRatio { get; init; }

    [Key("pressure")]
    public double? Pressure { get; init; }
}
