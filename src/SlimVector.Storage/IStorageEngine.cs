using SlimVector.Domain;

namespace SlimVector.Storage;

public interface IStorageEngine : IDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CollectionDefinition>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    ValueTask<CollectionDefinition?> GetCollectionAsync(string name, CancellationToken cancellationToken = default);

    ValueTask CreateCollectionAsync(CollectionDefinition definition, CancellationToken cancellationToken = default);

    ValueTask UpdateCollectionAsync(CollectionDefinition definition, CancellationToken cancellationToken = default);

    ValueTask DeleteCollectionAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadDocumentsAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);

    ValueTask<long> CountDocumentsAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);

    ValueTask AppendAsync(
        Guid collectionId,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default);

    ValueTask CompactAsync(Guid collectionId, CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReadDerivedDataAsync(
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default);

    ValueTask WriteDerivedDataAsync(
        Guid collectionId,
        string name,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}

public sealed record StorageOperation
{
    public required DocumentMutationKind Kind { get; init; }

    public required string Id { get; init; }

    public DocumentRecord? Document { get; init; }

    public long Version { get; init; }

    public static StorageOperation Upsert(DocumentRecord document) => new()
    {
        Kind = DocumentMutationKind.Upsert,
        Id = document.Id,
        Document = document,
        Version = document.Version,
    };

    public static StorageOperation Delete(string id, long version = 0) => new()
    {
        Kind = DocumentMutationKind.Delete,
        Id = id,
        Version = version,
    };
}
