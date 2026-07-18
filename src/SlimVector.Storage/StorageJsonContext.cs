using System.Text.Json.Serialization;
using SlimVector.Domain;

namespace SlimVector.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CatalogFile))]
[JsonSerializable(typeof(CollectionManifest))]
[JsonSerializable(typeof(SegmentPayload))]
[JsonSerializable(typeof(CollectionDefinition))]
[JsonSerializable(typeof(DocumentRecord))]
[JsonSerializable(typeof(StorageOperation))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
