using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class MetadataIndex
{
    private readonly HashSet<string> _allIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _exists = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Field, string Value), HashSet<string>> _equality = new();
    private readonly Dictionary<string, SortedDictionary<double, HashSet<string>>> _ordered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, MetadataValue>> _documents = new(StringComparer.Ordinal);

    public int Count => _allIds.Count;

    internal void EnsureCapacity(IReadOnlyCollection<DocumentRecord> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _allIds.EnsureCapacity(documents.Count);
        _documents.EnsureCapacity(documents.Count);
        int fields = 0;
        foreach (DocumentRecord document in documents)
        {
            fields = checked(fields + document.Metadata.Sum(static field => ScalarCount(field.Value)));
        }

        _equality.EnsureCapacity(fields);
    }

    public void Upsert(string id, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(metadata);
        Remove(id);

        Dictionary<string, MetadataValue> copy = metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        _documents[id] = copy;
        _allIds.Add(id);
        foreach ((string field, MetadataValue value) in metadata)
        {
            GetOrAdd(_exists, field).Add(id);
            foreach (MetadataValue scalar in ExpandScalars(value))
            {
                GetOrAdd(_equality, (field, scalar.ToIndexKey())).Add(id);
                if (TryGetOrderedValue(scalar, out double orderedValue))
                {
                    if (!_ordered.TryGetValue(field, out SortedDictionary<double, HashSet<string>>? values))
                    {
                        values = new SortedDictionary<double, HashSet<string>>();
                        _ordered.Add(field, values);
                    }

                    GetOrAdd(values, orderedValue).Add(id);
                }
            }
        }
    }

    public bool Remove(string id)
    {
        if (!_documents.Remove(id, out Dictionary<string, MetadataValue>? metadata))
        {
            return false;
        }

        _allIds.Remove(id);
        foreach ((string field, MetadataValue value) in metadata)
        {
            RemoveFromSet(_exists, field, id);
            foreach (MetadataValue scalar in ExpandScalars(value))
            {
                RemoveFromSet(_equality, (field, scalar.ToIndexKey()), id);
                if (TryGetOrderedValue(scalar, out double orderedValue) && _ordered.TryGetValue(field, out SortedDictionary<double, HashSet<string>>? values))
                {
                    RemoveFromSet(values, orderedValue, id);
                    if (values.Count == 0)
                    {
                        _ordered.Remove(field);
                    }
                }
            }
        }

        return true;
    }

    public IReadOnlySet<string> Evaluate(MetadataFilter? filter)
    {
        if (filter is null)
        {
            return new HashSet<string>(_allIds, StringComparer.Ordinal);
        }

        return EvaluateCore(filter);
    }

    internal byte[] Serialize()
    {
        MetadataIndexSnapshot snapshot = new()
        {
            Documents = _documents
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MetadataDocumentSnapshot
                {
                    Id = pair.Key,
                    Fields = pair.Value
                        .OrderBy(static field => field.Key, StringComparer.Ordinal)
                        .Select(static field => new MetadataFieldSnapshot
                        {
                            Name = field.Key,
                            Value = MetadataValueSnapshot.FromDomain(field.Value),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    internal static MetadataIndex? Deserialize(ReadOnlySpan<byte> data)
    {
        MetadataIndexSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<MetadataIndexSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Documents is null ||
            snapshot.Documents.Select(static document => document.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Documents.Length ||
            snapshot.Documents.Any(static document =>
                document is null || string.IsNullOrWhiteSpace(document.Id) || document.Fields is null ||
                document.Fields.Any(static field => string.IsNullOrWhiteSpace(field.Name) || field.Value is null) ||
                document.Fields.Select(static field => field.Name).Distinct(StringComparer.Ordinal).Count() != document.Fields.Length))
        {
            return null;
        }

        MetadataIndex index = new();
        try
        {
            foreach (MetadataDocumentSnapshot document in snapshot.Documents)
            {
                Dictionary<string, MetadataValue> metadata = document.Fields.ToDictionary(
                    static field => field.Name,
                    static field => field.Value.ToDomain(),
                    StringComparer.Ordinal);
                index.Upsert(document.Id, metadata);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or DomainException)
        {
            return null;
        }

        return index;
    }

    private HashSet<string> EvaluateCore(MetadataFilter filter) => filter.Operator switch
    {
        MetadataOperator.Equal => Equal(filter),
        MetadataOperator.NotEqual => ExceptAll(Equal(filter)),
        MetadataOperator.GreaterThan => Compare(filter, static comparison => comparison > 0),
        MetadataOperator.GreaterThanOrEqual => Compare(filter, static comparison => comparison >= 0),
        MetadataOperator.LessThan => Compare(filter, static comparison => comparison < 0),
        MetadataOperator.LessThanOrEqual => Compare(filter, static comparison => comparison <= 0),
        MetadataOperator.In => In(filter),
        MetadataOperator.Exists => Exists(filter),
        MetadataOperator.And => And(filter),
        MetadataOperator.Or => Or(filter),
        MetadataOperator.Not => Not(filter),
        _ => throw InvalidFilter($"Unsupported filter operator '{filter.Operator}'."),
    };

    private HashSet<string> Equal(MetadataFilter filter)
    {
        (string field, MetadataValue value) = RequireFieldAndValue(filter);
        return _equality.TryGetValue((field, value.ToIndexKey()), out HashSet<string>? ids)
            ? new HashSet<string>(ids, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private HashSet<string> In(MetadataFilter filter)
    {
        string field = RequireField(filter);
        if (filter.Values is not { Length: > 0 })
        {
            throw InvalidFilter("The 'in' operator requires at least one value.");
        }

        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (MetadataValue value in filter.Values)
        {
            if (_equality.TryGetValue((field, value.ToIndexKey()), out HashSet<string>? ids))
            {
                result.UnionWith(ids);
            }
        }

        return result;
    }

    private HashSet<string> Exists(MetadataFilter filter)
    {
        string field = RequireField(filter);
        return _exists.TryGetValue(field, out HashSet<string>? ids)
            ? new HashSet<string>(ids, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private HashSet<string> Compare(MetadataFilter filter, Func<int, bool> predicate)
    {
        (string field, MetadataValue value) = RequireFieldAndValue(filter);
        if (!_ordered.TryGetValue(field, out SortedDictionary<double, HashSet<string>>? values) ||
            !TryGetOrderedValue(value, out double target))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        HashSet<string> result = new(StringComparer.Ordinal);
        foreach ((double candidate, HashSet<string> ids) in values)
        {
            if (predicate(candidate.CompareTo(target)))
            {
                result.UnionWith(ids);
            }
        }

        return result;
    }

    private HashSet<string> And(MetadataFilter filter)
    {
        MetadataFilter[] operands = RequireOperands(filter, 2);
        HashSet<string> result = EvaluateCore(operands[0]);
        for (int index = 1; index < operands.Length; index++)
        {
            result.IntersectWith(EvaluateCore(operands[index]));
        }

        return result;
    }

    private HashSet<string> Or(MetadataFilter filter)
    {
        MetadataFilter[] operands = RequireOperands(filter, 2);
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (MetadataFilter operand in operands)
        {
            result.UnionWith(EvaluateCore(operand));
        }

        return result;
    }

    private HashSet<string> Not(MetadataFilter filter)
    {
        MetadataFilter[] operands = RequireOperands(filter, 1);
        if (operands.Length != 1)
        {
            throw InvalidFilter("The 'not' operator requires exactly one operand.");
        }

        return ExceptAll(EvaluateCore(operands[0]));
    }

    private HashSet<string> ExceptAll(HashSet<string> excluded)
    {
        HashSet<string> result = new(_allIds, StringComparer.Ordinal);
        result.ExceptWith(excluded);
        return result;
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

    private static MetadataFilter[] RequireOperands(MetadataFilter filter, int minimum) => filter.Operands is { Length: var length } operands && length >= minimum
        ? operands
        : throw InvalidFilter($"The '{filter.Operator}' operator requires at least {minimum} operand(s).");

    private static IEnumerable<MetadataValue> ExpandScalars(MetadataValue value)
    {
        switch (value.Kind)
        {
            case MetadataValueKind.TextArray:
                return value.StringArrayValue!.Select(MetadataValue.From);
            case MetadataValueKind.BooleanArray:
                return value.BooleanArrayValue!.Select(MetadataValue.From);
            case MetadataValueKind.IntegralArray:
                return value.IntegerArrayValue!.Select(MetadataValue.From);
            case MetadataValueKind.NumberArray:
                return value.NumberArrayValue!.Select(MetadataValue.From);
            default:
                return [value];
        }
    }

    private static int ScalarCount(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.TextArray => value.StringArrayValue!.Length,
        MetadataValueKind.BooleanArray => value.BooleanArrayValue!.Length,
        MetadataValueKind.IntegralArray => value.IntegerArrayValue!.Length,
        MetadataValueKind.NumberArray => value.NumberArrayValue!.Length,
        _ => 1,
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

    private static HashSet<string> GetOrAdd<TKey>(IDictionary<TKey, HashSet<string>> dictionary, TKey key)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out HashSet<string>? value))
        {
            value = new HashSet<string>(StringComparer.Ordinal);
            dictionary.Add(key, value);
        }

        return value;
    }

    private static void RemoveFromSet<TKey>(IDictionary<TKey, HashSet<string>> dictionary, TKey key, string id)
        where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out HashSet<string>? set))
        {
            set.Remove(id);
            if (set.Count == 0)
            {
                dictionary.Remove(key);
            }
        }
    }

    private static DomainException InvalidFilter(string message) => new(ErrorCodes.InvalidFilter, message);
}

[MemoryPackable]
internal sealed partial class MetadataIndexSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public MetadataDocumentSnapshot[] Documents { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class MetadataDocumentSnapshot
{
    public string Id { get; set; } = string.Empty;

    public MetadataFieldSnapshot[] Fields { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class MetadataFieldSnapshot
{
    public string Name { get; set; } = string.Empty;

    public MetadataValueSnapshot Value { get; set; } = new();
}

[MemoryPackable]
internal sealed partial class MetadataValueSnapshot
{
    public MetadataValueKind Kind { get; set; }

    public string? StringValue { get; set; }

    public bool? BooleanValue { get; set; }

    public long? IntegerValue { get; set; }

    public double? NumberValue { get; set; }

    public DateTimeOffset? DateTimeValue { get; set; }

    public Guid? GuidValue { get; set; }

    public string[]? StringArrayValue { get; set; }

    public bool[]? BooleanArrayValue { get; set; }

    public long[]? IntegerArrayValue { get; set; }

    public double[]? NumberArrayValue { get; set; }

    public static MetadataValueSnapshot FromDomain(MetadataValue value) => new()
    {
        Kind = value.Kind,
        StringValue = value.StringValue,
        BooleanValue = value.BooleanValue,
        IntegerValue = value.IntegerValue,
        NumberValue = value.NumberValue,
        DateTimeValue = value.DateTimeValue,
        GuidValue = value.GuidValue,
        StringArrayValue = value.StringArrayValue,
        BooleanArrayValue = value.BooleanArrayValue,
        IntegerArrayValue = value.IntegerArrayValue,
        NumberArrayValue = value.NumberArrayValue,
    };

    public MetadataValue ToDomain() => Kind switch
    {
        MetadataValueKind.Null => MetadataValue.Null(),
        MetadataValueKind.Text when StringValue is not null => MetadataValue.From(StringValue),
        MetadataValueKind.Boolean when BooleanValue.HasValue => MetadataValue.From(BooleanValue.Value),
        MetadataValueKind.Integral when IntegerValue.HasValue => MetadataValue.From(IntegerValue.Value),
        MetadataValueKind.Number when NumberValue.HasValue => MetadataValue.From(NumberValue.Value),
        MetadataValueKind.DateTime when DateTimeValue.HasValue => MetadataValue.From(DateTimeValue.Value),
        MetadataValueKind.Identifier when GuidValue.HasValue => MetadataValue.From(GuidValue.Value),
        MetadataValueKind.TextArray when StringArrayValue is not null => MetadataValue.From(StringArrayValue),
        MetadataValueKind.BooleanArray when BooleanArrayValue is not null => MetadataValue.From(BooleanArrayValue),
        MetadataValueKind.IntegralArray when IntegerArrayValue is not null => MetadataValue.From(IntegerArrayValue),
        MetadataValueKind.NumberArray when NumberArrayValue is not null => MetadataValue.From(NumberArrayValue),
        _ => throw new InvalidOperationException($"Persisted metadata value '{Kind}' is invalid."),
    };
}
