using SlimVector.Domain;

namespace SlimVector.Storage;

public interface IDataGroupStorage : IDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetLocalDataGroupIds();

    ValueTask EnsureDataGroupAsync(string groupId, CancellationToken cancellationToken = default);

    ValueTask RemoveDataGroupAsync(string groupId, CancellationToken cancellationToken = default);

    ValueTask EnsureCollectionAsync(
        string groupId,
        CollectionDefinition definition,
        CancellationToken cancellationToken = default);

    ValueTask DeleteCollectionAsync(
        string groupId,
        string collectionName,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadDocumentsAsync(
        string groupId,
        Guid collectionId,
        CancellationToken cancellationToken = default);

    ValueTask AppendAsync(
        string groupId,
        Guid collectionId,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> ReadDerivedDataAsync(
        string groupId,
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default);

    ValueTask WriteDerivedDataAsync(
        string groupId,
        Guid collectionId,
        string name,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    long GetAllocatedBytes(string groupId);
}
