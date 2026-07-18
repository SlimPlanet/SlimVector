namespace SlimVector.Domain;

public sealed record DocumentRecord
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public required float[] Vector { get; init; }

    public required Dictionary<string, MetadataValue> Metadata { get; init; }

    public long Version { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DocumentRecord DeepCopy() => this with
    {
        Vector = (float[])Vector.Clone(),
        Metadata = Metadata.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value with
            {
                StringArrayValue = pair.Value.StringArrayValue is null ? null : (string[])pair.Value.StringArrayValue.Clone(),
                BooleanArrayValue = pair.Value.BooleanArrayValue is null ? null : (bool[])pair.Value.BooleanArrayValue.Clone(),
                IntegerArrayValue = pair.Value.IntegerArrayValue is null ? null : (long[])pair.Value.IntegerArrayValue.Clone(),
                NumberArrayValue = pair.Value.NumberArrayValue is null ? null : (double[])pair.Value.NumberArrayValue.Clone(),
            },
            StringComparer.Ordinal),
    };
}

public sealed record DocumentPatch
{
    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public Dictionary<string, MetadataValue>? Metadata { get; init; }
}

public sealed record DocumentMutation
{
    public required DocumentMutationKind Kind { get; init; }

    public required string Id { get; init; }

    public DocumentRecord? Document { get; init; }

    public DocumentPatch? Patch { get; init; }
}

public sealed record DocumentMutationResult
{
    public required string Id { get; init; }

    public required bool Succeeded { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public long? Version { get; init; }
}

public sealed record BatchMutationResult
{
    public required bool Atomic { get; init; }

    public required int Succeeded { get; init; }

    public required int Failed { get; init; }

    public required IReadOnlyList<DocumentMutationResult> Results { get; init; }
}
