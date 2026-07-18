using SlimVector.Domain;

namespace SlimVector.Application;

public interface ISlimVectorDatabase
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<CollectionDefinition>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    ValueTask<CollectionDefinition> GetCollectionAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<CollectionDefinition> CreateCollectionAsync(
        string name,
        int dimension,
        DistanceMetric metric,
        VectorIndexConfiguration? vectorIndex = null,
        CancellationToken cancellationToken = default);

    ValueTask<CollectionDefinition> GetOrCreateCollectionAsync(
        string name,
        int dimension,
        DistanceMetric metric,
        VectorIndexConfiguration? vectorIndex = null,
        CancellationToken cancellationToken = default);

    ValueTask<CollectionDefinition> UpdateCollectionAsync(
        string currentName,
        string? newName,
        VectorIndexConfiguration? vectorIndex,
        CancellationToken cancellationToken = default);

    ValueTask DeleteCollectionAsync(string name, CancellationToken cancellationToken = default);

    ValueTask<BatchMutationResult> MutateAsync(
        string collectionName,
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        string collectionName,
        IReadOnlyList<string>? ids = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default);

    ValueTask<long> CountDocumentsAsync(string collectionName, CancellationToken cancellationToken = default);

    ValueTask<SearchResponse> SearchAsync(
        string collectionName,
        SearchRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<int> EvictInactiveCollectionsAsync(CancellationToken cancellationToken = default);

    int OpenCollectionCount { get; }
}
