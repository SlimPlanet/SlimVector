using System.Text.Json;
using SlimVector.Application;
using SlimVector.Application.Backups;
using SlimVector.Application.Writes;
using SlimVector.DocIngestor.Models;
using SlimVector.Domain;
using SlimVector.Raft;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.Studio.Contracts;

public sealed record CollectionSummary
{
    public required StudioCollectionDefinition Definition { get; init; }

    public required long DocumentCount { get; init; }
}

public sealed record StudioCollectionDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Dimension { get; init; }

    public required DistanceMetric Metric { get; init; }

    public required VectorIndexConfiguration VectorIndex { get; init; }

    public required bool MetadataIndexed { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public static StudioCollectionDefinition FromDomain(CollectionDefinition definition) => new()
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
}

public sealed record StudioBootstrapResponse
{
    public required string Product { get; init; }

    public required string Version { get; init; }

    public required IReadOnlyList<CollectionSummary> Collections { get; init; }

    public required EmbeddingModelStatus Model { get; init; }

    public required string[] SupportedExtensions { get; init; }

    public required long MaximumUploadBytes { get; init; }

    public required string StoragePath { get; init; }
}

public sealed record CreateCollectionInput
{
    public string Name { get; init; } = string.Empty;

    public int Dimension { get; init; } = 384;

    public DistanceMetric Metric { get; init; } = DistanceMetric.Cosine;

    public VectorIndexKind IndexKind { get; init; } = VectorIndexKind.Auto;

    public int HnswM { get; init; } = 16;

    public int HnswEfConstruction { get; init; } = 200;

    public int HnswEfSearch { get; init; } = 64;
}

public sealed record UpdateCollectionInput
{
    public string? Name { get; init; }

    public VectorIndexKind IndexKind { get; init; } = VectorIndexKind.Auto;

    public int HnswM { get; init; } = 16;

    public int HnswEfConstruction { get; init; } = 200;

    public int HnswEfSearch { get; init; } = 64;
}

public sealed record IngestCommand
{
    public required string Collection { get; init; }

    public required Stream Content { get; init; }

    public required string FileName { get; init; }

    public string? ContentType { get; init; }

    public long? Length { get; init; }

    public ChunkingOptions Chunking { get; init; } = new();

    public bool PreviewOnly { get; init; }

    public bool ReplaceExisting { get; init; } = true;

    public bool Atomic { get; init; } = true;

    public Dictionary<string, JsonElement> Metadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record IngestedChunkResponse
{
    public required string Id { get; init; }

    public required int Sequence { get; init; }

    public required int EstimatedTokens { get; init; }

    public required IReadOnlyList<string> Locations { get; init; }

    public string? Heading { get; init; }

    public required string Text { get; init; }

    public required float[] VectorPreview { get; init; }
}

public sealed record IngestResponse
{
    public required string FileName { get; init; }

    public required string DocumentId { get; init; }

    public required string ContentSha256 { get; init; }

    public required IngestedDocumentFormat Format { get; init; }

    public string? Title { get; init; }

    public required int SectionCount { get; init; }

    public required int CharacterCount { get; init; }

    public required int ChunkCount { get; init; }

    public required int StoredCount { get; init; }

    public required int RemovedPreviousCount { get; init; }

    public required bool PreviewOnly { get; init; }

    public required double ExtractionMilliseconds { get; init; }

    public required double ChunkingMilliseconds { get; init; }

    public required double EmbeddingMilliseconds { get; init; }

    public required IReadOnlyList<IngestedChunkResponse> Chunks { get; init; }
}

public sealed record MetadataFilterInput
{
    public MetadataOperator Operator { get; init; } = MetadataOperator.Equal;

    public string? Field { get; init; }

    public JsonElement Value { get; init; }

    public JsonElement[]? Values { get; init; }

    public MetadataFilterInput[]? Operands { get; init; }
}

public sealed record SearchInput
{
    public string Query { get; init; } = string.Empty;

    public float[]? Vector { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    public int Limit { get; init; } = 10;

    public MetadataFilterInput? Filter { get; init; }

    public ReadConsistency Consistency { get; init; } = ReadConsistency.Leader;

    public double VectorWeight { get; init; } = 0.5;

    public double TextWeight { get; init; } = 0.5;

    public bool IncludeText { get; init; } = true;

    public bool IncludeVector { get; init; }

    public bool IncludeMetadata { get; init; } = true;

    public bool IncludeScores { get; init; } = true;
}

public sealed record SearchHitResponse
{
    public required string Id { get; init; }

    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public Dictionary<string, JsonElement>? Metadata { get; init; }

    public double? Score { get; init; }

    public int? VectorRank { get; init; }

    public int? TextRank { get; init; }
}

public sealed record StudioSearchResponse
{
    public required IReadOnlyList<SearchHitResponse> Hits { get; init; }

    public required long TookMicroseconds { get; init; }

    public required bool QueryWasVectorized { get; init; }
}

public sealed record DocumentResponse
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public float[]? Vector { get; init; }

    public required int VectorDimension { get; init; }

    public required Dictionary<string, JsonElement> Metadata { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record DocumentPageResponse
{
    public required long Total { get; init; }

    public required int Offset { get; init; }

    public required int Limit { get; init; }

    public required IReadOnlyList<DocumentResponse> Documents { get; init; }
}

public sealed record ManualDocumentInput
{
    public string Id { get; init; } = string.Empty;

    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public bool AutoVectorize { get; init; } = true;

    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record ManualMutationInput
{
    public DocumentMutationKind Kind { get; init; } = DocumentMutationKind.Upsert;

    public bool Atomic { get; init; } = true;

    public ManualDocumentInput[] Documents { get; init; } = [];
}

public sealed record DeleteDocumentsInput
{
    public string[] Ids { get; init; } = [];

    public bool Atomic { get; init; } = true;
}

public sealed record RuntimeResponse
{
    public required bool Ready { get; init; }

    public required ExecutionMode Mode { get; init; }

    public required int OpenCollections { get; init; }

    public required long ManagedMemoryBytes { get; init; }

    public required IReadOnlyList<RaftGroupStatus> RaftGroups { get; init; }

    public required WriteSchedulerSnapshot Writes { get; init; }

    public required OperationalMetricsSnapshot Operations { get; init; }

    public required BackupMetricsSnapshot Backups { get; init; }
}

public sealed record RestoreCollectionInput
{
    public string CollectionName { get; init; } = string.Empty;

    public string? RestoredName { get; init; }

    public bool Overwrite { get; init; }
}

public sealed record ConfirmedRestoreInput
{
    public string Confirm { get; init; } = string.Empty;
}
