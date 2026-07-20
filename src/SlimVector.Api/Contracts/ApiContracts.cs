using System.Text.Json;
using MessagePack;
using SlimVector.Domain;
using SlimVector.Protocol;

namespace SlimVector.Api.Contracts;

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
public sealed record CollectionResponse
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
public sealed record CollectionListResponse
{
    [Key("collections")]
    public required IReadOnlyList<CollectionResponse> Collections { get; init; }
}

[MessagePackObject]
public sealed record DocumentInput
{
    [Key("id")]
    public required string Id { get; init; }

    [Key("text")]
    public required string Text { get; init; }

    [Key("vector")]
    public required float[] Vector { get; init; }

    [Key("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

[MessagePackObject]
public sealed record DocumentUpdateInput
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
public sealed record DocumentBatchRequest
{
    [Key("documents")]
    public required DocumentInput[] Documents { get; init; }

    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject]
public sealed record DocumentUpdateBatchRequest
{
    [Key("documents")]
    public required DocumentUpdateInput[] Documents { get; init; }

    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject]
public sealed record DocumentDeleteRequest
{
    [Key("ids")]
    public required string[] Ids { get; init; }

    [Key("atomic")]
    public bool? Atomic { get; init; }
}

[MessagePackObject]
public sealed record DocumentResponse
{
    [Key("id")]
    public required string Id { get; init; }

    [Key("text")]
    public required string Text { get; init; }

    [Key("vector")]
    public required float[] Vector { get; init; }

    [Key("metadata")]
    public required Dictionary<string, JsonElement> Metadata { get; init; }

    [Key("version")]
    public required long Version { get; init; }

    [Key("updatedAt")]
    [MessagePackFormatter(typeof(DateTimeOffsetMessagePackFormatter))]
    public required DateTimeOffset UpdatedAt { get; init; }
}

[MessagePackObject]
public sealed record DocumentListResponse
{
    [Key("documents")]
    public required IReadOnlyList<DocumentResponse> Documents { get; init; }

    [Key("continuationToken")]
    public string? ContinuationToken { get; init; }
}

[MessagePackObject]
public sealed record CountResponse
{
    [Key("count")]
    public required long Count { get; init; }
}

[MessagePackObject]
public sealed record MetadataFilterInput
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
    public MetadataFilterInput[]? Operands { get; init; }
}

[MessagePackObject]
public sealed record QueryRequest
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
    public MetadataFilterInput? Filter { get; init; }

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
public sealed record QueryHitResponse
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
public sealed record QueryResponse
{
    [Key("hits")]
    public required IReadOnlyList<QueryHitResponse> Hits { get; init; }

    [Key("tookMicroseconds")]
    public required long TookMicroseconds { get; init; }
}

[MessagePackObject]
public sealed record MutationItemResponse
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
public sealed record BatchMutationResponse
{
    [Key("atomic")]
    public required bool Atomic { get; init; }

    [Key("succeeded")]
    public required int Succeeded { get; init; }

    [Key("failed")]
    public required int Failed { get; init; }

    [Key("results")]
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

public sealed record ClusterNodeJoinRequest
{
    public required string NodeId { get; init; }

    public required string ApiEndpoint { get; init; }

    public required string InternalEndpoint { get; init; }

    public required string RaftHost { get; init; }

    public required string Zone { get; init; }

    public required long CapacityBytes { get; init; }

    public required int RaftPortStart { get; init; }

    public required int RaftPortCount { get; init; }

    public string[]? Roles { get; init; }
}

public sealed record ClusterNodeResponse
{
    public required string NodeId { get; init; }

    public required string ApiEndpoint { get; init; }

    public required string InternalEndpoint { get; init; }

    public required string RaftHost { get; init; }

    public required string Zone { get; init; }

    public required long CapacityBytes { get; init; }

    public required long UsedBytes { get; init; }

    public required long AssignedBytes { get; init; }

    public required int RaftPortStart { get; init; }

    public required int RaftPortCount { get; init; }

    public required ClusterNodeState State { get; init; }

    public required DateTimeOffset LastSeenAt { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }
}

public sealed record DataGroupReplicaResponse
{
    public required string NodeId { get; init; }

    public required string RaftEndpoint { get; init; }

    public long? ObservedReplicationLag { get; init; }

    public required bool Healthy { get; init; }
}

public sealed record DataGroupTopologyResponse
{
    public required string GroupId { get; init; }

    public required long Generation { get; init; }

    public required int ReplicationFactor { get; init; }

    public required long EstimatedBytes { get; init; }

    public required DataGroupState State { get; init; }

    public required IReadOnlyList<DataGroupReplicaResponse> Replicas { get; init; }
}

public sealed record ClusterCapacityResponse
{
    public required long RawBytes { get; init; }

    public required long ReservedBytes { get; init; }

    public required long EstimatedUsableBytes { get; init; }

    public required int ReplicationFactor { get; init; }
}

public sealed record ClusterTopologyResponse
{
    public required int FormatVersion { get; init; }

    public required long Epoch { get; init; }

    public required IReadOnlyList<string> CatalogNodeIds { get; init; }

    public required IReadOnlyList<ClusterNodeResponse> Nodes { get; init; }

    public required IReadOnlyList<DataGroupTopologyResponse> DataGroups { get; init; }

    public required IReadOnlyList<ReplicaMoveStatusResponse> ReplicaMoves { get; init; }

    public required ClusterCapacityResponse Capacity { get; init; }

    public SharedNothingRebalancePlanResponse? SuggestedRebalancePlan { get; init; }
}

public sealed record ReplicaMoveStatusResponse
{
    public required Guid OperationId { get; init; }

    public required Guid PlanId { get; init; }

    public required string GroupId { get; init; }

    public required string SourceNodeId { get; init; }

    public required string TargetNodeId { get; init; }

    public required long EstimatedBytes { get; init; }

    public required ReplicaMoveState State { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ReplicaRelocationResponse
{
    public required string GroupId { get; init; }

    public required string SourceNodeId { get; init; }

    public required string TargetNodeId { get; init; }

    public required string TargetRaftEndpoint { get; init; }

    public required long EstimatedBytes { get; init; }

    public required string Reason { get; init; }
}

public sealed record NodeCapacityChangeResponse
{
    public required string NodeId { get; init; }

    public required long AssignedBytesBefore { get; init; }

    public required long AssignedBytesAfter { get; init; }
}

public sealed record SharedNothingRebalancePlanResponse
{
    public required Guid PlanId { get; init; }

    public required long TopologyEpoch { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? DrainNodeId { get; init; }

    public required IReadOnlyList<DataGroupTopologyResponse> GroupsToCreate { get; init; }

    public required IReadOnlyList<ReplicaRelocationResponse> ReplicaMoves { get; init; }

    public required IReadOnlyList<NodeCapacityChangeResponse> CapacityChanges { get; init; }
}

public sealed record RebalanceApprovalRequest
{
    public required Guid PlanId { get; init; }
}

public sealed record PrepareDataGroupReplicaRequest
{
    public required string GroupId { get; init; }

    public required string RaftEndpoint { get; init; }
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
