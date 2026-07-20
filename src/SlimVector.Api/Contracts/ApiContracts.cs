using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Api.Contracts;

public sealed record CreateCollectionRequest
{
    public required string Name { get; init; }

    public required int Dimension { get; init; }

    public DistanceMetric? Metric { get; init; }

    public VectorIndexConfiguration? VectorIndex { get; init; }
}

public sealed record UpdateCollectionRequest
{
    public string? Name { get; init; }

    public VectorIndexConfiguration? VectorIndex { get; init; }
}

public sealed record CollectionResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Dimension { get; init; }

    public required DistanceMetric Metric { get; init; }

    public required VectorIndexConfiguration VectorIndex { get; init; }

    public required bool MetadataIndexed { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record CollectionListResponse
{
    public required IReadOnlyList<CollectionResponse> Collections { get; init; }
}

public sealed record DocumentInput
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public required float[] Vector { get; init; }

    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record DocumentUpdateInput
{
    public required string Id { get; init; }

    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record DocumentBatchRequest
{
    public required DocumentInput[] Documents { get; init; }

    public bool? Atomic { get; init; }
}

public sealed record DocumentUpdateBatchRequest
{
    public required DocumentUpdateInput[] Documents { get; init; }

    public bool? Atomic { get; init; }
}

public sealed record DocumentDeleteRequest
{
    public required string[] Ids { get; init; }

    public bool? Atomic { get; init; }
}

public sealed record DocumentResponse
{
    public required string Id { get; init; }

    public required string Text { get; init; }

    public required float[] Vector { get; init; }

    public required Dictionary<string, JsonElement> Metadata { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record DocumentListResponse
{
    public required IReadOnlyList<DocumentResponse> Documents { get; init; }
}

public sealed record CountResponse
{
    public required long Count { get; init; }
}

public sealed record MetadataFilterInput
{
    public required MetadataOperator Operator { get; init; }

    public string? Field { get; init; }

    public JsonElement? Value { get; init; }

    public JsonElement[]? Values { get; init; }

    public MetadataFilterInput[]? Operands { get; init; }
}

public sealed record QueryRequest
{
    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public SearchMode? Mode { get; init; }

    public int? Limit { get; init; }

    public MetadataFilterInput? Filter { get; init; }

    public string[]? Include { get; init; }

    public ReadConsistency? Consistency { get; init; }

    public double? VectorWeight { get; init; }

    public double? TextWeight { get; init; }
}

public sealed record QueryHitResponse
{
    public required string Id { get; init; }

    public string? Text { get; init; }

    public float[]? Vector { get; init; }

    public Dictionary<string, JsonElement>? Metadata { get; init; }

    public double? Score { get; init; }

    public int? VectorRank { get; init; }

    public int? TextRank { get; init; }
}

public sealed record QueryResponse
{
    public required IReadOnlyList<QueryHitResponse> Hits { get; init; }

    public required long TookMicroseconds { get; init; }
}

public sealed record MutationItemResponse
{
    public required string Id { get; init; }

    public required bool Succeeded { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public long? Version { get; init; }
}

public sealed record BatchMutationResponse
{
    public required bool Atomic { get; init; }

    public required int Succeeded { get; init; }

    public required int Failed { get; init; }

    public required IReadOnlyList<MutationItemResponse> Results { get; init; }
}

public sealed record HealthResponse
{
    public required string Status { get; init; }
}

public sealed record BackupResponse
{
    public required string BackupId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? ParentBackupId { get; init; }

    public required int CollectionCount { get; init; }

    public required long DocumentCount { get; init; }
}

public sealed record BackupListResponse
{
    public required IReadOnlyList<BackupResponse> Backups { get; init; }
}

public sealed record IndexMigrationResponse
{
    public required VectorIndexKind ActiveKind { get; init; }

    public required long ActiveGeneration { get; init; }

    public long? PreviousGeneration { get; init; }

    public required string State { get; init; }

    public string? Reason { get; init; }

    public required DateTimeOffset LastMigration { get; init; }
}

public sealed record MembershipChangeRequest
{
    public required string GroupId { get; init; }

    public string? Endpoint { get; init; }
}

public sealed record RaftMemberResponse
{
    public required string Endpoint { get; init; }

    public required bool IsLeader { get; init; }

    public required bool IsRemote { get; init; }

    public required string TransportStatus { get; init; }

    public long? MatchIndex { get; init; }

    public long? ReplicationLag { get; init; }
}

public sealed record GroupMembershipResponse
{
    public required string GroupId { get; init; }

    public required IReadOnlyList<RaftMemberResponse> Members { get; init; }

    public string? ChangeState { get; init; }

    public string? ChangeEndpoint { get; init; }
}

public sealed record ClusterMembershipResponse
{
    public required IReadOnlyList<GroupMembershipResponse> Groups { get; init; }
}

public sealed record RebalanceApprovalRequest
{
    public required Guid PlanId { get; init; }
}

public sealed record RebalanceActionResponse
{
    public required Guid OperationId { get; init; }

    public required Guid CollectionId { get; init; }

    public required string CollectionName { get; init; }

    public required int ShardId { get; init; }

    public required string SourceDataGroupId { get; init; }

    public required string TargetDataGroupId { get; init; }

    public required string Reason { get; init; }
}

public sealed record RebalancePlanResponse
{
    public required Guid PlanId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required bool DryRun { get; init; }

    public required IReadOnlyList<RebalanceActionResponse> Actions { get; init; }
}

public sealed record ShardMoveResponse
{
    public required Guid OperationId { get; init; }

    public required Guid CollectionId { get; init; }

    public required string CollectionName { get; init; }

    public required int ShardId { get; init; }

    public required string SourceDataGroupId { get; init; }

    public required string TargetDataGroupId { get; init; }

    public required ShardPlacementState State { get; init; }

    public required long RoutingEpoch { get; init; }

    public required long SnapshotVersion { get; init; }

    public required long ReplayedThroughVersion { get; init; }
}

public sealed record PlacementControllerResponse
{
    public required bool Paused { get; init; }

    public required IReadOnlyList<ShardMoveResponse> Moves { get; init; }
}

public sealed record AdminOperationResponse
{
    public required string Status { get; init; }
}

public sealed record BackupOperationResponse
{
    public required string Status { get; init; }
}

public sealed record RestoreCollectionRequest
{
    public required string CollectionName { get; init; }

    public string? RestoredName { get; init; }

    public bool Overwrite { get; init; }
}
