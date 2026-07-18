using SlimVector.Domain;

namespace SlimVector.Indexing;

internal static class MetadataFilterEvaluator
{
    public static bool Matches(IReadOnlyDictionary<string, MetadataValue> metadata, MetadataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Operator switch
        {
            MetadataOperator.Equal => Equal(metadata, filter),
            MetadataOperator.NotEqual => !Equal(metadata, filter),
            MetadataOperator.GreaterThan => Compare(metadata, filter, static comparison => comparison > 0),
            MetadataOperator.GreaterThanOrEqual => Compare(metadata, filter, static comparison => comparison >= 0),
            MetadataOperator.LessThan => Compare(metadata, filter, static comparison => comparison < 0),
            MetadataOperator.LessThanOrEqual => Compare(metadata, filter, static comparison => comparison <= 0),
            MetadataOperator.In => In(metadata, filter),
            MetadataOperator.Exists => metadata.ContainsKey(RequireField(filter)),
            MetadataOperator.And => RequireOperands(filter, 2).All(operand => Matches(metadata, operand)),
            MetadataOperator.Or => RequireOperands(filter, 2).Any(operand => Matches(metadata, operand)),
            MetadataOperator.Not => Not(metadata, filter),
            _ => throw InvalidFilter($"Unsupported filter operator '{filter.Operator}'."),
        };
    }

    private static bool Equal(IReadOnlyDictionary<string, MetadataValue> metadata, MetadataFilter filter)
    {
        (string field, MetadataValue target) = RequireFieldAndValue(filter);
        return metadata.TryGetValue(field, out MetadataValue? actual) &&
            ExpandScalars(actual).Any(value => string.Equals(value.ToIndexKey(), target.ToIndexKey(), StringComparison.Ordinal));
    }

    private static bool In(IReadOnlyDictionary<string, MetadataValue> metadata, MetadataFilter filter)
    {
        string field = RequireField(filter);
        if (filter.Values is not { Length: > 0 })
        {
            throw InvalidFilter("The 'in' operator requires at least one value.");
        }

        if (!metadata.TryGetValue(field, out MetadataValue? actual))
        {
            return false;
        }

        HashSet<string> targets = filter.Values.Select(static value => value.ToIndexKey()).ToHashSet(StringComparer.Ordinal);
        return ExpandScalars(actual).Any(value => targets.Contains(value.ToIndexKey()));
    }

    private static bool Compare(
        IReadOnlyDictionary<string, MetadataValue> metadata,
        MetadataFilter filter,
        Func<int, bool> predicate)
    {
        (string field, MetadataValue targetValue) = RequireFieldAndValue(filter);
        if (!metadata.TryGetValue(field, out MetadataValue? actual) || !TryGetOrderedValue(targetValue, out double target))
        {
            return false;
        }

        return ExpandScalars(actual).Any(value =>
            TryGetOrderedValue(value, out double candidate) && predicate(candidate.CompareTo(target)));
    }

    private static bool Not(IReadOnlyDictionary<string, MetadataValue> metadata, MetadataFilter filter)
    {
        MetadataFilter[] operands = RequireOperands(filter, 1);
        if (operands.Length != 1)
        {
            throw InvalidFilter("The 'not' operator requires exactly one operand.");
        }

        return !Matches(metadata, operands[0]);
    }

    private static (string Field, MetadataValue Value) RequireFieldAndValue(MetadataFilter filter)
    {
        string field = RequireField(filter);
        return filter.Value is null
            ? throw InvalidFilter($"The '{filter.Operator}' operator requires a value.")
            : (field, filter.Value);
    }

    private static string RequireField(MetadataFilter filter) => string.IsNullOrWhiteSpace(filter.Field)
        ? throw InvalidFilter($"The '{filter.Operator}' operator requires a field.")
        : filter.Field;

    private static MetadataFilter[] RequireOperands(MetadataFilter filter, int minimum) =>
        filter.Operands is { Length: var length } operands && length >= minimum
            ? operands
            : throw InvalidFilter($"The '{filter.Operator}' operator requires at least {minimum} operand(s).");

    private static IEnumerable<MetadataValue> ExpandScalars(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.TextArray => value.StringArrayValue!.Select(MetadataValue.From),
        MetadataValueKind.BooleanArray => value.BooleanArrayValue!.Select(MetadataValue.From),
        MetadataValueKind.IntegralArray => value.IntegerArrayValue!.Select(MetadataValue.From),
        MetadataValueKind.NumberArray => value.NumberArrayValue!.Select(MetadataValue.From),
        _ => [value],
    };

    private static bool TryGetOrderedValue(MetadataValue value, out double ordered)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.Integral:
                ordered = value.IntegerValue!.Value;
                return true;
            case MetadataValueKind.Number:
                ordered = value.NumberValue!.Value;
                return true;
            case MetadataValueKind.DateTime:
                ordered = value.DateTimeValue!.Value.ToUniversalTime().Ticks;
                return true;
            default:
                ordered = 0;
                return false;
        }
    }

    private static DomainException InvalidFilter(string message) => new(ErrorCodes.InvalidFilter, message);
}
