using System.Text.Json;
using SlimVector.Domain;
using SlimVector.Studio.Contracts;

namespace SlimVector.Studio.Services;

internal static class MetadataJson
{
    public static Dictionary<string, MetadataValue> FromJson(IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        }

        return values.ToDictionary(
            static pair => pair.Key,
            static pair => FromElement(pair.Value),
            StringComparer.Ordinal);
    }

    public static Dictionary<string, JsonElement> ToJson(IReadOnlyDictionary<string, MetadataValue> values) =>
        values.ToDictionary(
            static pair => pair.Key,
            static pair => JsonSerializer.SerializeToElement(pair.Value.ToObject()),
            StringComparer.Ordinal);

    public static MetadataFilter ToFilter(MetadataFilterInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new MetadataFilter
        {
            Operator = input.Operator,
            Field = input.Field,
            Value = input.Value.ValueKind == JsonValueKind.Undefined ? null : FromElement(input.Value),
            Values = input.Values?.Select(FromElement).ToArray(),
            Operands = input.Operands?.Select(ToFilter).ToArray(),
        };
    }

    public static MetadataValue FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => MetadataValue.Null(),
        JsonValueKind.String => MetadataValue.From(element.GetString() ?? string.Empty),
        JsonValueKind.True => MetadataValue.From(true),
        JsonValueKind.False => MetadataValue.From(false),
        JsonValueKind.Number when element.TryGetInt64(out long integer) => MetadataValue.From(integer),
        JsonValueKind.Number => MetadataValue.From(element.GetDouble()),
        JsonValueKind.Array => FromArray(element),
        _ => throw new ArgumentException("Metadata values must be scalar values or homogeneous scalar arrays."),
    };

    private static MetadataValue FromArray(JsonElement element)
    {
        JsonElement[] items = element.EnumerateArray().ToArray();
        if (items.Length == 0 || items.All(static item => item.ValueKind == JsonValueKind.String))
        {
            return MetadataValue.From(items.Select(static item => item.GetString() ?? string.Empty).ToArray());
        }

        if (items.All(static item => item.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return MetadataValue.From(items.Select(static item => item.GetBoolean()).ToArray());
        }

        if (items.All(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out _)))
        {
            return MetadataValue.From(items.Select(static item => item.GetInt64()).ToArray());
        }

        if (items.All(static item => item.ValueKind == JsonValueKind.Number))
        {
            return MetadataValue.From(items.Select(static item => item.GetDouble()).ToArray());
        }

        throw new ArgumentException("Metadata arrays must contain values of one scalar type.");
    }
}
