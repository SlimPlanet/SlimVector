using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Routing;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application;

public sealed class SlimVectorDatabase : ISlimVectorDatabase, ILocalDataQueryService
{
    private const string HnswDerivedDataName = "vector-hnsw-v1";
    private const string SearchIndexDerivedDataName = "search-index-v1";
    private const string SearchIndexManifestDataName = "search-index-manifest-v1";
    private readonly IStorageEngine _storage;
    private readonly IConsensusCoordinator _consensus;
    private readonly IWriteScheduler _writeScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly CollectionsOptions _collectionsOptions;
    private readonly VectorIndexOptions _vectorIndexOptions;
    private readonly TextIndexOptions _textIndexOptions;
    private readonly MetadataIndexOptions _metadataIndexOptions;
    private readonly ObservabilityOptions _observabilityOptions;
    private readonly AutoIndexOptions _autoIndexOptions;
    private readonly HnswOptions _hnswOptions;
    private readonly IvfOptions _ivfOptions;
    private readonly PqOptions _pqOptions;
    private readonly DiskAnnOptions _diskAnnOptions;
    private readonly OperationalMetrics _metrics;
    private readonly ILogger<SlimVectorDatabase> _logger;
    private readonly IDataGroupStorage? _dataGroupStorage;
    private readonly IClusterTopologyStore? _topologyStore;
    private readonly ILocalRaftGroupManager? _localGroups;
    private readonly IDataNodeQueryClient? _queryClient;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<CollectionRuntime>>> _runtimes = new();
    private readonly ConcurrentDictionary<Guid, int> _localMutationScopes = new();
    private volatile bool _initialized;

    public SlimVectorDatabase(
        IStorageEngine storage,
        IConsensusCoordinator consensus,
        IWriteScheduler writeScheduler,
        TimeProvider timeProvider,
        IOptions<CollectionsOptions> collectionsOptions,
        IOptions<VectorIndexOptions> vectorIndexOptions,
        IOptions<TextIndexOptions>? textIndexOptions = null,
        IOptions<MetadataIndexOptions>? metadataIndexOptions = null,
        IOptions<ObservabilityOptions>? observabilityOptions = null,
        IOptions<AutoIndexOptions>? autoIndexOptions = null,
        IOptions<DiskAnnOptions>? diskAnnOptions = null,
        OperationalMetrics? metrics = null,
        ILogger<SlimVectorDatabase>? logger = null,
        IOptions<HnswOptions>? hnswOptions = null,
        IOptions<IvfOptions>? ivfOptions = null,
        IOptions<PqOptions>? pqOptions = null,
        IDataGroupStorage? dataGroupStorage = null,
        IClusterTopologyStore? topologyStore = null,
        ILocalRaftGroupManager? localGroups = null,
        IDataNodeQueryClient? queryClient = null)
    {
        _storage = storage;
        _consensus = consensus;
        _writeScheduler = writeScheduler;
        _timeProvider = timeProvider;
        _collectionsOptions = collectionsOptions.Value;
        _vectorIndexOptions = vectorIndexOptions.Value;
        _textIndexOptions = textIndexOptions?.Value ?? new TextIndexOptions();
        _metadataIndexOptions = metadataIndexOptions?.Value ?? new MetadataIndexOptions();
        _observabilityOptions = observabilityOptions?.Value ?? new ObservabilityOptions();
        _autoIndexOptions = autoIndexOptions?.Value ?? new AutoIndexOptions
        {
            HnswMinimumVectors = _vectorIndexOptions.AutoHnswThreshold,
        };
        _hnswOptions = hnswOptions?.Value ?? new HnswOptions();
        _ivfOptions = ivfOptions?.Value ?? new IvfOptions();
        _pqOptions = pqOptions?.Value ?? new PqOptions();
        _diskAnnOptions = diskAnnOptions?.Value ?? new DiskAnnOptions();
        _metrics = metrics ?? new OperationalMetrics();
        _logger = logger ?? NullLogger<SlimVectorDatabase>.Instance;
        _dataGroupStorage = dataGroupStorage;
        _topologyStore = topologyStore;
        _localGroups = localGroups;
        _queryClient = queryClient;
        _consensus.StateChanged += InvalidateRuntime;
    }

    public int OpenCollectionCount => _runtimes.Count;

    public IReadOnlyList<IndexRuntimeMetrics> GetOpenIndexMetrics()
    {
        List<IndexRuntimeMetrics> metrics = [];
        foreach (Lazy<Task<CollectionRuntime>> lazy in _runtimes.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                metrics.Add(lazy.Value.Result.GetRuntimeMetrics());
            }
        }

