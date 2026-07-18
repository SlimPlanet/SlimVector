using System.Buffers;
using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Api.Contracts;

internal static class ContractMapper
{
    public static CollectionResponse ToResponse(CollectionDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        Dimension = definition.Dimension,
        Metric = definition.Metric,
        VectorIndex = definition.VectorIndex,
        MetadataIndexed = definition.MetadataIndexed,
        CreatedAt = definition.CreatedAt,
        UpdatedAt = definition.UpdatedAt,
    };

    public static DocumentRecord ToDocument(DocumentInput input) => new()
    {
        Id = input.Id,
        Text = input.Text,
        Vector = input.Vector,
        Metadata = ToMetadata(input.Metadata),
    };

    public static DocumentPatch ToPatch(DocumentUpdateInput input) => new()
    {
        Text = input.Text,
        Vector = input.Vector,
        Metadata = input.Metadata is null ? null : ToMetadata(input.Metadata),
    };

    public static DocumentResponse ToResponse(DocumentRecord document) => new()
    {
        Id = document.Id,
        Text = document.Text,
        Vector = document.Vector,
        Metadata = FromMetadata(document.Metadata),
        Version = document.Version,
        UpdatedAt = document.UpdatedAt,
    };

    public static QueryResponse ToResponse(SearchResponse response) => new()
    {
        Hits = response.Hits.Select(static hit => new QueryHitResponse
        {
            Id = hit.Id,
            Text = hit.Text,
            Vector = hit.Vector,
            Metadata = hit.Metadata is null ? null : FromMetadata(hit.Metadata),
            Score = hit.Score,
            VectorRank = hit.VectorRank,
            TextRank = hit.TextRank,
        }).ToArray(),
        TookMicroseconds = response.TookMicroseconds,
    };

    public static BatchMutationResponse ToResponse(BatchMutationResult result) => new()
    {
        Atomic = result.Atomic,
        Succeeded = result.Succeeded,
        Failed = result.Failed,
        Results = result.Results.Select(static item => new MutationItemResponse
        {
            Id = item.Id,
            Succeeded = item.Succeeded,
            ErrorCode = item.ErrorCode,
            ErrorMessage = item.ErrorMessage,
            Version = item.Version,
        }).ToArray(),
    };

    public static SearchRequest ToSearchRequest(QueryRequest request) => new()
    {
        Text = request.Text,
        Vector = request.Vector,
        Mode = request.Mode ?? SearchMode.Hybrid,
        Limit = request.Limit ?? 10,
        Filter = request.Filter is null ? null : ToFilter(request.Filter),
        Include = ParseInclude(request.Include),
        Consistency = request.Consistency ?? ReadConsistency.Leader,
        VectorWeight = request.VectorWeight ?? 0.5,
        TextWeight = request.TextWeight ?? 0.5,
    };

    private static MetadataFilter ToFilter(MetadataFilterInput input) => new()
    {
        Operator = input.Operator,
        Field = input.Field,
        Value = input.Value is { } value ? ToMetadataValue(value) : null,
        Values = input.Values?.Select(ToMetadataValue).ToArray(),
        Operands = input.Operands?.Select(ToFilter).ToArray(),
    };

