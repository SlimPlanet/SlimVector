using System.Text.Json.Serialization;

namespace SlimVector.Client;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CollectionInfo))]
[JsonSerializable(typeof(CreateCollectionRequest))]
[JsonSerializable(typeof(UpdateCollectionRequest))]
[JsonSerializable(typeof(CollectionList))]
[JsonSerializable(typeof(SlimVectorDocument))]
[JsonSerializable(typeof(SlimVectorDocumentUpdate))]
[JsonSerializable(typeof(DocumentBatch))]
[JsonSerializable(typeof(DocumentUpdateBatch))]
[JsonSerializable(typeof(DocumentDelete))]
[JsonSerializable(typeof(DocumentList))]
[JsonSerializable(typeof(DocumentCount))]
[JsonSerializable(typeof(SlimVectorQuery))]
[JsonSerializable(typeof(SlimVectorQueryResult))]
[JsonSerializable(typeof(BatchResult))]
[JsonSerializable(typeof(IndexStatusInfo))]
[JsonSerializable(typeof(ClusterMembershipInfo))]
[JsonSerializable(typeof(MembershipChange))]
[JsonSerializable(typeof(AdminOperationInfo))]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
