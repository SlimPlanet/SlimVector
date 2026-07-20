namespace SlimVector.Domain;

public sealed record VectorIndexConfiguration
{
    public VectorIndexKind Kind { get; init; } = VectorIndexKind.Auto;

    public int HnswM { get; init; } = 16;

    public int HnswEfConstruction { get; init; } = 200;

    public int HnswEfSearch { get; init; } = 64;

    public VectorQuantizationKind Quantization { get; init; } = VectorQuantizationKind.Float32;

    public int RerankCandidateMultiplier { get; init; } = 4;

    public int IvfListCount { get; init; } = 256;

    public int IvfProbeCount { get; init; } = 8;

    public int IvfTrainingIterations { get; init; } = 20;

    public int PqSubvectorCount { get; init; } = 8;

    public int PqCentroidCount { get; init; } = 256;

    public int PqTrainingIterations { get; init; } = 20;

    public int DiskAnnMaxDegree { get; init; } = 32;

    public int DiskAnnSearchListSize { get; init; } = 64;

    public int DiskAnnBeamWidth { get; init; } = 4;

    public int DiskAnnDeltaThreshold { get; init; } = 10_000;

    public int DiskAnnPageSize { get; init; } = 4_096;

    public int DiskAnnCachePages { get; init; } = 256;

    public int DiskAnnRetainedGenerations { get; init; } = 2;
}

public sealed record CollectionDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Dimension { get; init; }

    public required DistanceMetric Metric { get; init; }

    public VectorIndexConfiguration VectorIndex { get; init; } = new();

    public CollectionPlacement? Placement { get; init; }

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
        DomainValidation.ValidateVectorIndex(vectorIndex ?? new VectorIndexConfiguration(), dimension);

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
