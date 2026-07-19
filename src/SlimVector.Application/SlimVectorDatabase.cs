using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application;

public sealed class SlimVectorDatabase : ISlimVectorDatabase
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
    private readonly ConcurrentDictionary<Guid, Lazy<Task<CollectionRuntime>>> _runtimes = new();
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
        IOptions<PqOptions>? pqOptions = null)
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

    public ValueTask<BatchMutationResult> MutateAsync(
        string collectionName,
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            collectionName,
            runtime => runtime.MutateAsync(mutations, atomic, clientId, cancellationToken),
            cancellationToken);

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        string collectionName,
        IReadOnlyList<string>? ids = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyReadBarrierAsync(definition.Id, ReadConsistency.Leader, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(collectionName, runtime => runtime.GetDocumentsAsync(ids, offset, limit), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<long> CountDocumentsAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        CollectionDefinition definition = await ResolveRequiredCollectionAsync(collectionName, ReadConsistency.Leader, cancellationToken)
            .ConfigureAwait(false);
        await _consensus.ApplyReadBarrierAsync(definition.Id, ReadConsistency.Leader, cancellationToken).ConfigureAwait(false);
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
            await _consensus.ApplyReadBarrierAsync(definition.Id, request.Consistency, cancellationToken).ConfigureAwait(false);
            SearchResponse response = await ExecuteAsync(collectionName, runtime => runtime.SearchAsync(request), cancellationToken)
                .ConfigureAwait(false);
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
            IReadOnlyDictionary<string, DocumentRecord> documents = await _storage
                .LoadDocumentsAsync(definition.Id, cancellationToken)
                .ConfigureAwait(false);
            documentCount = documents.Count;
            if (definition.VectorIndex.Kind == VectorIndexKind.Auto)
            {
                byte[]? manifestData = await _storage
                    .ReadDerivedDataAsync(definition.Id, SearchIndexManifestDataName, cancellationToken)
                    .ConfigureAwait(false);
                persistedManifest = manifestData is null ? null : IndexGenerationManifestCodec.Deserialize(manifestData);
            }

            effectiveKind = persistedManifest?.ActiveKind ?? (definition.VectorIndex.Kind == VectorIndexKind.Auto
                ? documentCount >= _vectorIndexOptions.AutoHnswThreshold ? VectorIndexKind.Hnsw : VectorIndexKind.Flat
                : definition.VectorIndex.Kind);
            persistedVectorIndex = persistedManifest is null
                ? await _storage.ReadDerivedDataAsync(definition.Id, SearchIndexDerivedDataName, cancellationToken).ConfigureAwait(false)
                : await _storage.ReadDerivedDataAsync(
                    definition.Id,
                    $"search-index-generation-{persistedManifest.ActiveGeneration}",
                    cancellationToken).ConfigureAwait(false);
            persistedVectorIndex ??= await _storage
                .ReadDerivedDataAsync(definition.Id, HnswDerivedDataName, cancellationToken)
                .ConfigureAwait(false);
            if (persistedManifest?.PreviousGeneration is { } previousGeneration && persistedManifest.PreviousKind.HasValue)
            {
                persistedPreviousVectorIndex = await _storage.ReadDerivedDataAsync(
                    definition.Id,
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
                _metrics);
            restoredPersistedIndex = runtime.RestoredPersistedIndex;
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

    private async ValueTask EvictOldestRuntimeAsync(CancellationToken cancellationToken)
    {
        int evicted = await EvictInactiveCollectionsAsync(cancellationToken).ConfigureAwait(false);
        if (evicted == 0 && _runtimes.Count >= _collectionsOptions.MaximumOpenCollections)
        {
            throw new DomainException(ErrorCodes.QueueSaturated, "The maximum number of open collections has been reached.");
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

    private void InvalidateRuntime(Guid? collectionId)
    {
        if (collectionId.HasValue)
        {
            _runtimes.TryRemove(collectionId.Value, out _);
        }
        else
        {
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
