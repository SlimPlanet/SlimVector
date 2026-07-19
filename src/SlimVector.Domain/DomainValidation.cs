using System.Numerics;
using System.Text.RegularExpressions;

namespace SlimVector.Domain;

public static partial class DomainValidation
{
    public const int MaximumDimension = 65_536;
    public const int MaximumDocumentIdLength = 512;
    public const int MaximumMetadataKeyLength = 256;

    public static void ValidateCollectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!CollectionNamePattern().IsMatch(name))
        {
            throw new DomainException(
                ErrorCodes.InvalidCollectionName,
                "A collection name must be 1-128 characters and contain only letters, digits, '.', '_' or '-'.");
        }
    }

    public static void ValidateDimension(int dimension)
    {
        if (dimension is < 1 or > MaximumDimension)
        {
            throw new DomainException(
                ErrorCodes.InvalidDimension,
                $"Dimension must be between 1 and {MaximumDimension}.");
        }
    }

    public static void ValidateVectorIndex(VectorIndexConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.HnswM is < 2 or > 128)
        {
            throw new DomainException(ErrorCodes.InvalidIndexConfiguration, "HNSW M must be between 2 and 128.");
        }

        if (configuration.HnswEfConstruction < configuration.HnswM || configuration.HnswEfConstruction > 4_096)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                "HNSW efConstruction must be at least M and no greater than 4096.");
        }

        if (configuration.HnswEfSearch is < 1 or > 4_096)
        {
            throw new DomainException(ErrorCodes.InvalidIndexConfiguration, "HNSW efSearch must be between 1 and 4096.");
        }

        if (!Enum.IsDefined(configuration.Kind) || !Enum.IsDefined(configuration.Quantization))
        {
            throw new DomainException(ErrorCodes.InvalidIndexConfiguration, "The vector index kind or quantization kind is invalid.");
        }

        if (configuration.RerankCandidateMultiplier is < 1 or > 100)
        {
            throw new DomainException(ErrorCodes.InvalidIndexConfiguration, "The re-rank candidate multiplier must be between 1 and 100.");
        }

        if (configuration.IvfListCount is < 1 or > 65_536 ||
            configuration.IvfProbeCount is < 1 || configuration.IvfProbeCount > configuration.IvfListCount ||
            configuration.IvfTrainingIterations is < 1 or > 1_000)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                "IVF list count must be 1-65536, probe count must be 1-list count, and training iterations must be 1-1000.");
        }

        if (configuration.PqSubvectorCount is < 1 or > 1_024 ||
            configuration.PqCentroidCount is < 2 or > 256 ||
            configuration.PqTrainingIterations is < 1 or > 1_000)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                "PQ subvector count must be 1-1024, centroid count must be 2-256, and training iterations must be 1-1000.");
        }

        if (configuration.DiskAnnMaxDegree is < 2 or > 512 ||
            configuration.DiskAnnSearchListSize < configuration.DiskAnnMaxDegree ||
            configuration.DiskAnnSearchListSize > 16_384 ||
            configuration.DiskAnnBeamWidth is < 1 or > 256 ||
            configuration.DiskAnnDeltaThreshold < 1 ||
            configuration.DiskAnnPageSize is < 512 or > 1_048_576 ||
            !BitOperations.IsPow2(configuration.DiskAnnPageSize) ||
            configuration.DiskAnnCachePages is < 1 or > 1_000_000 ||
            configuration.DiskAnnRetainedGenerations is < 2 or > 64)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                "DiskANN degree, search-list, beam-width, or delta-threshold configuration is invalid.");
        }
    }

    public static void ValidateVectorIndex(VectorIndexConfiguration configuration, int dimension)
    {
        ValidateVectorIndex(configuration);
        ValidateDimension(dimension);
        if (configuration.Kind == VectorIndexKind.IvfPq && dimension % configuration.PqSubvectorCount != 0)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                $"PQ subvector count {configuration.PqSubvectorCount} must divide vector dimension {dimension}.");
        }
    }

    public static void ValidateDocument(DocumentRecord document, int dimension)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocumentId(document.Id);
        ValidateVector(document.Vector, dimension);
        ArgumentNullException.ThrowIfNull(document.Text);
        ArgumentNullException.ThrowIfNull(document.Metadata);

        foreach ((string key, MetadataValue value) in document.Metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumMetadataKeyLength)
            {
                throw new DomainException(
                    ErrorCodes.InvalidMetadata,
                    $"Metadata keys must be non-empty and no longer than {MaximumMetadataKeyLength} characters.");
            }

            ArgumentNullException.ThrowIfNull(value);
            ValidateMetadataValue(value);
        }
    }

    public static void ValidateDocumentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumDocumentIdLength)
        {
            throw new DomainException(
                ErrorCodes.InvalidDocumentId,
                $"Document ids must be non-empty and no longer than {MaximumDocumentIdLength} characters.");
        }
    }

    public static void ValidateVector(float[] vector, int dimension)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != dimension)
        {
            throw new DomainException(
                ErrorCodes.DimensionMismatch,
                $"Expected a vector with dimension {dimension}, but received {vector.Length} values.");
        }

        if (Array.Exists(vector, static value => !float.IsFinite(value)))
        {
            throw new DomainException(ErrorCodes.InvalidVector, "Vectors may contain only finite values.");
        }
    }

    public static void ValidateSearch(SearchRequest request, int dimension, int maximumLimit)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 || request.Limit > maximumLimit)
        {
            throw new DomainException(ErrorCodes.InvalidLimit, $"Search limit must be between 1 and {maximumLimit}.");
        }

        if (request.VectorWeight is < 0 or > 1 || request.TextWeight is < 0 or > 1 ||
            request.VectorWeight + request.TextWeight <= 0)
        {
            throw new DomainException(ErrorCodes.InvalidWeights, "Search weights must be between 0 and 1 and not both zero.");
        }

        if (request.Mode is SearchMode.Vector or SearchMode.Hybrid)
        {
            if (request.Vector is null)
            {
                throw new DomainException(ErrorCodes.VectorRequired, $"A vector is required for {request.Mode} search.");
            }

            ValidateVector(request.Vector, dimension);
        }

        if (request.Mode is SearchMode.Text or SearchMode.Hybrid && string.IsNullOrWhiteSpace(request.Text))
        {
            throw new DomainException(ErrorCodes.TextRequired, $"Text is required for {request.Mode} search.");
        }
    }

    private static void ValidateMetadataValue(MetadataValue value)
    {
        bool valid = value.Kind switch
        {
            MetadataValueKind.Null => true,
            MetadataValueKind.Text => value.StringValue is not null,
            MetadataValueKind.Boolean => value.BooleanValue.HasValue,
            MetadataValueKind.Integral => value.IntegerValue.HasValue,
            MetadataValueKind.Number => value.NumberValue is { } number && double.IsFinite(number),
            MetadataValueKind.DateTime => value.DateTimeValue.HasValue,
            MetadataValueKind.Identifier => value.GuidValue.HasValue,
            MetadataValueKind.TextArray => value.StringArrayValue is not null,
            MetadataValueKind.BooleanArray => value.BooleanArrayValue is not null,
            MetadataValueKind.IntegralArray => value.IntegerArrayValue is not null,
            MetadataValueKind.NumberArray => value.NumberArrayValue is { } numbers && Array.TrueForAll(numbers, double.IsFinite),
            _ => false,
        };

        if (!valid)
        {
            throw new DomainException(ErrorCodes.InvalidMetadata, $"Metadata value of kind '{value.Kind}' is malformed.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionNamePattern();
}
