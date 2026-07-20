using System.Text.Json;
using MessagePack;
using SlimVector.Domain;
using SlimVector.Protocol;

namespace SlimVector.Client;

[MessagePackObject]
public sealed record CollectionInfo
{
    [Key("id")]
    public required Guid Id { get; init; }
    [Key("name")]
    public required string Name { get; init; }
    [Key("dimension")]
    public required int Dimension { get; init; }
    [Key("metric")]
    [MessagePackFormatter(typeof(DistanceMetricMessagePackFormatter))]
    public required DistanceMetric Metric { get; init; }
    [Key("vectorIndex")]
    [MessagePackFormatter(typeof(VectorIndexConfigurationMessagePackFormatter))]
    public required VectorIndexConfiguration VectorIndex { get; init; }
    [Key("metadataIndexed")]
    public required bool MetadataIndexed { get; init; }
    [Key("createdAt")]
    [MessagePackFormatter(typeof(DateTimeOffsetMessagePackFormatter))]
    public required DateTimeOffset CreatedAt { get; init; }
    [Key("updatedAt")]
    [MessagePackFormatter(typeof(DateTimeOffsetMessagePackFormatter))]
    public required DateTimeOffset UpdatedAt { get; init; }
}

[MessagePackObject]
public sealed record CreateCollectionRequest
{
    [Key("name")]
    public required string Name { get; init; }
    [Key("dimension")]
    public required int Dimension { get; init; }
    [Key("metric")]
    [MessagePackFormatter(typeof(NullableDistanceMetricMessagePackFormatter))]
    public DistanceMetric? Metric { get; init; }
    [Key("vectorIndex")]
    [MessagePackFormatter(typeof(VectorIndexConfigurationMessagePackFormatter))]
    public VectorIndexConfiguration? VectorIndex { get; init; }
}

[MessagePackObject]
public sealed record UpdateCollectionRequest
{
    [Key("name")]
    public string? Name { get; init; }
    [Key("vectorIndex")]
    [MessagePackFormatter(typeof(VectorIndexConfigurationMessagePackFormatter))]
    public VectorIndexConfiguration? VectorIndex { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorDocument
{
    [Key("id")]
    public required string Id { get; init; }
    [Key("text")]
    public required string Text { get; init; }
    [Key("vector")]
    public required float[] Vector { get; init; }
    [Key("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    [Key("version")]
    public long Version { get; init; }
    [Key("updatedAt")]
    [MessagePackFormatter(typeof(DateTimeOffsetMessagePackFormatter))]
    public DateTimeOffset UpdatedAt { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorDocumentPage
{
    [Key("documents")]
    public required SlimVectorDocument[] Documents { get; init; }

    [Key("continuationToken")]
    public string? ContinuationToken { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorDocumentUpdate
{
    [Key("id")]
    public required string Id { get; init; }
    [Key("text")]
    public string? Text { get; init; }
    [Key("vector")]
    public float[]? Vector { get; init; }
    [Key("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

[MessagePackObject]
public sealed record MetadataFilterRequest
{
    [Key("operator")]
    [MessagePackFormatter(typeof(MetadataOperatorMessagePackFormatter))]
    public required MetadataOperator Operator { get; init; }
    [Key("field")]
    public string? Field { get; init; }
    [Key("value")]
    public JsonElement? Value { get; init; }
    [Key("values")]
    public JsonElement[]? Values { get; init; }
    [Key("operands")]
    public MetadataFilterRequest[]? Operands { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorQuery
{
    [Key("text")]
    public string? Text { get; init; }
    [Key("vector")]
    public float[]? Vector { get; init; }
    [Key("mode")]
    [MessagePackFormatter(typeof(NullableSearchModeMessagePackFormatter))]
    public SearchMode? Mode { get; init; }
    [Key("limit")]
    public int? Limit { get; init; }
    [Key("filter")]
    public MetadataFilterRequest? Filter { get; init; }
    [Key("include")]
    public string[]? Include { get; init; }
    [Key("consistency")]
    [MessagePackFormatter(typeof(NullableReadConsistencyMessagePackFormatter))]
    public ReadConsistency? Consistency { get; init; }
    [Key("vectorWeight")]
    public double? VectorWeight { get; init; }
    [Key("textWeight")]
    public double? TextWeight { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorQueryHit
{
    [Key("id")]
    public required string Id { get; init; }
    [Key("text")]
    public string? Text { get; init; }
    [Key("vector")]
    public float[]? Vector { get; init; }
    [Key("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    [Key("score")]
    public double? Score { get; init; }
    [Key("vectorRank")]
    public int? VectorRank { get; init; }
    [Key("textRank")]
    public int? TextRank { get; init; }
}

[MessagePackObject]
public sealed record SlimVectorQueryResult
{
    [Key("hits")]
    public required IReadOnlyList<SlimVectorQueryHit> Hits { get; init; }
    [Key("tookMicroseconds")]
    public required long TookMicroseconds { get; init; }
}

[MessagePackObject]
public sealed record MutationResult
{
    [Key("id")]
    public required string Id { get; init; }
    [Key("succeeded")]
    public required bool Succeeded { get; init; }
    [Key("errorCode")]
    public string? ErrorCode { get; init; }
    [Key("errorMessage")]
    public string? ErrorMessage { get; init; }
    [Key("version")]
    public long? Version { get; init; }
}

[MessagePackObject]
public sealed record BatchResult
{
    [Key("atomic")]
    public required bool Atomic { get; init; }
    [Key("succeeded")]
    public required int Succeeded { get; init; }
    [Key("failed")]
    public required int Failed { get; init; }
    [Key("results")]
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

[MessagePackObject(AllowPrivate = true)]
internal sealed record CollectionList
{
    [Key("collections")]
    public required CollectionInfo[] Collections { get; init; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record DocumentBatch
{
    [Key("documents")]
    public required SlimVectorDocument[] Documents { get; init; }
    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record DocumentUpdateBatch
{
    [Key("documents")]
    public required SlimVectorDocumentUpdate[] Documents { get; init; }
    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record DocumentDelete
{
    [Key("ids")]
    public required string[] Ids { get; init; }
    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record DocumentCount
{
    [Key("count")]
    public required long Count { get; init; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record ApiProblem
{
    [Key("title")]
    public string? Title { get; init; }
    [Key("detail")]
    public string? Detail { get; init; }
    [Key("code")]
    public string? Code { get; init; }
    [Key("status")]
    public int? Status { get; init; }
}
