using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Net.Http.Headers;
using SlimVector.Domain;
using SlimVector.Protocol;
using SlimVector.Studio.Contracts;

[assembly: MessagePackKnownFormatter(typeof(JsonElementMessagePackFormatter))]

namespace SlimVector.Studio;

[GeneratedMessagePackResolver]
internal sealed partial class StudioMessagePackResolver;

internal static class StudioSerialization
{
    public const string MessagePackMediaType = "application/vnd.msgpack";

    public static MessagePackSerializerOptions MessagePackOptions { get; } =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                [new JsonElementMessagePackFormatter()],
                [StudioMessagePackResolver.Instance, StandardResolver.Instance]))
            .WithSecurity(MessagePackSecurity.UntrustedData);

    public static async ValueTask<SearchInput> ReadSearchAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (HasMediaType(request.ContentType, MessagePackMediaType))
            {
                StudioMessagePackSearchRequest? wire = await MessagePackSerializer.DeserializeAsync<StudioMessagePackSearchRequest>(
                    request.Body,
                    MessagePackOptions,
                    cancellationToken).ConfigureAwait(false);
                return (wire ?? throw new BadHttpRequestException("Le corps de la requête est vide."))
                    .ToContract();
            }

            if (HasMediaType(request.ContentType, "application/json"))
            {
                return await request.ReadFromJsonAsync<SearchInput>(cancellationToken).ConfigureAwait(false)
                    ?? throw new BadHttpRequestException("Le corps de la requête est vide.");
            }

            throw new BadHttpRequestException(
                "Utilisez application/json ou application/vnd.msgpack pour cette requête.",
                StatusCodes.Status415UnsupportedMediaType);
        }
        catch (Exception exception) when (exception is MessagePackSerializationException or JsonException)
        {
            throw new BadHttpRequestException("Le corps de la requête est invalide.", exception);
        }
    }

    public static IResult Ok<T>(T value) => new StudioNegotiatedResult<T>(value);

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

    public static async Task WriteAsync<T>(
        HttpContext context,
        T value,
        int statusCode,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = MessagePackMediaType;
        await MessagePackSerializer.SerializeAsync(
            context.Response.Body,
            value,
            MessagePackOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteProblemAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        string detail)
    {
        if (!WantsMessagePack(context.Request))
        {
            return Results.Problem(
                detail: detail,
                instance: context.Request.Path,
                statusCode: status,
                title: title,
                type: $"https://slimvector.dev/problems/{code}",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["traceId"] = context.TraceIdentifier,
                }).ExecuteAsync(context);
        }

        return WriteAsync(
            context,
            new StudioMessagePackProblem
            {
                Type = $"https://slimvector.dev/problems/{code}",
                Title = title,
                Status = status,
                Detail = detail,
                Instance = context.Request.Path,
                Code = code,
                TraceId = context.TraceIdentifier,
            },
            status,
            context.RequestAborted);
    }

    private static double QualityFor(IEnumerable<MediaTypeHeaderValue> accepted, string mediaType) => accepted
        .Where(item => string.Equals(item.MediaType.Value, mediaType, StringComparison.OrdinalIgnoreCase))
        .Select(static item => item.Quality ?? 1)
        .DefaultIfEmpty(0)
        .Max();

    private static bool HasMediaType(string? value, string expected) =>
        MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? parsed) &&
        string.Equals(parsed.MediaType.Value, expected, StringComparison.OrdinalIgnoreCase);
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record StudioMessagePackMetadataFilter
{
    [Key("operator")]
    [MessagePackFormatter(typeof(MetadataOperatorMessagePackFormatter))]
    public MetadataOperator Operator { get; init; }

    [Key("field")]
    public string? Field { get; init; }

    [Key("value")]
    public JsonElement? Value { get; init; }

    [Key("values")]
    public JsonElement[]? Values { get; init; }

    [Key("operands")]
    public StudioMessagePackMetadataFilter[]? Operands { get; init; }

    public MetadataFilterInput ToContract() => new()
    {
        Operator = Operator,
        Field = Field,
        Value = Value.GetValueOrDefault(),
        Values = Values,
        Operands = Operands?.Select(static operand => operand.ToContract()).ToArray(),
    };
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record StudioMessagePackSearchRequest
{
    [Key("query")]
    public string? Query { get; init; }

    [Key("vector")]
    public float[]? Vector { get; init; }

    [Key("mode")]
    [MessagePackFormatter(typeof(NullableSearchModeMessagePackFormatter))]
    public SearchMode? Mode { get; init; }

    [Key("limit")]
    public int? Limit { get; init; }

    [Key("filter")]
    public StudioMessagePackMetadataFilter? Filter { get; init; }

    [Key("consistency")]
    [MessagePackFormatter(typeof(NullableReadConsistencyMessagePackFormatter))]
    public ReadConsistency? Consistency { get; init; }

    [Key("vectorWeight")]
    public double? VectorWeight { get; init; }

    [Key("textWeight")]
    public double? TextWeight { get; init; }

    [Key("includeText")]
    public bool? IncludeText { get; init; }

    [Key("includeVector")]
    public bool? IncludeVector { get; init; }

    [Key("includeMetadata")]
    public bool? IncludeMetadata { get; init; }

    [Key("includeScores")]
    public bool? IncludeScores { get; init; }

    public SearchInput ToContract() => new()
    {
        Query = Query ?? string.Empty,
        Vector = Vector,
        Mode = Mode ?? SearchMode.Hybrid,
        Limit = Limit ?? 10,
        Filter = Filter?.ToContract(),
        Consistency = Consistency ?? ReadConsistency.Leader,
        VectorWeight = VectorWeight ?? 0.5,
        TextWeight = TextWeight ?? 0.5,
        IncludeText = IncludeText ?? true,
        IncludeVector = IncludeVector ?? false,
        IncludeMetadata = IncludeMetadata ?? true,
        IncludeScores = IncludeScores ?? true,
    };
}

internal sealed class StudioNegotiatedResult<T>(T value) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) =>
        StudioSerialization.WantsMessagePack(httpContext.Request)
            ? StudioSerialization.WriteAsync(
                httpContext,
                value,
                StatusCodes.Status200OK,
                httpContext.RequestAborted)
            : TypedResults.Ok(value).ExecuteAsync(httpContext);
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record StudioMessagePackProblem
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
}
