namespace SlimVector.Domain;

public sealed record VectorIndexConfiguration
{
    public VectorIndexKind Kind { get; init; } = VectorIndexKind.Auto;

    public int HnswM { get; init; } = 16;

    public int HnswEfConstruction { get; init; } = 200;

    public int HnswEfSearch { get; init; } = 64;
}

public sealed record CollectionDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Dimension { get; init; }

    public required DistanceMetric Metric { get; init; }

    public VectorIndexConfiguration VectorIndex { get; init; } = new();

    public bool MetadataIndexed { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public static CollectionDefinition Create(
        string name,
        int dimension,
        DistanceMetric metric,
        VectorIndexConfiguration? vectorIndex = null,
        TimeProvider? timeProvider = null,
        bool metadataIndexed = true)
    {
        DomainValidation.ValidateCollectionName(name);
        DomainValidation.ValidateDimension(dimension);
        DomainValidation.ValidateVectorIndex(vectorIndex ?? new VectorIndexConfiguration());

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CollectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Dimension = dimension,
            Metric = metric,
            VectorIndex = vectorIndex ?? new VectorIndexConfiguration(),
            MetadataIndexed = metadataIndexed,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
