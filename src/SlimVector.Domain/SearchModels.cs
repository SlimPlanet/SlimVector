namespace SlimVector.Domain;

public sealed record MetadataFilter
{
    public required MetadataOperator Operator { get; init; }

    public string? Field { get; init; }

    public MetadataValue? Value { get; init; }

    public MetadataValue[]? Values { get; init; }

    public MetadataFilter[]? Operands { get; init; }
}

public sealed record SearchRequest
{
    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    public int Limit { get; init; } = 10;

    public MetadataFilter? Filter { get; init; }

    public IncludeFields Include { get; init; } = IncludeFields.Text | IncludeFields.Metadata | IncludeFields.Scores;

    public ReadConsistency Consistency { get; init; } = ReadConsistency.Leader;

    public double VectorWeight { get; init; } = 0.5;

    public double TextWeight { get; init; } = 0.5;
}

public sealed record SearchHit
{
    public required string Id { get; init; }

    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public Dictionary<string, MetadataValue>? Metadata { get; init; }

    public double? Score { get; init; }

    public int? VectorRank { get; init; }

    public int? TextRank { get; init; }
}

public sealed record SearchResponse
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    public required long TookMicroseconds { get; init; }
}
