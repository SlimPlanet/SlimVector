using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SlimVector.Api.Contracts;

namespace SlimVector.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CreateCollectionRequest))]
[JsonSerializable(typeof(UpdateCollectionRequest))]
[JsonSerializable(typeof(CollectionResponse))]
[JsonSerializable(typeof(CollectionListResponse))]
[JsonSerializable(typeof(DocumentInput))]
[JsonSerializable(typeof(DocumentUpdateInput))]
[JsonSerializable(typeof(DocumentBatchRequest))]
[JsonSerializable(typeof(DocumentUpdateBatchRequest))]
[JsonSerializable(typeof(DocumentDeleteRequest))]
[JsonSerializable(typeof(DocumentResponse))]
[JsonSerializable(typeof(DocumentListResponse))]
[JsonSerializable(typeof(CountResponse))]
[JsonSerializable(typeof(MetadataFilterInput))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(QueryHitResponse))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(MutationItemResponse))]
[JsonSerializable(typeof(BatchMutationResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(BackupResponse))]
[JsonSerializable(typeof(BackupListResponse))]
[JsonSerializable(typeof(BackupOperationResponse))]
[JsonSerializable(typeof(IndexMigrationResponse))]
[JsonSerializable(typeof(MembershipChangeRequest))]
[JsonSerializable(typeof(RaftMemberResponse))]
[JsonSerializable(typeof(GroupMembershipResponse))]
[JsonSerializable(typeof(ClusterMembershipResponse))]
[JsonSerializable(typeof(ClusterNodeJoinRequest))]
[JsonSerializable(typeof(ClusterNodeResponse))]
[JsonSerializable(typeof(DataGroupReplicaResponse))]
[JsonSerializable(typeof(DataGroupTopologyResponse))]
[JsonSerializable(typeof(ClusterCapacityResponse))]
[JsonSerializable(typeof(ClusterTopologyResponse))]
[JsonSerializable(typeof(ReplicaMoveStatusResponse))]
[JsonSerializable(typeof(ReplicaRelocationResponse))]
[JsonSerializable(typeof(NodeCapacityChangeResponse))]
[JsonSerializable(typeof(SharedNothingRebalancePlanResponse))]
[JsonSerializable(typeof(RebalanceApprovalRequest))]
[JsonSerializable(typeof(PrepareDataGroupReplicaRequest))]
[JsonSerializable(typeof(RebalanceActionResponse))]
[JsonSerializable(typeof(RebalancePlanResponse))]
[JsonSerializable(typeof(ShardMoveResponse))]
[JsonSerializable(typeof(PlacementControllerResponse))]
[JsonSerializable(typeof(AdminOperationResponse))]
[JsonSerializable(typeof(RestoreCollectionRequest))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
