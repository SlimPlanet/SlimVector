using System.Diagnostics;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Storage;

namespace SlimVector.Application;

internal sealed class CollectionRuntime : IDisposable
{
    private const string SearchIndexDerivedDataName = "search-index-v1";
    private const string SearchIndexManifestDataName = "search-index-manifest-v1";
    private readonly object _queueLock = new();
    private readonly IStorageEngine _storage;
    private readonly IWriteScheduler _writeScheduler;
    private readonly TimeProvider _timeProvider;
    private readonly int _candidateMultiplier;
    private readonly int _autoHnswThreshold;
    private readonly AutoIndexOptions _autoIndexOptions;
    private readonly DiskAnnOptions _diskAnnOptions;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly double _bm25K1;
    private readonly double _bm25B;
    private readonly OperationalMetrics _metrics;
    private readonly int _maximumTermsPerDocument;
    private Task _tail = Task.CompletedTask;
    private bool _evicted;
    private bool _resourcesDisposed;
    private Dictionary<string, DocumentRecord> _documents;
    private CollectionSearchIndex _index;
    private long _lastAccessTimestamp;
    private long _activeGeneration = 1;
    private long? _previousGeneration;
    private CollectionSearchIndex? _previousIndex;
    private Task? _migrationTask;
    private DateTimeOffset _lastAssessment;
    private DateTimeOffset _lastMigration;
    private DateTimeOffset _statisticsStarted;
    private long _searchCount;
    private long _mutationCount;
    private long _deletionCount;
    private long _queryElapsedTicks;
    private string _migrationState = "idle";
    private string? _migrationReason;
    private long _persistedSnapshotBytes;

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
        int maximumTermsPerDocument = 100_000,
        AutoIndexOptions? autoIndexOptions = null,
        DiskAnnOptions? diskAnnOptions = null,
        IndexGenerationManifest? persistedManifest = null,
        byte[]? persistedPreviousVectorIndex = null,
        OperationalMetrics? metrics = null)
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
        _autoIndexOptions = autoIndexOptions ?? new AutoIndexOptions { HnswMinimumVectors = autoHnswThreshold };
        _diskAnnOptions = diskAnnOptions ?? new DiskAnnOptions();
        _metrics = metrics ?? new OperationalMetrics();
        _activeGeneration = persistedManifest?.ActiveGeneration ?? 1;
        _persistedSnapshotBytes = persistedVectorIndex?.LongLength ?? 0;
        _previousGeneration = persistedManifest?.PreviousGeneration;
        VectorIndexKind? initialKind = persistedManifest?.ActiveKind;
        if (!initialKind.HasValue && definition.VectorIndex.Kind == VectorIndexKind.Auto)
        {
            initialKind = SelectAutoIndex().Selected;
        }

        _index = BuildIndex(
            definition,
            _documents.Values,
            autoHnswThreshold,
            persistedVectorIndex,
            bm25K1,
            bm25B,
            maximumTermsPerDocument,
            initialKind,
            GetDiskAnnArtifactDirectory("active"));
        if (_previousGeneration.HasValue && persistedManifest?.PreviousKind is { } previousKind)
        {
            _previousIndex = BuildIndex(
                definition,
                _documents.Values,
                autoHnswThreshold,
                persistedPreviousVectorIndex,
                bm25K1,
                bm25B,
                maximumTermsPerDocument,
                previousKind,
                GetDiskAnnArtifactDirectory("previous"));
        }
        _lastAccessTimestamp = timeProvider.GetTimestamp();
        _lastAssessment = timeProvider.GetUtcNow();
        _lastMigration = persistedManifest?.ActivatedAt ?? DateTimeOffset.MinValue;
        _statisticsStarted = timeProvider.GetUtcNow();
    }

    public CollectionDefinition Definition { get; private set; }

    public bool RestoredPersistedIndex => _index.WasRestored;

    public IndexMigrationStatus GetMigrationStatus() => new(
        _index.VectorKind,
        _activeGeneration,
        _previousGeneration,
        _migrationState,
        _migrationReason,
        _lastMigration);

    public IndexRuntimeMetrics GetRuntimeMetrics() => new(
        Definition.Name,
        _index.VectorKind,
        _migrationState,
        _documents.Count,
        EstimateResidentBytes(),
        Volatile.Read(ref _persistedSnapshotBytes));

    public Task<IndexMigrationStatus> GetMigrationStatusAsync() =>
        EnqueueAsync(() => Task.FromResult(GetMigrationStatus()));

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

    public Task<bool> RollbackIndexAsync(CancellationToken cancellationToken) => EnqueueAsync(async () =>
    {
        if (_previousIndex is null || !_previousGeneration.HasValue)
        {
            return false;
        }

        CollectionSearchIndex target = _previousIndex;
        long targetGeneration = _previousGeneration.Value;
        CollectionSearchIndex current = _index;
        long currentGeneration = _activeGeneration;
        byte[] data = target.Serialize(_documents.Values);
        Volatile.Write(ref _persistedSnapshotBytes, data.LongLength);
        await _storage.WriteDerivedDataAsync(
            Definition.Id,
            SearchIndexDerivedDataName,
            data,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset activatedAt = _timeProvider.GetUtcNow();
        IndexGenerationManifest manifest = new(
            targetGeneration,
            _activeGeneration,
            target.VectorKind,
            current.VectorKind,
            activatedAt);
        await _storage.WriteDerivedDataAsync(
            Definition.Id,
            SearchIndexManifestDataName,
            IndexGenerationManifestCodec.Serialize(manifest),
            cancellationToken).ConfigureAwait(false);
        _index = target;
        _activeGeneration = targetGeneration;
        _previousIndex = current;
        _previousGeneration = currentGeneration;
        _lastMigration = activatedAt;
        _migrationState = "rolled-back";
        _migrationReason = $"Rolled back to generation {targetGeneration}.";
        return true;
    });

    public Task UpdateDefinitionAsync(CollectionDefinition definition) => EnqueueAsync(async () =>
    {
        Definition = definition;
        CollectionSearchIndex previous = _index;
        _index = BuildIndex(
            definition,
            _documents.Values,
            _autoHnswThreshold,
            persistedVectorIndex: null,
            _bm25K1,
            _bm25B,
            _maximumTermsPerDocument,
            persistedKind: null,
            GetDiskAnnArtifactDirectory("active"));
        previous.Dispose();
        _previousIndex?.Dispose();
        _previousIndex = null;
        _previousGeneration = null;
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
            _lifetimeCancellation.Cancel();
            _index.Dispose();
            _previousIndex?.Dispose();
            return true;
        }
    }

    public void MarkEvicted()
    {
        lock (_queueLock)
        {
            _evicted = true;
            _lifetimeCancellation.Cancel();
            _index.Dispose();
            _previousIndex?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
            _evicted = true;
            _lifetimeCancellation.Cancel();
            _index.Dispose();
            _previousIndex?.Dispose();
            _lifetimeCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
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
            _mutationCount += storageOperations.Count;
            _deletionCount += storageOperations.Count(static operation => operation.Kind == DocumentMutationKind.Delete);
            foreach (StorageOperation operation in storageOperations)
            {
                if (operation.Kind == DocumentMutationKind.Delete)
                {
                    _index.Remove(operation.Id);
                    _previousIndex?.Remove(operation.Id);
                }
                else
                {
                    _index.Upsert(operation.Document!);
                    _previousIndex?.Upsert(operation.Document!);
                }
            }

            await PersistVectorIndexAsync(cancellationToken).ConfigureAwait(false);
            MaybeScheduleAutoMigration();
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
        _searchCount++;
        _queryElapsedTicks += stopwatch.ElapsedTicks;
        MaybeScheduleAutoMigration();
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
        => BuildIndex(
            definition,
            documents,
            autoHnswThreshold,
            persistedVectorIndex,
            bm25K1,
            bm25B,
            maximumTermsPerDocument,
            persistedKind: null,
            diskAnnArtifactDirectory: null);

    private static CollectionSearchIndex BuildIndex(
        CollectionDefinition definition,
        IEnumerable<DocumentRecord> documents,
        int autoHnswThreshold,
        byte[]? persistedVectorIndex,
        double bm25K1,
        double bm25B,
        int maximumTermsPerDocument,
        VectorIndexKind? persistedKind,
        string? diskAnnArtifactDirectory)
    {
        DocumentRecord[] records = documents.ToArray();
        VectorIndexKind effectiveKind = persistedKind ?? ResolveVectorKind(definition, records.Length, autoHnswThreshold);
        return new CollectionSearchIndex(
            definition,
            effectiveKind,
            records,
            persistedVectorIndex,
            bm25K1,
            bm25B,
            maximumTermsPerDocument,
            diskAnnArtifactDirectory);
    }

    private void MaybeScheduleAutoMigration()
    {
        if (Definition.VectorIndex.Kind != VectorIndexKind.Auto || !_autoIndexOptions.Enabled ||
            _migrationTask is { IsCompleted: false })
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now - _lastAssessment < _autoIndexOptions.AssessmentInterval ||
            now - _lastMigration < _autoIndexOptions.MinimumMigrationInterval)
        {
            return;
        }

        _lastAssessment = now;
        AutoIndexDecision decision = SelectAutoIndex();
        _migrationReason = decision.Reason;
        if (!decision.MigrationRecommended)
        {
            _migrationState = "stable";
            ResetStatistics(now);
            return;
        }

        DocumentRecord[] snapshot = _documents.Values
            .OrderBy(static document => document.Id, StringComparer.Ordinal)
            .Select(static document => document.DeepCopy())
            .ToArray();
        HashSet<string> baseIds = snapshot.Select(static document => document.Id).ToHashSet(StringComparer.Ordinal);
        _migrationState = "building";
        CancellationToken cancellationToken = _lifetimeCancellation.Token;
        _migrationTask = RunMigrationAsync(decision.Selected, snapshot, baseIds, cancellationToken);
    }

    private async Task RunMigrationAsync(
        VectorIndexKind targetKind,
        DocumentRecord[] snapshot,
        HashSet<string> baseIds,
        CancellationToken cancellationToken)
    {
        CollectionSearchIndex? candidate = null;
        long started = Stopwatch.GetTimestamp();
        bool recorded = false;
        try
        {
            candidate = await Task.Run(
                () => BuildIndex(
                    Definition,
                    snapshot,
                    _autoHnswThreshold,
                    persistedVectorIndex: null,
                    _bm25K1,
                    _bm25B,
                    _maximumTermsPerDocument,
                    targetKind,
                    GetDiskAnnArtifactDirectory("candidate-" + Guid.NewGuid().ToString("N"))),
                cancellationToken).ConfigureAwait(false);
            CollectionSearchIndex ownedCandidate = candidate;
            await EnqueueAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _migrationState = "replaying-delta";
                foreach (string removed in baseIds.Where(id => !_documents.ContainsKey(id)))
                {
                    ownedCandidate.Remove(removed);
                }

                foreach (DocumentRecord document in _documents.Values)
                {
                    ownedCandidate.Upsert(document);
                }

                _migrationState = "validating";
                IndexValidationResult validation = ValidateCandidate(ownedCandidate);
                _migrationReason = validation.Reason;
                if (!validation.Accepted)
                {
                    _migrationState = "rejected";
                    _metrics.RecordIndexMigration(Stopwatch.GetElapsedTime(started), succeeded: false, validation.Recall);
                    recorded = true;
                    return;
                }

                long nextGeneration = checked(_activeGeneration + 1);
                byte[] data = ownedCandidate.Serialize(_documents.Values);
                Volatile.Write(ref _persistedSnapshotBytes, data.LongLength);
                await _storage.WriteDerivedDataAsync(
                    Definition.Id,
                    GenerationDataName(nextGeneration),
                    data,
                    cancellationToken).ConfigureAwait(false);
                await _storage.WriteDerivedDataAsync(
                    Definition.Id,
                    SearchIndexDerivedDataName,
                    data,
                    cancellationToken).ConfigureAwait(false);
                DateTimeOffset activatedAt = _timeProvider.GetUtcNow();
                IndexGenerationManifest manifest = new(
                    nextGeneration,
                    _activeGeneration,
                    targetKind,
                    _index.VectorKind,
                    activatedAt);
                await _storage.WriteDerivedDataAsync(
                    Definition.Id,
                    SearchIndexManifestDataName,
                    IndexGenerationManifestCodec.Serialize(manifest),
                    cancellationToken).ConfigureAwait(false);

                _previousIndex?.Dispose();
                _previousIndex = _index;
                _previousGeneration = _activeGeneration;
                _activeGeneration = nextGeneration;
                _index = ownedCandidate;
                candidate = null;
                _lastMigration = activatedAt;
                _migrationState = "active";
                _metrics.RecordIndexMigration(Stopwatch.GetElapsedTime(started), succeeded: true, validation.Recall);
                recorded = true;
                ResetStatistics(activatedAt);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _migrationState = "cancelled";
        }
        catch (RuntimeEvictedException)
        {
            _migrationState = "cancelled";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _migrationState = "failed";
            _migrationReason = exception.Message;
        }
        finally
        {
            if (!recorded)
            {
                _metrics.RecordIndexMigration(Stopwatch.GetElapsedTime(started), succeeded: false, recall: 0);
            }

            candidate?.Dispose();
        }
    }

    private IndexValidationResult ValidateCandidate(CollectionSearchIndex candidate)
    {
        if (candidate.Count != _documents.Count)
        {
            return new IndexValidationResult(false, 0, 0, "Candidate document count does not match the active generation.");
        }

        DocumentRecord[] samples = _documents.Values
            .OrderBy(static document => document.Id, StringComparer.Ordinal)
            .Take(_autoIndexOptions.ValidationSampleSize)
            .ToArray();
        if (samples.Length == 0)
        {
            return new IndexValidationResult(true, 1, 1, "Empty candidate generation validated.");
        }

        CollectionDefinition exactDefinition = Definition with
        {
            VectorIndex = Definition.VectorIndex with
            {
                Quantization = VectorQuantizationKind.Float32,
            },
        };
        using CollectionSearchIndex exact = new(
            exactDefinition,
            VectorIndexKind.Flat,
            _documents.Values,
            persistedVectorIndex: null,
            _bm25K1,
            _bm25B,
            _maximumTermsPerDocument);
        double recall = 0;
        const int repetitions = 3;
        long activeTicks = 0;
        long candidateTicks = 0;
        foreach (DocumentRecord sample in samples)
        {
            int limit = Math.Min(10, _documents.Count);
            SearchRequest request = new() { Mode = SearchMode.Vector, Vector = sample.Vector, Limit = limit };
            HashSet<string> truth = exact.Search(request, _candidateMultiplier)
                .Select(static result => result.Id)
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<HybridRankedResult> candidateResults = candidate.Search(request, _candidateMultiplier);
            recall += candidateResults.Count(result => truth.Contains(result.Id)) / (double)truth.Count;
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                long started = Stopwatch.GetTimestamp();
                _ = _index.Search(request, _candidateMultiplier);
                activeTicks += Stopwatch.GetTimestamp() - started;
                started = Stopwatch.GetTimestamp();
                _ = candidate.Search(request, _candidateMultiplier);
                candidateTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        recall /= samples.Length;
        double gain = activeTicks <= 0 ? 0 : (activeTicks - candidateTicks) / (double)activeTicks;
        bool accepted = recall >= _autoIndexOptions.MinimumRecall && gain >= _autoIndexOptions.MinimumPerformanceGain;
        string reason = FormattableString.Invariant(
            $"validation recall={recall:F4}, measuredGain={gain:P2}, samples={samples.Length}");
        return new IndexValidationResult(accepted, recall, gain, reason);
    }

    private AutoIndexDecision SelectAutoIndex()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        double elapsedSeconds = Math.Max(1, (now - _statisticsStarted).TotalSeconds);
        long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        AutoIndexSignals signals = new()
        {
            VectorCount = _documents.Count,
            Dimension = Definition.Dimension,
            EstimatedResidentBytes = checked((long)_documents.Count * Definition.Dimension * sizeof(float)),
            AvailableMemoryBytes = availableMemory,
            QueriesPerSecond = _searchCount / elapsedSeconds,
            UpdatesPerSecond = _mutationCount / elapsedSeconds,
            DeletionRatio = _mutationCount == 0 ? 0 : (double)_deletionCount / _mutationCount,
            AverageQueryMilliseconds = _searchCount == 0
                ? 0
                : _queryElapsedTicks * 1_000D / Stopwatch.Frequency / _searchCount,
            DiskReadMilliseconds = 0,
            Quantization = Definition.VectorIndex.Quantization,
        };
        VectorIndexKind current = _index?.VectorKind ?? VectorIndexKind.Flat;
        return AutoIndexSelector.Select(signals, current, _autoIndexOptions, Definition.VectorIndex.PqSubvectorCount);
    }

    private void ResetStatistics(DateTimeOffset now)
    {
        _statisticsStarted = now;
        _searchCount = 0;
        _mutationCount = 0;
        _deletionCount = 0;
        _queryElapsedTicks = 0;
    }

    private string GetDiskAnnArtifactDirectory(string generation) => Path.Combine(
        Path.GetFullPath(_diskAnnOptions.Path),
        Definition.Id.ToString("N"),
        generation);

    private static string GenerationDataName(long generation) => $"search-index-generation-{generation}";

    private async Task PersistVectorIndexAsync(CancellationToken cancellationToken)
    {
        byte[] data = _index.Serialize(_documents.Values);
        Volatile.Write(ref _persistedSnapshotBytes, data.LongLength);
        if (Definition.VectorIndex.Kind == VectorIndexKind.Auto)
        {
            await _storage.WriteDerivedDataAsync(
                Definition.Id,
                GenerationDataName(_activeGeneration),
                data,
                cancellationToken).ConfigureAwait(false);
            if (_previousIndex is not null && _previousGeneration.HasValue)
            {
                byte[] previousData = _previousIndex.Serialize(_documents.Values);
                await _storage.WriteDerivedDataAsync(
                    Definition.Id,
                    GenerationDataName(_previousGeneration.Value),
                    previousData,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await _storage.WriteDerivedDataAsync(Definition.Id, SearchIndexDerivedDataName, data, cancellationToken).ConfigureAwait(false);
        if (Definition.VectorIndex.Kind == VectorIndexKind.Auto)
        {
            IndexGenerationManifest manifest = new(
                _activeGeneration,
                _previousGeneration,
                _index.VectorKind,
                _previousIndex?.VectorKind,
                _lastMigration == DateTimeOffset.MinValue ? _timeProvider.GetUtcNow() : _lastMigration);
            await _storage.WriteDerivedDataAsync(
                Definition.Id,
                SearchIndexManifestDataName,
                IndexGenerationManifestCodec.Serialize(manifest),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static VectorIndexKind ResolveVectorKind(
        CollectionDefinition definition,
        int documentCount,
        int autoHnswThreshold) => definition.VectorIndex.Kind switch
        {
            VectorIndexKind.Auto => documentCount >= autoHnswThreshold ? VectorIndexKind.Hnsw : VectorIndexKind.Flat,
            VectorIndexKind.Flat => VectorIndexKind.Flat,
            VectorIndexKind.Hnsw => VectorIndexKind.Hnsw,
            VectorIndexKind.IvfFlat => VectorIndexKind.IvfFlat,
            VectorIndexKind.IvfPq => VectorIndexKind.IvfPq,
            VectorIndexKind.DiskAnn => VectorIndexKind.DiskAnn,
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.VectorIndex.Kind, "Unknown vector-index kind."),
        };

    private long EstimateResidentBytes()
    {
        VectorIndexConfiguration configuration = Definition.VectorIndex;
        double bytesPerVector = _index.VectorKind switch
        {
            VectorIndexKind.Flat when configuration.Quantization == VectorQuantizationKind.Float16 =>
                Definition.Dimension * sizeof(ushort),
            VectorIndexKind.Flat when configuration.Quantization == VectorQuantizationKind.Int8 =>
                Definition.Dimension + sizeof(float) * 2,
            VectorIndexKind.Hnsw =>
                Definition.Dimension * sizeof(float) + configuration.HnswM * sizeof(int) * 2,
            VectorIndexKind.IvfPq =>
                Definition.Dimension * sizeof(float) + configuration.PqSubvectorCount,
            VectorIndexKind.DiskAnn =>
                (double)configuration.DiskAnnCachePages * configuration.DiskAnnPageSize / Math.Max(1, _documents.Count) +
                Math.Min(_documents.Count, configuration.DiskAnnDeltaThreshold) *
                (double)Definition.Dimension * sizeof(float) / Math.Max(1, _documents.Count),
            _ => Definition.Dimension * sizeof(float),
        };
        double total = Math.Max(0, bytesPerVector * _documents.Count);
        return total >= long.MaxValue ? long.MaxValue : (long)total;
    }

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
