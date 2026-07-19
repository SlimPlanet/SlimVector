using SlimVector.Domain;

namespace SlimVector.Application.Indexes;

public sealed record IndexMigrationStatus(
    VectorIndexKind ActiveKind,
    long ActiveGeneration,
    long? PreviousGeneration,
    string State,
    string? Reason,
    DateTimeOffset LastMigration);

public sealed record IndexRuntimeMetrics(
    string Collection,
    VectorIndexKind ActiveKind,
    string MigrationState,
    int DocumentCount,
    long EstimatedResidentBytes,
    long PersistedSnapshotBytes);

internal readonly record struct IndexValidationResult(
    bool Accepted,
    double Recall,
    double PerformanceGain,
    string Reason);
