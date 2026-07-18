using System.Globalization;

namespace SlimVector.Domain;

public sealed record MetadataValue
{
    public MetadataValueKind Kind { get; init; }

    public string? StringValue { get; init; }

    public bool? BooleanValue { get; init; }

    public long? IntegerValue { get; init; }

    public double? NumberValue { get; init; }

    public DateTimeOffset? DateTimeValue { get; init; }

    public Guid? GuidValue { get; init; }

    public string[]? StringArrayValue { get; init; }

    public bool[]? BooleanArrayValue { get; init; }

    public long[]? IntegerArrayValue { get; init; }

    public double[]? NumberArrayValue { get; init; }

    public static MetadataValue Null() => new() { Kind = MetadataValueKind.Null };

    public static MetadataValue From(string value) => new() { Kind = MetadataValueKind.Text, StringValue = value };

    public static MetadataValue From(bool value) => new() { Kind = MetadataValueKind.Boolean, BooleanValue = value };

    public static MetadataValue From(long value) => new() { Kind = MetadataValueKind.Integral, IntegerValue = value };

    public static MetadataValue From(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Metadata numbers must be finite.");
        }

        return new MetadataValue { Kind = MetadataValueKind.Number, NumberValue = value };
    }

    public static MetadataValue From(DateTimeOffset value) => new() { Kind = MetadataValueKind.DateTime, DateTimeValue = value };

    public static MetadataValue From(Guid value) => new() { Kind = MetadataValueKind.Identifier, GuidValue = value };

    public static MetadataValue From(string[] value) => new() { Kind = MetadataValueKind.TextArray, StringArrayValue = value };

    public static MetadataValue From(bool[] value) => new() { Kind = MetadataValueKind.BooleanArray, BooleanArrayValue = value };

    public static MetadataValue From(long[] value) => new() { Kind = MetadataValueKind.IntegralArray, IntegerArrayValue = value };

    public static MetadataValue From(double[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Array.Exists(value, static number => !double.IsFinite(number)))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Metadata numbers must be finite.");
        }

        return new MetadataValue { Kind = MetadataValueKind.NumberArray, NumberArrayValue = value };
    }

    public object? ToObject() => Kind switch
    {
        MetadataValueKind.Null => null,
        MetadataValueKind.Text => StringValue,
        MetadataValueKind.Boolean => BooleanValue,
        MetadataValueKind.Integral => IntegerValue,
        MetadataValueKind.Number => NumberValue,
        MetadataValueKind.DateTime => DateTimeValue,
        MetadataValueKind.Identifier => GuidValue,
        MetadataValueKind.TextArray => StringArrayValue,
        MetadataValueKind.BooleanArray => BooleanArrayValue,
        MetadataValueKind.IntegralArray => IntegerArrayValue,
        MetadataValueKind.NumberArray => NumberArrayValue,
        _ => throw new InvalidOperationException($"Unsupported metadata kind '{Kind}'."),
    };

    public string ToIndexKey() => Kind switch
    {
        MetadataValueKind.Null => "n:",
        MetadataValueKind.Text => $"s:{StringValue}",
        MetadataValueKind.Boolean => BooleanValue == true ? "b:1" : "b:0",
        MetadataValueKind.Integral => $"i:{IntegerValue?.ToString(CultureInfo.InvariantCulture)}",
        MetadataValueKind.Number => $"d:{NumberValue?.ToString("R", CultureInfo.InvariantCulture)}",
        MetadataValueKind.DateTime => $"t:{DateTimeValue?.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}",
        MetadataValueKind.Identifier => $"g:{GuidValue:D}",
        _ => throw new InvalidOperationException($"Metadata kind '{Kind}' is not a scalar."),
    };
}