        return metrics.OrderBy(static item => item.Collection, StringComparer.Ordinal).ToArray();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_dataGroupStorage is not null)
        {
            await _dataGroupStorage.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_topologyStore is not null)
        {
            await _topologyStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        await _consensus.StartAsync(cancellationToken).ConfigureAwait(false);
        await _writeScheduler.StartAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async ValueTask<IReadOnlyList<CollectionDefinition>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _consensus
            .ApplyReadBarrierAsync(collectionId: null, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        return await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CollectionDefinition> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _consensus
            .ApplyReadBarrierAsync(collectionId: null, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        return await ResolveCollectionAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound(name);
    }

    public async ValueTask<CollectionDefinition> CreateCollectionAsync(
        string name,
        int dimension,
        DistanceMetric metric,
        VectorIndexConfiguration? vectorIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        vectorIndex ??= CreateDefaultVectorIndexConfiguration();
        CollectionDefinition definition = CollectionDefinition.Create(
            name,
            dimension,
            metric,
            vectorIndex,
            _timeProvider,
            _metadataIndexOptions.IndexByDefault);
        definition = definition with { Placement = _consensus.CreateInitialPlacement(definition.Id) };
        await _consensus.UpsertCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
        return definition;
    }

    public async ValueTask<CollectionDefinition> GetOrCreateCollectionAsync(
        string name,
        int dimension,
        DistanceMetric metric,
        VectorIndexConfiguration? vectorIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CollectionDefinition? existing = await _storage.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Dimension != dimension || existing.Metric != metric)
            {
                throw new DomainException(
                    ErrorCodes.InvalidIndexConfiguration,
                    $"Existing collection '{name}' does not match the requested dimension and metric.");
            }

            if (existing.Placement is null)
            {
                existing = existing with
                {
                    Placement = _consensus.CreateInitialPlacement(existing.Id),
                    UpdatedAt = _timeProvider.GetUtcNow(),
                };
                await _consensus.UpsertCollectionAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        try
        {
            return await CreateCollectionAsync(name, dimension, metric, vectorIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException exception) when (exception.Code == ErrorCodes.CollectionAlreadyExists)
        {
            return await GetCollectionAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<CollectionDefinition> UpdateCollectionAsync(
        string currentName,
        string? newName,
        VectorIndexConfiguration? vectorIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CollectionDefinition current = await GetCollectionAsync(currentName, cancellationToken).ConfigureAwait(false);
        string name = newName ?? current.Name;
        DomainValidation.ValidateCollectionName(name);
        VectorIndexConfiguration configuration = vectorIndex ?? current.VectorIndex;
        DomainValidation.ValidateVectorIndex(configuration, current.Dimension);
        CollectionDefinition updated = current with
        {
            Name = name,
            VectorIndex = configuration,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        await _consensus.UpsertCollectionAsync(updated, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async ValueTask DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CollectionDefinition definition = await GetCollectionAsync(name, cancellationToken).ConfigureAwait(false);
        await _consensus.DeleteCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
        if (_runtimes.TryRemove(definition.Id, out Lazy<Task<CollectionRuntime>>? lazy) && lazy.IsValueCreated)
        {
            CollectionRuntime runtime = await lazy.Value.ConfigureAwait(false);
            runtime.Dispose();
        }
    }

    public async ValueTask<BatchMutationResult> MutateAsync(
        string collectionName,
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        if (_consensus.Mode == ExecutionMode.Cluster && _queryClient is not null && _localGroups is not null)
        {
            return await MutateDistributedAsync(
                    collectionName,
                    mutations,
                    atomic,
                    clientId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        CollectionDefinition definition = await ResolveRequiredCollectionAsync(
                collectionName,
                ReadConsistency.Leader,
                cancellationToken)
            .ConfigureAwait(false);
        _localMutationScopes.AddOrUpdate(definition.Id, 1, static (_, count) => checked(count + 1));
        try
        {
            return await ExecuteAsync(
                    definition.Name,
                    runtime => runtime.MutateAsync(mutations, atomic, clientId, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _localMutationScopes.AddOrUpdate(definition.Id, 0, static (_, count) => Math.Max(0, count - 1));
            if (_localMutationScopes.TryGetValue(definition.Id, out int count) && count == 0)
            {
                _localMutationScopes.TryRemove(new KeyValuePair<Guid, int>(definition.Id, count));
            }
        }
    }

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        string collectionName,
        IReadOnlyList<string>? ids = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyReadBarriersAsync(definition, ReadConsistency.Leader, cancellationToken).ConfigureAwait(false);
        string[] dataGroupIds = _consensus.GetReadRoutes(definition)
            .Select(static route => route.DataGroupId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        HashSet<string>? hosted = _localGroups?.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
        if (_queryClient is not null && hosted is not null && dataGroupIds.Any(groupId => !hosted.Contains(groupId)))
        {
            int fetchLimit = ids is { Count: > 0 } ? ids.Count : checked(offset + limit);
            Task<IReadOnlyList<DocumentRecord>>[] tasks = dataGroupIds.Select(groupId =>
            {
                string[]? routedIds = ids?.Where(id => string.Equals(
                        _consensus.GetShardRoute(definition, id).DataGroupId,
                        groupId,
                        StringComparison.Ordinal))
                    .ToArray();
                return GetDocumentsFromGroupAsync(
                    collectionName,
                    groupId,
                    routedIds,
                    fetchLimit,
                    hosted,
                    cancellationToken).AsTask();
            }).ToArray();
            IReadOnlyList<DocumentRecord>[] partitions = await Task.WhenAll(tasks).ConfigureAwait(false);
            Dictionary<string, DocumentRecord> byId = partitions.SelectMany(static partition => partition)
                .GroupBy(static document => document.Id, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderByDescending(static document => document.Version).First(),
                    StringComparer.Ordinal);
            IEnumerable<DocumentRecord> ordered = ids is { Count: > 0 }
                ? ids.Distinct(StringComparer.Ordinal).Where(byId.ContainsKey).Select(id => byId[id])
                : byId.Values.OrderBy(static document => document.Id, StringComparer.Ordinal);
            return ordered.Skip(offset).Take(limit).Select(static document => document.DeepCopy()).ToArray();
        }

        if (dataGroupIds.Length == 1)
        {
            IReadOnlyDictionary<string, DocumentRecord> stored = _dataGroupStorage is null
                ? await _storage.LoadDocumentsAsync(definition.Id, cancellationToken).ConfigureAwait(false)
                : await _dataGroupStorage
                    .LoadDocumentsAsync(dataGroupIds[0], definition.Id, cancellationToken)
                    .ConfigureAwait(false);
            IEnumerable<DocumentRecord> ordered = ids is { Count: > 0 }
                ? ids.Distinct(StringComparer.Ordinal).Where(stored.ContainsKey).Select(id => stored[id])
                : stored.Values.OrderBy(static document => document.Id, StringComparer.Ordinal);
            return ordered.Skip(offset).Take(limit).ToArray();
        }

        return await ExecuteAsync(collectionName, runtime => runtime.GetDocumentsAsync(ids, offset, limit), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<DocumentPage> GetDocumentPageAsync(
        string collectionName,
        IReadOnlyList<string>? ids = null,
        int offset = 0,
        int limit = 100,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(
            collectionName,
            ReadConsistency.Leader,
            cancellationToken).ConfigureAwait(false);
        int effectiveOffset = offset;
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            DocumentContinuationState state = DecodeContinuationToken(continuationToken);
            if (state.CollectionId != definition.Id || state.RoutingEpoch != (definition.Placement?.Epoch ?? 0))
            {
                throw new DomainException(
                    ErrorCodes.RoutingEpochMismatch,
                    "The continuation token belongs to an older collection placement epoch.");
            }

            effectiveOffset = state.Offset;
        }

        IReadOnlyList<DocumentRecord> fetched = await GetDocumentsAsync(
            collectionName,
            ids,
            effectiveOffset,
            checked(limit + 1),
            cancellationToken).ConfigureAwait(false);
        bool hasMore = fetched.Count > limit;
        DocumentRecord[] documents = fetched.Take(limit).ToArray();
        return new DocumentPage
        {
            Documents = documents,
            ContinuationToken = hasMore
                ? EncodeContinuationToken(new DocumentContinuationState
                {
                    CollectionId = definition.Id,
                    RoutingEpoch = definition.Placement?.Epoch ?? 0,
                    Offset = checked(effectiveOffset + documents.Length),
                })
                : null,
        };
    }

    public async ValueTask<long> CountDocumentsAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyReadBarriersAsync(definition, ReadConsistency.Leader, cancellationToken).ConfigureAwait(false);
        string[] dataGroupIds = _consensus.GetReadRoutes(definition)
            .Select(static route => route.DataGroupId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        HashSet<string>? hosted = _localGroups?.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
        if (_queryClient is not null && hosted is not null && dataGroupIds.Any(groupId => !hosted.Contains(groupId)))
        {
            Task<long>[] tasks = dataGroupIds.Select(groupId => CountDocumentsFromGroupAsync(
                collectionName,
                groupId,
                hosted,
                cancellationToken).AsTask()).ToArray();
            return (await Task.WhenAll(tasks).ConfigureAwait(false)).Sum();
        }

        if (dataGroupIds.Length == 1)
        {
            return _dataGroupStorage is null
                ? await _storage.CountDocumentsAsync(definition.Id, cancellationToken).ConfigureAwait(false)
                : await _dataGroupStorage
                    .CountDocumentsAsync(dataGroupIds[0], definition.Id, cancellationToken)
                    .ConfigureAwait(false);
        }

        return await ExecuteAsync(collectionName, static runtime => runtime.CountAsync(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SearchResponse> SearchAsync(
        string collectionName,
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        bool succeeded = false;
        try
        {
            CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, request.Consistency, cancellationToken)
                .ConfigureAwait(false);
            DomainValidation.ValidateSearch(request, definition.Dimension, _vectorIndexOptions.MaximumSearchLimit);
            ValidateFilterDepth(request.Filter, _metadataIndexOptions.MaximumFilterDepth);
            await _consensus.ApplyReadBarriersAsync(definition, request.Consistency, cancellationToken).ConfigureAwait(false);
            string[] dataGroupIds = _consensus.GetReadRoutes(definition)
                .Select(static route => route.DataGroupId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            HashSet<string>? hosted = _localGroups?.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
            bool requiresFanOut = _queryClient is not null && hosted is not null && dataGroupIds.Any(groupId => !hosted.Contains(groupId));
            SearchResponse response = requiresFanOut
                ? await SearchDistributedAsync(collectionName, dataGroupIds, request, hosted!, cancellationToken).ConfigureAwait(false)
                : await ExecuteAsync(collectionName, runtime => runtime.SearchAsync(request), cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return response;
        }
        finally
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            bool slow = elapsed >= _observabilityOptions.SlowQueryThreshold;
            _metrics.RecordSearch(request.Mode, elapsed, succeeded, slow);
            if (slow)
            {
                DatabaseLog.SlowQuery(_logger, collectionName, request.Mode, request.Limit, elapsed.TotalMilliseconds);
            }
        }
    }

    public ValueTask<IndexMigrationStatus> GetIndexStatusAsync(
        string collectionName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(collectionName, static runtime => runtime.GetMigrationStatusAsync(), cancellationToken);

    public ValueTask<bool> RollbackIndexAsync(
        string collectionName,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(collectionName, runtime => runtime.RollbackIndexAsync(cancellationToken), cancellationToken);

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        IReadOnlyList<string>? ids,
        int limit,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalGroup(dataGroupId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, consistency, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyDataGroupReadBarrierAsync(dataGroupId, consistency, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(
            definition.Name,
            runtime => runtime.GetDocumentsDataGroupAsync(dataGroupId, ids, limit),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long> CountDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalGroup(dataGroupId);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, consistency, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyDataGroupReadBarrierAsync(dataGroupId, consistency, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(
            definition.Name,
            runtime => runtime.CountDataGroupAsync(dataGroupId),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetRawDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalGroup(dataGroupId);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, consistency, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyDataGroupReadBarrierAsync(dataGroupId, consistency, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, DocumentRecord> documents = _dataGroupStorage is null
            ? await _storage.LoadDocumentsAsync(definition.Id, cancellationToken).ConfigureAwait(false)
            : await _dataGroupStorage.LoadDocumentsAsync(dataGroupId, definition.Id, cancellationToken).ConfigureAwait(false);
        return documents.Values
            .OrderBy(static document => document.Id, StringComparer.Ordinal)
            .Select(static document => document.DeepCopy())
            .ToArray();
    }

    public async ValueTask<Bm25CorpusStatistics> GetTextCorpusStatisticsLocalAsync(
        string collectionName,
        string dataGroupId,
        string query,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalGroup(dataGroupId);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, consistency, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyDataGroupReadBarrierAsync(dataGroupId, consistency, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(
            definition.Name,
            runtime => runtime.GetTextCorpusStatisticsAsync(dataGroupId, query),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SearchResponse> SearchLocalAsync(
        string collectionName,
        string dataGroupId,
        SearchRequest request,
        Bm25CorpusStatistics? corpusStatistics,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalGroup(dataGroupId);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, request.Consistency, cancellationToken)
            .ConfigureAwait(false);
        DomainValidation.ValidateSearch(request, definition.Dimension, _vectorIndexOptions.MaximumSearchLimit);
        ValidateFilterDepth(request.Filter, _metadataIndexOptions.MaximumFilterDepth);
        await _consensus.ApplyDataGroupReadBarrierAsync(dataGroupId, request.Consistency, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(
            definition.Name,
            runtime => runtime.SearchDataGroupAsync(dataGroupId, request, corpusStatistics),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<int> EvictInactiveCollectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int evicted = 0;
        foreach ((Guid id, Lazy<Task<CollectionRuntime>> lazy) in _runtimes)
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            CollectionRuntime runtime = lazy.Value.Result;
            if (runtime.TryMarkEvicted(_collectionsOptions.IdleTimeout) && _runtimes.TryRemove(new KeyValuePair<Guid, Lazy<Task<CollectionRuntime>>>(id, lazy)))
            {
                runtime.Dispose();
                evicted++;
            }
        }

        return ValueTask.FromResult(evicted);
    }

    private async ValueTask<T> ExecuteAsync<T>(
        string collectionName,
        Func<CollectionRuntime, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CollectionRuntime runtime = await GetRuntimeAsync(collectionName, cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(runtime).ConfigureAwait(false);
            }
            catch (RuntimeEvictedException) when (attempt == 0)
            {
                _runtimes.TryRemove(runtime.Definition.Id, out _);
            }
        }

        throw new InvalidOperationException("Collection runtime could not be acquired.");
    }

    private async ValueTask<CollectionRuntime> GetRuntimeAsync(string collectionName, CancellationToken cancellationToken)
    {
        CollectionDefinition definition = await ResolveCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false)
            ?? throw NotFound(collectionName);
        if (_runtimes.Count >= _collectionsOptions.MaximumOpenCollections && !_runtimes.ContainsKey(definition.Id))
        {
            await EvictOldestRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }

        Lazy<Task<CollectionRuntime>> lazy = _runtimes.GetOrAdd(
            definition.Id,
            _ => new Lazy<Task<CollectionRuntime>>(
                () => LoadRuntimeAsync(definition, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _runtimes.TryRemove(new KeyValuePair<Guid, Lazy<Task<CollectionRuntime>>>(definition.Id, lazy));
            throw;
        }
    }

    private async Task<CollectionRuntime> LoadRuntimeAsync(CollectionDefinition definition, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int documentCount = 0;
        bool succeeded = false;
        bool restoredPersistedIndex = false;
        byte[]? persistedVectorIndex = null;
        byte[]? persistedPreviousVectorIndex = null;
        IndexGenerationManifest? persistedManifest = null;
        VectorIndexKind effectiveKind = definition.VectorIndex.Kind;
        try
        {
            IReadOnlyDictionary<string, DocumentRecord> documents = await LoadLocalDocumentsAsync(
                    definition,
                    cancellationToken)
                .ConfigureAwait(false);
            documentCount = documents.Count;
            if (definition.VectorIndex.Kind == VectorIndexKind.Auto)
            {
                byte[]? manifestData = await ReadDerivedDataAsync(
                        definition,
                        SearchIndexManifestDataName,
                        cancellationToken)
                    .ConfigureAwait(false);
                persistedManifest = manifestData is null ? null : IndexGenerationManifestCodec.Deserialize(manifestData);
            }

            effectiveKind = persistedManifest?.ActiveKind ?? (definition.VectorIndex.Kind == VectorIndexKind.Auto
                ? documentCount >= _vectorIndexOptions.AutoHnswThreshold ? VectorIndexKind.Hnsw : VectorIndexKind.Flat
                : definition.VectorIndex.Kind);
            persistedVectorIndex = persistedManifest is null
                ? await ReadDerivedDataAsync(definition, SearchIndexDerivedDataName, cancellationToken).ConfigureAwait(false)
                : await ReadDerivedDataAsync(
                    definition,
                    $"search-index-generation-{persistedManifest.ActiveGeneration}",
                    cancellationToken).ConfigureAwait(false);
            persistedVectorIndex ??= await ReadDerivedDataAsync(
                    definition,
                    HnswDerivedDataName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (persistedManifest?.PreviousGeneration is { } previousGeneration && persistedManifest.PreviousKind.HasValue)
            {
                persistedPreviousVectorIndex = await ReadDerivedDataAsync(
                    definition,
                    $"search-index-generation-{previousGeneration}",
                    cancellationToken).ConfigureAwait(false);
            }

            CollectionRuntime runtime = new(
                definition,
                documents,
                _storage,
                _writeScheduler,
                _timeProvider,
                _vectorIndexOptions.HybridCandidateMultiplier,
                _vectorIndexOptions.AutoHnswThreshold,
                persistedVectorIndex,
                _textIndexOptions.Bm25K1,
                _textIndexOptions.Bm25B,
                _textIndexOptions.MaximumTermsPerDocument,
                _autoIndexOptions,
                _diskAnnOptions,
                persistedManifest,
                persistedPreviousVectorIndex,
                _metrics,
                _dataGroupStorage);
            restoredPersistedIndex = runtime.RestoredPersistedIndex;
            if (_dataGroupStorage is not null && !restoredPersistedIndex)
            {
                try
                {
                    await runtime.EnsureIndexesPersistedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    runtime.Dispose();
                    throw;
                }
            }

            succeeded = true;
            return runtime;
        }
        finally
        {
            _metrics.RecordIndexLoad(
                effectiveKind,
                documentCount,
                Stopwatch.GetElapsedTime(started),
                succeeded,
                restoredPersistedIndex);
        }
    }

    private async ValueTask<byte[]?> ReadDerivedDataAsync(
        CollectionDefinition definition,
        string name,
        CancellationToken cancellationToken)
    {
        if (_dataGroupStorage is not null)
        {
            string[] routes = _consensus.GetReadRoutes(definition)
                .Select(static route => route.DataGroupId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (routes.Length == 1 &&
                _dataGroupStorage.GetLocalDataGroupIds().Contains(routes[0], StringComparer.Ordinal))
            {
                try
                {
                    return await _dataGroupStorage
                        .ReadDerivedDataAsync(routes[0], definition.Id, name, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (DomainException exception) when (exception.Code == ErrorCodes.CollectionNotFound)
                {
                    // Legacy single-node stores may only have the catalog-level derived index.
                }
            }
        }

        return await _storage.ReadDerivedDataAsync(definition.Id, name, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EvictOldestRuntimeAsync(CancellationToken cancellationToken)
    {
        int evicted = await EvictInactiveCollectionsAsync(cancellationToken).ConfigureAwait(false);
        if (evicted == 0 && _runtimes.Count >= _collectionsOptions.MaximumOpenCollections)
        {
            throw new DomainException(ErrorCodes.QueueSaturated, "The maximum number of open collections has been reached.");
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadLocalDocumentsAsync(
        CollectionDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_dataGroupStorage is null)
        {
            return await _storage.LoadDocumentsAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        }

        string[] localGroups = _dataGroupStorage.GetLocalDataGroupIds()
            .Where(groupId => definition.Placement?.ReadRoutes().Any(route =>
                string.Equals(route.DataGroupId, groupId, StringComparison.Ordinal)) ??
                string.Equals(_consensus.GetDataGroupId(definition.Id), groupId, StringComparison.Ordinal))
            .ToArray();
        Dictionary<string, DocumentRecord> documents = new(StringComparer.Ordinal);
        foreach (string groupId in localGroups)
        {
            IReadOnlyDictionary<string, DocumentRecord> groupDocuments = await _dataGroupStorage
                .LoadDocumentsAsync(groupId, definition.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach ((string id, DocumentRecord document) in groupDocuments)
            {
                ShardRoute route = _consensus.GetShardRoute(definition, id);
                if (!string.Equals(route.DataGroupId, groupId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!documents.TryGetValue(id, out DocumentRecord? existing) || existing.Version < document.Version)
                {
                    documents[id] = document.DeepCopy();
                }
            }
        }

        return documents;
    }

    private async ValueTask<SearchResponse> SearchDistributedAsync(
        string collectionName,
        string[] dataGroupIds,
        SearchRequest request,
        IReadOnlySet<string> hostedGroups,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int candidateLimit = Math.Min(
            _vectorIndexOptions.MaximumSearchLimit,
            Math.Max(request.Limit, checked(request.Limit * _vectorIndexOptions.HybridCandidateMultiplier)));
        Bm25CorpusStatistics? corpusStatistics = null;
        if (request.Mode is SearchMode.Text or SearchMode.Hybrid)
        {
            Task<Bm25CorpusStatistics>[] statisticsTasks = dataGroupIds.Select(groupId =>
                GetGroupTextStatisticsAsync(
                    collectionName,
                    groupId,
                    request.Text!,
                    request.Consistency,
                    hostedGroups,
                    cancellationToken).AsTask()).ToArray();
            corpusStatistics = AggregateCorpusStatistics(await Task.WhenAll(statisticsTasks).ConfigureAwait(false));
        }

        _metrics.RecordSearchFanOut(dataGroupIds.Length);
        IReadOnlyList<SearchHit> hits;
        if (request.Mode == SearchMode.Hybrid)
        {
            SearchRequest vectorRequest = request with
            {
                Mode = SearchMode.Vector,
                Limit = candidateLimit,
                Include = request.Include | IncludeFields.Scores,
            };
            SearchRequest textRequest = request with
            {
                Mode = SearchMode.Text,
                Limit = candidateLimit,
                Include = request.Include | IncludeFields.Scores,
            };
            Task<SearchResponse>[] vectorTasks = dataGroupIds.Select(groupId => SearchGroupAsync(
                collectionName,
                groupId,
                vectorRequest,
                corpusStatistics: null,
                hostedGroups,
                cancellationToken).AsTask()).ToArray();
            Task<SearchResponse>[] textTasks = dataGroupIds.Select(groupId => SearchGroupAsync(
                collectionName,
                groupId,
                textRequest,
                corpusStatistics,
                hostedGroups,
                cancellationToken).AsTask()).ToArray();
            await Task.WhenAll([.. vectorTasks, .. textTasks]).ConfigureAwait(false);
            hits = MergeHybrid(
                vectorTasks.SelectMany(static task => task.Result.Hits),
                textTasks.SelectMany(static task => task.Result.Hits),
                request);
        }
        else
        {
            SearchRequest shardRequest = request with
            {
                Limit = candidateLimit,
                Include = request.Include | IncludeFields.Scores,
            };
            Task<SearchResponse>[] tasks = dataGroupIds.Select(groupId => SearchGroupAsync(
                collectionName,
                groupId,
                shardRequest,
                corpusStatistics,
                hostedGroups,
                cancellationToken).AsTask()).ToArray();
            SearchResponse[] responses = await Task.WhenAll(tasks).ConfigureAwait(false);
            hits = MergeSingleMode(responses.SelectMany(static response => response.Hits), request);
        }

        return new SearchResponse
        {
            Hits = hits,
            TookMicroseconds = (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds,
        };
    }

    private ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsFromGroupAsync(
        string collectionName,
        string dataGroupId,
        IReadOnlyList<string>? ids,
        int limit,
        HashSet<string> hostedGroups,
        CancellationToken cancellationToken) => hostedGroups.Contains(dataGroupId)
        ? GetDocumentsLocalAsync(
            collectionName,
            dataGroupId,
            ids,
            limit,
            ReadConsistency.Leader,
            cancellationToken)
        : _queryClient!.GetDocumentsAsync(
            collectionName,
            dataGroupId,
            ids,
            limit,
            ReadConsistency.Leader,
            cancellationToken);

    private ValueTask<long> CountDocumentsFromGroupAsync(
        string collectionName,
        string dataGroupId,
        HashSet<string> hostedGroups,
        CancellationToken cancellationToken) => hostedGroups.Contains(dataGroupId)
        ? CountDocumentsLocalAsync(collectionName, dataGroupId, ReadConsistency.Leader, cancellationToken)
        : _queryClient!.CountDocumentsAsync(collectionName, dataGroupId, ReadConsistency.Leader, cancellationToken);

    private ValueTask<Bm25CorpusStatistics> GetGroupTextStatisticsAsync(
        string collectionName,
        string dataGroupId,
        string query,
        ReadConsistency consistency,
        IReadOnlySet<string> hostedGroups,
        CancellationToken cancellationToken) => hostedGroups.Contains(dataGroupId)
        ? GetTextCorpusStatisticsLocalAsync(collectionName, dataGroupId, query, consistency, cancellationToken)
        : _queryClient!.GetTextCorpusStatisticsAsync(collectionName, dataGroupId, query, consistency, cancellationToken);

    private ValueTask<SearchResponse> SearchGroupAsync(
        string collectionName,
        string dataGroupId,
        SearchRequest request,
        Bm25CorpusStatistics? corpusStatistics,
        IReadOnlySet<string> hostedGroups,
        CancellationToken cancellationToken) => hostedGroups.Contains(dataGroupId)
        ? SearchLocalAsync(collectionName, dataGroupId, request, corpusStatistics, cancellationToken)
        : _queryClient!.SearchAsync(collectionName, dataGroupId, request, corpusStatistics, cancellationToken);

    private static Bm25CorpusStatistics AggregateCorpusStatistics(
        IReadOnlyList<Bm25CorpusStatistics> statistics) => new()
        {
            DocumentCount = statistics.Sum(static item => item.DocumentCount),
            TotalTerms = statistics.Sum(static item => item.TotalTerms),
            Terms = statistics.SelectMany(static item => item.Terms)
                .GroupBy(static term => term.Term, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new Bm25TermStatistics
                {
                    Term = group.Key,
                    DocumentFrequency = group.Sum(static term => term.DocumentFrequency),
                }).ToArray(),
        };

    private static SearchHit[] MergeSingleMode(IEnumerable<SearchHit> candidates, SearchRequest request)
    {
        IEnumerable<SearchHit> distinct = candidates
            .GroupBy(static hit => hit.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static hit => hit.Score ?? double.MinValue).First());
        IEnumerable<SearchHit> ordered = request.Mode switch
        {
            SearchMode.Metadata => distinct.OrderBy(static hit => hit.Id, StringComparer.Ordinal),
            _ => distinct.OrderByDescending(static hit => hit.Score ?? double.MinValue)
                .ThenBy(static hit => hit.Id, StringComparer.Ordinal),
        };
        return ordered.Take(request.Limit)
            .Select((hit, index) => hit with
            {
                Score = request.Include.HasFlag(IncludeFields.Scores) ? hit.Score : null,
                VectorRank = request.Include.HasFlag(IncludeFields.Scores) && request.Mode == SearchMode.Vector
                    ? index + 1
                    : null,
                TextRank = request.Include.HasFlag(IncludeFields.Scores) && request.Mode == SearchMode.Text
                    ? index + 1
                    : null,
            }).ToArray();
    }

    private static SearchHit[] MergeHybrid(
        IEnumerable<SearchHit> vectorCandidates,
        IEnumerable<SearchHit> textCandidates,
        SearchRequest request)
    {
        SearchHit[] vectors = vectorCandidates
            .GroupBy(static hit => hit.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static hit => hit.Score ?? double.MinValue).First())
            .OrderByDescending(static hit => hit.Score ?? double.MinValue)
            .ThenBy(static hit => hit.Id, StringComparer.Ordinal)
            .ToArray();
        SearchHit[] texts = textCandidates
            .GroupBy(static hit => hit.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static hit => hit.Score ?? double.MinValue).First())
            .OrderByDescending(static hit => hit.Score ?? double.MinValue)
            .ThenBy(static hit => hit.Id, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<HybridRankedResult> ranked = RankFusion.WeightedReciprocalRank(
            vectors.Select(static hit => new RankedResult(hit.Id, hit.Score ?? double.MinValue)).ToArray(),
            texts.Select(static hit => new RankedResult(hit.Id, hit.Score ?? double.MinValue)).ToArray(),
            request.VectorWeight,
            request.TextWeight,
            request.Limit);
        Dictionary<string, SearchHit> content = vectors.Concat(texts)
            .GroupBy(static hit => hit.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        return ranked.Select(result => content[result.Id] with
        {
            Score = request.Include.HasFlag(IncludeFields.Scores) ? result.Score : null,
            VectorRank = request.Include.HasFlag(IncludeFields.Scores) ? result.VectorRank : null,
            TextRank = request.Include.HasFlag(IncludeFields.Scores) ? result.TextRank : null,
        }).ToArray();
    }

    private void EnsureLocalGroup(string dataGroupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataGroupId);
        if (_localGroups is not null && !_localGroups.GetHostedDataGroupIds().Contains(dataGroupId, StringComparer.Ordinal))
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"This node does not host data group '{dataGroupId}'.");
        }
    }

    private static string EncodeContinuationToken(DocumentContinuationState state)
    {
        byte[] payload = MemoryPackSerializer.Serialize(state);
        byte[] checksum = SHA256.HashData(payload);
        byte[] token = new byte[payload.Length + 16];
        payload.CopyTo(token, 0);
        checksum.AsSpan(0, 16).CopyTo(token.AsSpan(payload.Length));
        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static DocumentContinuationState DecodeContinuationToken(string token)
    {
        try
        {
            if (token.Length > 1_024)
            {
                throw new FormatException();
            }

            string base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length <= 16)
            {
                throw new FormatException();
            }

            ReadOnlySpan<byte> payload = bytes.AsSpan(0, bytes.Length - 16);
            Span<byte> checksum = stackalloc byte[32];
            SHA256.HashData(payload, checksum);
            if (!CryptographicOperations.FixedTimeEquals(checksum[..16], bytes.AsSpan(bytes.Length - 16)))
            {
                throw new FormatException();
            }

            DocumentContinuationState state = MemoryPackSerializer.Deserialize<DocumentContinuationState>(payload)
                ?? throw new FormatException();
            if (state.FormatVersion != 1 || state.CollectionId == Guid.Empty || state.Offset < 0)
            {
                throw new FormatException();
            }

            return state;
        }
        catch (Exception exception) when (exception is FormatException or MemoryPackSerializationException)
        {
            throw new DomainException(ErrorCodes.InvalidLimit, "The continuation token is invalid.");
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SlimVector must be initialized before it is used.");
        }
    }

    private async ValueTask<CollectionDefinition> ResolveRequiredCollectionAsync(
        string name,
        ReadConsistency consistency,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _consensus.ApplyReadBarrierAsync(collectionId: null, consistency, cancellationToken).ConfigureAwait(false);
        return await ResolveCollectionAsync(name, cancellationToken).ConfigureAwait(false) ?? throw NotFound(name);
    }

    private ValueTask<CollectionDefinition?> ResolveCollectionAsync(string name, CancellationToken cancellationToken) =>
        _storage.GetCollectionAsync(name, cancellationToken);

    private async ValueTask<BatchMutationResult> MutateDistributedAsync(
        string collectionName,
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(
            collectionName,
            ReadConsistency.Leader,
            cancellationToken).ConfigureAwait(false);
        string[] ids = mutations.Select(static mutation => mutation.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<DocumentRecord> current = ids.Length == 0
            ? []
            : await GetDocumentsAsync(collectionName, ids, 0, ids.Length, cancellationToken).ConfigureAwait(false);
        Dictionary<string, DocumentRecord> working = current.ToDictionary(
            static document => document.Id,
            static document => document,
            StringComparer.Ordinal);
        List<StorageOperation> operations = [];
        List<DocumentMutationResult> results = [];
        foreach (DocumentMutation mutation in mutations)
        {
            try
            {
                (StorageOperation operation, DocumentMutationResult result) = ApplyDistributedMutation(
                    definition,
                    working,
                    mutation);
                operations.Add(operation);
                results.Add(result);
            }
            catch (DomainException exception) when (!atomic)
            {
                results.Add(new DocumentMutationResult
                {
                    Id = mutation.Id,
                    Succeeded = false,
                    ErrorCode = exception.Code,
                    ErrorMessage = exception.Message,
                });
            }
            catch (DomainException exception)
            {
                return new BatchMutationResult
                {
                    Atomic = true,
                    Succeeded = 0,
                    Failed = mutations.Count,
                    Results = mutations.Select(item => new DocumentMutationResult
                    {
                        Id = item.Id,
                        Succeeded = false,
                        ErrorCode = exception.Code,
                        ErrorMessage = exception.Message,
                    }).ToArray(),
                };
            }
        }

        if (operations.Count > 0)
        {
            await _writeScheduler.EnqueueAsync(
                definition,
                operations,
                clientId,
                atomic,
                cancellationToken).ConfigureAwait(false);
            InvalidateRuntime(definition.Id);
        }

        return new BatchMutationResult
        {
            Atomic = atomic,
            Succeeded = results.Count(static result => result.Succeeded),
            Failed = results.Count(static result => !result.Succeeded),
            Results = results,
        };
    }

    private (StorageOperation Operation, DocumentMutationResult Result) ApplyDistributedMutation(
        CollectionDefinition definition,
        Dictionary<string, DocumentRecord> working,
        DocumentMutation mutation)
    {
        DomainValidation.ValidateDocumentId(mutation.Id);
        working.TryGetValue(mutation.Id, out DocumentRecord? existing);
        if (mutation.Kind == DocumentMutationKind.Delete)
        {
            if (existing is null)
            {
                throw new DomainException(ErrorCodes.DocumentNotFound, $"Document '{mutation.Id}' was not found.");
            }

            working.Remove(mutation.Id);
            long version = checked(existing.Version + 1);
            return (StorageOperation.Delete(mutation.Id, version), DistributedSuccess(mutation.Id, version));
        }

        DocumentRecord next = mutation.Kind switch
        {
            DocumentMutationKind.Add => existing is not null
                ? throw new DomainException(ErrorCodes.DocumentAlreadyExists, $"Document '{mutation.Id}' already exists.")
                : RequireDistributedDocument(mutation),
            DocumentMutationKind.Upsert => RequireDistributedDocument(mutation),
            DocumentMutationKind.Update => existing is null
                ? throw new DomainException(ErrorCodes.DocumentNotFound, $"Document '{mutation.Id}' was not found.")
                : ApplyDistributedPatch(existing, mutation.Patch),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation.Kind, "Unknown mutation kind."),
        };
        next = next with
        {
            Id = mutation.Id,
            Version = checked((existing?.Version ?? 0) + 1),
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        DomainValidation.ValidateDocument(next, definition.Dimension);
        new Bm25Index(
            _textIndexOptions.Bm25K1,
            _textIndexOptions.Bm25B,
            _textIndexOptions.MaximumTermsPerDocument).Upsert(next.Id, next.Text);
        next = next.DeepCopy();
        working[mutation.Id] = next;
        return (StorageOperation.Upsert(next), DistributedSuccess(mutation.Id, next.Version));
    }

    private static DocumentRecord RequireDistributedDocument(DocumentMutation mutation) => mutation.Document ??
        throw new DomainException(ErrorCodes.InvalidDocumentId, $"Mutation for '{mutation.Id}' requires a document.");

    private static DocumentRecord ApplyDistributedPatch(DocumentRecord existing, DocumentPatch? patch)
    {
        if (patch is null || patch.Text is null && patch.Vector is null && patch.Metadata is null)
        {
            throw new DomainException(ErrorCodes.InvalidDocumentId, $"Update for '{existing.Id}' contains no changes.");
        }

        return existing with
        {
            Text = patch.Text ?? existing.Text,
            Vector = patch.Vector ?? existing.Vector,
            Metadata = patch.Metadata ?? existing.Metadata,
        };
    }

    private static DocumentMutationResult DistributedSuccess(string id, long version) => new()
    {
        Id = id,
        Succeeded = true,
        Version = version,
    };

    private void InvalidateRuntime(Guid? collectionId)
    {
        if (collectionId.HasValue)
        {
            if (_localMutationScopes.ContainsKey(collectionId.Value))
            {
                return;
            }

            if (_runtimes.TryRemove(collectionId.Value, out Lazy<Task<CollectionRuntime>>? removed) &&
                removed.IsValueCreated && removed.Value.IsCompletedSuccessfully)
            {
                removed.Value.Result.Dispose();
            }
        }
        else
        {
            foreach (Lazy<Task<CollectionRuntime>> removed in _runtimes.Values)
            {
                if (removed.IsValueCreated && removed.Value.IsCompletedSuccessfully)
                {
                    removed.Value.Result.Dispose();
                }
            }

            _runtimes.Clear();
        }
    }

    private static DomainException NotFound(string name) =>
        new(ErrorCodes.CollectionNotFound, $"Collection '{name}' was not found.");

    private VectorIndexConfiguration CreateDefaultVectorIndexConfiguration() => new()
    {
        Kind = VectorIndexKind.Auto,
        HnswM = _hnswOptions.M,
        HnswEfConstruction = _hnswOptions.EfConstruction,
        HnswEfSearch = _hnswOptions.EfSearch,
        RerankCandidateMultiplier = _pqOptions.RerankCandidateMultiplier,
        IvfListCount = _ivfOptions.ListCount,
        IvfProbeCount = _ivfOptions.ProbeCount,
        IvfTrainingIterations = _ivfOptions.TrainingIterations,
        PqSubvectorCount = _pqOptions.SubvectorCount,
        PqCentroidCount = _pqOptions.CentroidCount,
        PqTrainingIterations = _pqOptions.TrainingIterations,
        DiskAnnMaxDegree = _diskAnnOptions.MaxDegree,
        DiskAnnSearchListSize = _diskAnnOptions.SearchListSize,
        DiskAnnBeamWidth = _diskAnnOptions.BeamWidth,
        DiskAnnDeltaThreshold = _diskAnnOptions.DeltaThreshold,
        DiskAnnPageSize = _diskAnnOptions.PageSize,
        DiskAnnCachePages = _diskAnnOptions.CachePages,
        DiskAnnRetainedGenerations = _diskAnnOptions.RetainedGenerations,
    };

    private static void ValidateFilterDepth(MetadataFilter? filter, int maximumDepth)
    {
        if (filter is null)
        {
            return;
        }

        Stack<(MetadataFilter Filter, int Depth)> pending = new();
        pending.Push((filter, 1));
        while (pending.TryPop(out (MetadataFilter Filter, int Depth) current))
        {
            if (current.Depth > maximumDepth)
            {
                throw new DomainException(
                    ErrorCodes.InvalidFilter,
                    $"Metadata filter depth exceeds the configured limit of {maximumDepth}.");
            }

            if (current.Filter.Operands is not null)
            {
                foreach (MetadataFilter operand in current.Filter.Operands)
                {
                    pending.Push((operand, current.Depth + 1));
                }
            }
        }
    }
}

[MemoryPackable]
internal sealed partial class DocumentContinuationState
{
    public int FormatVersion { get; set; } = 1;

    public Guid CollectionId { get; set; }

    public long RoutingEpoch { get; set; }

    public int Offset { get; set; }
}

internal static partial class DatabaseLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Slow query collection={CollectionName} mode={Mode} limit={Limit} elapsed_ms={ElapsedMilliseconds}")]
    public static partial void SlowQuery(
        ILogger logger,
        string collectionName,
        SearchMode mode,
        int limit,
        double elapsedMilliseconds);
}
