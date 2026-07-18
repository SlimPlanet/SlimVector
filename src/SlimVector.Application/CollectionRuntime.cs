using System.Diagnostics;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Storage;

namespace SlimVector.Application;

internal sealed class CollectionRuntime
{
    private const string SearchIndexDerivedDataName = "search-index-v1";
    private readonly object _queueLock = new();
    private readonly IStorageEngine _storage;
    private readonly IWriteScheduler _writeScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly int _candidateMultiplier;
    private readonly int _autoHnswThreshold;
    private readonly double _bm25K1;
    private readonly double _bm25B;
    private readonly int _maximumTermsPerDocument;
    private Task _tail = Task.CompletedTask;
    private bool _evicted;
    private Dictionary<string, DocumentRecord> _documents;
    private CollectionSearchIndex _index;
    private long _lastAccessTimestamp;

    public CollectionRuntime(
        CollectionDefinition definition,
        IReadOnlyDictionary<string, DocumentRecord> documents,
        IStorageEngine storage,
        IWriteScheduler writeScheduler,
        TimeProvider timeProvider,
        int candidateMultiplier,
        int autoHnswThreshold,
        byte[]? persistedVectorIndex,
        double bm25K1 = 1.2,
        double bm25B = 0.75,
        int maximumTermsPerDocument = 100_000)
    {
        Definition = definition;
        _documents = documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.DeepCopy(), StringComparer.Ordinal);
        _storage = storage;
        _writeScheduler = writeScheduler;
        _timeProvider = timeProvider;
        _candidateMultiplier = candidateMultiplier;
        _autoHnswThreshold = autoHnswThreshold;
        _bm25K1 = bm25K1;
        _bm25B = bm25B;
        _maximumTermsPerDocument = maximumTermsPerDocument;
        _index = BuildIndex(
            definition,
            _documents.Values,
            autoHnswThreshold,
            persistedVectorIndex,
            bm25K1,
            bm25B,
            maximumTermsPerDocument);
        _lastAccessTimestamp = timeProvider.GetTimestamp();
    }

    public CollectionDefinition Definition { get; private set; }

    public bool RestoredPersistedIndex => _index.WasRestored;

    public Task<BatchMutationResult> MutateAsync(
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId,
        CancellationToken cancellationToken) =>
        EnqueueAsync(() => MutateCoreAsync(mutations, atomic, clientId, cancellationToken));

    public Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        IReadOnlyList<string>? ids,
        int offset,
        int limit) =>
        EnqueueAsync<IReadOnlyList<DocumentRecord>>(() => Task.FromResult<IReadOnlyList<DocumentRecord>>(GetDocumentsCore(ids, offset, limit)));

    public Task<long> CountAsync() => EnqueueAsync(() => Task.FromResult((long)_documents.Count));

    public Task<SearchResponse> SearchAsync(SearchRequest request) => EnqueueAsync(() => Task.FromResult(SearchCore(request)));

    public Task UpdateDefinitionAsync(CollectionDefinition definition) => EnqueueAsync(async () =>
    {
        Definition = definition;
        _index = BuildIndex(
            definition,
            _documents.Values,
            _autoHnswThreshold,
            persistedVectorIndex: null,
            _bm25K1,
            _bm25B,
            _maximumTermsPerDocument);
        await PersistVectorIndexAsync(CancellationToken.None).ConfigureAwait(false);
    });

    public bool TryMarkEvicted(TimeSpan idleTimeout)
    {
        lock (_queueLock)
        {
            if (_evicted || !_tail.IsCompleted || _timeProvider.GetElapsedTime(_lastAccessTimestamp) < idleTimeout)
            {
                return false;
            }

            _evicted = true;
            return true;
        }
    }

    public void MarkEvicted()
    {
        lock (_queueLock)
        {
            _evicted = true;
        }
    }

    private Task<T> EnqueueAsync<T>(Func<Task<T>> operation)
    {
        lock (_queueLock)
        {
            if (_evicted)
            {
                throw new RuntimeEvictedException();
            }

            Task<T> next = RunAfterAsync(_tail, operation);
            _tail = next;
            return next;
        }
    }

    private Task EnqueueAsync(Func<Task> operation)
    {
        lock (_queueLock)
        {
            if (_evicted)
            {
                throw new RuntimeEvictedException();
            }

            Task next = RunAfterAsync(_tail, operation);
            _tail = next;
            return next;
        }
    }

    private async Task<BatchMutationResult> MutateCoreAsync(
        IReadOnlyList<DocumentMutation> mutations,
        bool atomic,
        string? clientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        Dictionary<string, DocumentRecord> working = _documents.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        List<StorageOperation> storageOperations = [];
        List<DocumentMutationResult> results = [];

        foreach (DocumentMutation mutation in mutations)
        {
            try
            {
                (StorageOperation operation, DocumentMutationResult result) = ApplyMutation(working, mutation);
                storageOperations.Add(operation);
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

        if (storageOperations.Count > 0)
        {
            await _writeScheduler
                .EnqueueAsync(Definition, storageOperations, clientId, cancellationToken)
                .ConfigureAwait(false);
            _documents = working;
            foreach (StorageOperation operation in storageOperations)
            {
                if (operation.Kind == DocumentMutationKind.Delete)
                {
                    _index.Remove(operation.Id);
                }
                else
                {
                    _index.Upsert(operation.Document!);
                }
            }

            RebuildIfAutoIndexKindChanged();
            await PersistVectorIndexAsync(cancellationToken).ConfigureAwait(false);
        }

        return new BatchMutationResult
        {
            Atomic = atomic,
            Succeeded = results.Count(static result => result.Succeeded),
            Failed = results.Count(static result => !result.Succeeded),
            Results = results,
        };
    }

    private (StorageOperation Operation, DocumentMutationResult Result) ApplyMutation(
        Dictionary<string, DocumentRecord> working,
        DocumentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        DomainValidation.ValidateDocumentId(mutation.Id);
        working.TryGetValue(mutation.Id, out DocumentRecord? existing);

        if (mutation.Kind == DocumentMutationKind.Delete)
        {
            if (existing is null)
            {
                throw new DomainException(ErrorCodes.DocumentNotFound, $"Document '{mutation.Id}' was not found.");
            }

            working.Remove(mutation.Id);
            long version = existing.Version + 1;
            return (StorageOperation.Delete(mutation.Id, version), Success(mutation.Id, version));
        }

        DocumentRecord next = mutation.Kind switch
        {
            DocumentMutationKind.Add => existing is not null
                ? throw new DomainException(ErrorCodes.DocumentAlreadyExists, $"Document '{mutation.Id}' already exists.")
                : RequireDocument(mutation),
            DocumentMutationKind.Upsert => RequireDocument(mutation),
            DocumentMutationKind.Update => existing is null
                ? throw new DomainException(ErrorCodes.DocumentNotFound, $"Document '{mutation.Id}' was not found.")
                : ApplyPatch(existing, mutation.Patch),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation.Kind, "Unknown mutation kind."),
        };

        next = next with
        {
            Id = mutation.Id,
            Version = (existing?.Version ?? 0) + 1,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        DomainValidation.ValidateDocument(next, Definition.Dimension);
        _index.ValidateDocument(next);
        next = next.DeepCopy();
        working[mutation.Id] = next;
        return (StorageOperation.Upsert(next), Success(mutation.Id, next.Version));
    }

    private static DocumentRecord RequireDocument(DocumentMutation mutation)
    {
        if (mutation.Document is null)
        {
            throw new DomainException(ErrorCodes.InvalidDocumentId, $"Mutation for '{mutation.Id}' requires a document.");
        }

        return mutation.Document;
    }

    private static DocumentRecord ApplyPatch(DocumentRecord existing, DocumentPatch? patch)
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

    private DocumentRecord[] GetDocumentsCore(IReadOnlyList<string>? ids, int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        IEnumerable<DocumentRecord> documents;
        if (ids is { Count: > 0 })
        {
            documents = ids.Distinct(StringComparer.Ordinal)
                .Select(id => _documents.GetValueOrDefault(id))
                .Where(static document => document is not null)!;
        }
        else
        {
            documents = _documents.Values.OrderBy(static document => document.Id, StringComparer.Ordinal);
        }

        return documents.Skip(offset).Take(limit).Select(static document => document.DeepCopy()).ToArray();
    }

    private SearchResponse SearchCore(SearchRequest request)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<HybridRankedResult> ranked = _index.Search(request, _candidateMultiplier);
        List<SearchHit> hits = new(ranked.Count);
        foreach (HybridRankedResult result in ranked)
        {
            DocumentRecord document = _documents[result.Id];
            hits.Add(new SearchHit
            {
                Id = document.Id,
                Text = request.Include.HasFlag(IncludeFields.Text) ? document.Text : null,
                Vector = request.Include.HasFlag(IncludeFields.Vector) ? (float[])document.Vector.Clone() : null,
                Metadata = request.Include.HasFlag(IncludeFields.Metadata)
                    ? document.DeepCopy().Metadata
                    : null,
                Score = request.Include.HasFlag(IncludeFields.Scores) ? result.Score : null,
                VectorRank = request.Include.HasFlag(IncludeFields.Scores) ? result.VectorRank : null,
                TextRank = request.Include.HasFlag(IncludeFields.Scores) ? result.TextRank : null,
            });
        }

        stopwatch.Stop();
        return new SearchResponse
        {
            Hits = hits,
            TookMicroseconds = (long)(stopwatch.Elapsed.TotalMilliseconds * 1_000),
        };
    }

    private static CollectionSearchIndex BuildIndex(
        CollectionDefinition definition,
        IEnumerable<DocumentRecord> documents,
        int autoHnswThreshold,
        byte[]? persistedVectorIndex,
        double bm25K1,
        double bm25B,
        int maximumTermsPerDocument)
    {
        DocumentRecord[] records = documents.ToArray();
        VectorIndexKind effectiveKind = ResolveVectorKind(definition, records.Length, autoHnswThreshold);
        return new CollectionSearchIndex(
            definition,
            effectiveKind,
            records,
            persistedVectorIndex,
            bm25K1,
            bm25B,
            maximumTermsPerDocument);
    }

    private void RebuildIfAutoIndexKindChanged()
    {
        VectorIndexKind expected = ResolveVectorKind(Definition, _documents.Count, _autoHnswThreshold);
        if (_index.VectorKind != expected)
        {
            _index = BuildIndex(
                Definition,
                _documents.Values,
                _autoHnswThreshold,
                persistedVectorIndex: null,
                _bm25K1,
                _bm25B,
                _maximumTermsPerDocument);
        }
    }

    private async Task PersistVectorIndexAsync(CancellationToken cancellationToken)
    {
        byte[] data = _index.Serialize(_documents.Values);
        await _storage.WriteDerivedDataAsync(Definition.Id, SearchIndexDerivedDataName, data, cancellationToken).ConfigureAwait(false);
    }

    private static VectorIndexKind ResolveVectorKind(
        CollectionDefinition definition,
        int documentCount,
        int autoHnswThreshold) => definition.VectorIndex.Kind switch
        {
            VectorIndexKind.Auto => documentCount >= autoHnswThreshold ? VectorIndexKind.Hnsw : VectorIndexKind.Flat,
            VectorIndexKind.Flat => VectorIndexKind.Flat,
            VectorIndexKind.Hnsw => VectorIndexKind.Hnsw,
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.VectorIndex.Kind, "Unknown vector-index kind."),
        };

    private static DocumentMutationResult Success(string id, long version) => new()
    {
        Id = id,
        Succeeded = true,
        Version = version,
    };

    private async Task<T> RunAfterAsync<T>(Task previous, Func<Task<T>> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed request must not poison the serial execution chain.
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            lock (_queueLock)
            {
                _lastAccessTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    private async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A failed request must not poison the serial execution chain.
        }

        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            lock (_queueLock)
            {
                _lastAccessTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }
}

internal sealed class RuntimeEvictedException : Exception;