    private static Dictionary<string, MetadataValue> ToMetadata(Dictionary<string, JsonElement>? metadata) =>
        metadata?.ToDictionary(static pair => pair.Key, static pair => ToMetadataValue(pair.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

    private static MetadataValue ToMetadataValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => MetadataValue.Null(),
        JsonValueKind.String => MetadataValue.From(element.GetString()!),
        JsonValueKind.True => MetadataValue.From(true),
        JsonValueKind.False => MetadataValue.From(false),
        JsonValueKind.Number when element.TryGetInt64(out long integer) => MetadataValue.From(integer),
        JsonValueKind.Number => MetadataValue.From(element.GetDouble()),
        JsonValueKind.Array => ToArrayValue(element),
        JsonValueKind.Object => ToTaggedValue(element),
        _ => throw new DomainException(ErrorCodes.InvalidMetadata, $"Unsupported metadata JSON value '{element.ValueKind}'."),
    };

    private static MetadataValue ToArrayValue(JsonElement element)
    {
        JsonElement.ArrayEnumerator values = element.EnumerateArray();
        if (!values.MoveNext())
        {
            return MetadataValue.From(Array.Empty<string>());
        }

        JsonValueKind kind = values.Current.ValueKind;
        return kind switch
        {
            JsonValueKind.String when AllAre(element, JsonValueKind.String) =>
                MetadataValue.From(element.EnumerateArray().Select(static value => value.GetString()!).ToArray()),
            JsonValueKind.True or JsonValueKind.False when AllAreBoolean(element) =>
                MetadataValue.From(element.EnumerateArray().Select(static value => value.GetBoolean()).ToArray()),
            JsonValueKind.Number when AllAreIntegral(element) =>
                MetadataValue.From(element.EnumerateArray().Select(static value => value.GetInt64()).ToArray()),
            JsonValueKind.Number when AllAre(element, JsonValueKind.Number) =>
                MetadataValue.From(element.EnumerateArray().Select(static value => value.GetDouble()).ToArray()),
            _ => throw new DomainException(ErrorCodes.InvalidMetadata, "Metadata arrays must contain simple values of one type."),
        };
    }

    private static MetadataValue ToTaggedValue(JsonElement element)
    {
        if (element.EnumerateObject().Count() != 1)
        {
            throw new DomainException(ErrorCodes.InvalidMetadata, "Metadata objects must be a $date or $guid tagged value.");
        }

        if (element.TryGetProperty("$date", out JsonElement date) &&
            date.ValueKind == JsonValueKind.String &&
            date.TryGetDateTimeOffset(out DateTimeOffset parsedDate))
        {
            return MetadataValue.From(parsedDate);
        }

        if (element.TryGetProperty("$guid", out JsonElement guid) &&
            guid.ValueKind == JsonValueKind.String &&
            guid.TryGetGuid(out Guid parsedGuid))
        {
            return MetadataValue.From(parsedGuid);
        }

        throw new DomainException(ErrorCodes.InvalidMetadata, "Metadata tagged value is invalid.");
    }

    private static Dictionary<string, JsonElement> FromMetadata(IReadOnlyDictionary<string, MetadataValue> metadata) =>
        metadata.ToDictionary(static pair => pair.Key, static pair => ToJsonElement(pair.Value), StringComparer.Ordinal);

    private static JsonElement ToJsonElement(MetadataValue value)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteMetadataValue(writer, value);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteMetadataValue(Utf8JsonWriter writer, MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Null:
                writer.WriteNullValue();
                break;
            case MetadataValueKind.Text:
                writer.WriteStringValue(value.StringValue);
                break;
            case MetadataValueKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue!.Value);
                break;
            case MetadataValueKind.Integral:
                writer.WriteNumberValue(value.IntegerValue!.Value);
                break;
            case MetadataValueKind.Number:
                writer.WriteNumberValue(value.NumberValue!.Value);
                break;
            case MetadataValueKind.DateTime:
                writer.WriteStartObject();
                writer.WriteString("$date", value.DateTimeValue!.Value);
                writer.WriteEndObject();
                break;
            case MetadataValueKind.Identifier:
                writer.WriteStartObject();
                writer.WriteString("$guid", value.GuidValue!.Value);
                writer.WriteEndObject();
                break;
            case MetadataValueKind.TextArray:
                writer.WriteStartArray();
                foreach (string item in value.StringArrayValue!)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            case MetadataValueKind.BooleanArray:
                writer.WriteStartArray();
                foreach (bool item in value.BooleanArrayValue!)
                {
                    writer.WriteBooleanValue(item);
                }

                writer.WriteEndArray();
                break;
            case MetadataValueKind.IntegralArray:
                writer.WriteStartArray();
                foreach (long item in value.IntegerArrayValue!)
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
                break;
            case MetadataValueKind.NumberArray:
                writer.WriteStartArray();
                foreach (double item in value.NumberArrayValue!)
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unknown metadata kind.");
        }
    }

    private static IncludeFields ParseInclude(string[]? include)
    {
        if (include is null)
        {
            return IncludeFields.Text | IncludeFields.Metadata | IncludeFields.Scores;
        }

        IncludeFields result = IncludeFields.None;
        foreach (string field in include)
        {
            result |= field.ToLowerInvariant() switch
            {
                "text" => IncludeFields.Text,
                "vector" => IncludeFields.Vector,
                "metadata" => IncludeFields.Metadata,
                "scores" => IncludeFields.Scores,
                "all" => IncludeFields.All,
                _ => throw new DomainException("invalid_include", $"Unknown include field '{field}'."),
            };
        }

        return result;
    }

    private static bool AllAre(JsonElement element, JsonValueKind kind) =>
        element.EnumerateArray().All(value => value.ValueKind == kind);

    private static bool AllAreBoolean(JsonElement element) =>
        element.EnumerateArray().All(static value => value.ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static bool AllAreIntegral(JsonElement element) =>
        element.EnumerateArray().All(static value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _));
}
