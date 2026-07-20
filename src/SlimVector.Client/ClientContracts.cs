using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Client;

public sealed record CollectionInfo
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

public sealed record SlimVectorDocument
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required float[] Vector { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    public long Version { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SlimVectorDocumentPage
{
    public required SlimVectorDocument[] Documents { get; init; }

    public string? ContinuationToken { get; init; }
}

public sealed record SlimVectorDocumentUpdate
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public float[]? Vector { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record MetadataFilterRequest
{
    public required MetadataOperator Operator { get; init; }
    public string? Field { get; init; }
    public JsonElement? Value { get; init; }
    public JsonElement[]? Values { get; init; }
    public MetadataFilterRequest[]? Operands { get; init; }
}

public sealed record SlimVectorQuery
{
    public string? Text { get; init; }
    public float[]? Vector { get; init; }
    public SearchMode? Mode { get; init; }
    public int? Limit { get; init; }
    public MetadataFilterRequest? Filter { get; init; }
    public string[]? Include { get; init; }
    public ReadConsistency? Consistency { get; init; }
    public double? VectorWeight { get; init; }
    public double? TextWeight { get; init; }
}

public sealed record SlimVectorQueryHit
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public float[]? Vector { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    public double? Score { get; init; }
    public int? VectorRank { get; init; }
    public int? TextRank { get; init; }
}

public sealed record SlimVectorQueryResult
{
    public required IReadOnlyList<SlimVectorQueryHit> Hits { get; init; }
    public required long TookMicroseconds { get; init; }
}

public sealed record MutationResult
{
    public required string Id { get; init; }
    public required bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long? Version { get; init; }
}

public sealed record BatchResult
{
    public required bool Atomic { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<MutationResult> Results { get; init; }
}

public sealed record IndexStatusInfo
{
    public required VectorIndexKind ActiveKind { get; init; }
    public required long ActiveGeneration { get; init; }
    public long? PreviousGeneration { get; init; }
    public required string State { get; init; }
    public string? Reason { get; init; }
    public required DateTimeOffset LastMigration { get; init; }
}

public sealed record ClusterMembershipInfo
{
    public required IReadOnlyList<GroupMembershipInfo> Groups { get; init; }
}

public sealed record GroupMembershipInfo
{
    public required string GroupId { get; init; }
    public string? ChangeState { get; init; }
    public string? ChangeEndpoint { get; init; }
    public required IReadOnlyList<RaftMemberInfo> Members { get; init; }
}

public sealed record RaftMemberInfo
{
    public required string Endpoint { get; init; }
    public bool IsLeader { get; init; }
    public bool IsRemote { get; init; }
    public required string TransportStatus { get; init; }
    public long? MatchIndex { get; init; }
    public long? ReplicationLag { get; init; }
}

internal sealed record MembershipChange
{
    public required string GroupId { get; init; }
    public string? Endpoint { get; init; }
}

internal sealed record AdminOperationInfo
{
    public required string Status { get; init; }
}

internal sealed record CollectionList
{
    public required CollectionInfo[] Collections { get; init; }
}

internal sealed record DocumentBatch
{
    public required SlimVectorDocument[] Documents { get; init; }
    public bool? Atomic { get; init; }
}

internal sealed record DocumentUpdateBatch
{
    public required SlimVectorDocumentUpdate[] Documents { get; init; }
    public bool? Atomic { get; init; }
}

internal sealed record DocumentDelete
{
    public required string[] Ids { get; init; }
    public bool? Atomic { get; init; }
}

internal sealed record DocumentCount
{
    public required long Count { get; init; }
}

internal sealed record ApiProblem
{
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public string? Code { get; init; }
    public int? Status { get; init; }
}
