using System.Collections.Concurrent;
using MemoryPack;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft;

public sealed class StorageRaftCommandApplier : IRaftCommandApplier, IDisposable
{
    private readonly IStorageEngine _storage;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _collectionLocks = new();
    private readonly string[] _dataGroupIds;

    public StorageRaftCommandApplier(IStorageEngine storage, IReadOnlyList<string>? dataGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _dataGroupIds = (dataGroupIds ?? ["data-0"])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (_dataGroupIds.Length == 0)
        {
            throw new ArgumentException("At least one data group is required.", nameof(dataGroupIds));
        }
    }

    public event Action<Guid?>? StateChanged;

    public ValueTask ApplyAsync(RaftCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Kind switch
        {
            RaftCommandKind.CatalogUpsert when command.CatalogUpsert is not null =>
                ApplyCatalogUpsertAsync(command.CatalogUpsert, cancellationToken),
            RaftCommandKind.CatalogDelete when command.CatalogDelete is not null =>
                ApplyCatalogDeleteAsync(command.CatalogDelete, cancellationToken),
            RaftCommandKind.DataBatch when command.DataBatch is not null =>
                ApplyDataBatchAsync(command.DataBatch, command.GroupId, cancellationToken),
            RaftCommandKind.ShardBatch when command.ShardBatch is not null =>
                ApplyShardBatchAsync(command.ShardBatch, command.GroupId, cancellationToken),
            _ => ValueTask.FromException(new InvalidDataException("The Raft command payload is inconsistent.")),
        };
    }

    public async ValueTask<byte[]> CaptureSnapshotAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CollectionDefinition> allCollections = await _storage
                .ListCollectionsAsync(cancellationToken)
                .ConfigureAwait(false);
            CollectionDefinition[] selected = string.Equals(groupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal)
                ? allCollections.ToArray()
                : allCollections
                    .Where(collection => CollectionBelongsToGroup(collection, groupId))
                    .ToArray();
            List<RaftCollectionSnapshot> collections = new(selected.Length);
            foreach (CollectionDefinition collection in selected.OrderBy(static item => item.Id))
            {
                RaftDocument[] documents = [];
                if (!string.Equals(groupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal))
                {
                    SemaphoreSlim collectionLock = _collectionLocks.GetOrAdd(collection.Id, static _ => new SemaphoreSlim(1, 1));
                    await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        IReadOnlyDictionary<string, DocumentRecord> current = await _storage
                            .LoadDocumentsAsync(collection.Id, cancellationToken)
                            .ConfigureAwait(false);
                        documents = current.Values
                            .Where(document => string.Equals(
                                GetShardRoute(collection, document.Id).DataGroupId,
                                groupId,
                                StringComparison.Ordinal))
                            .OrderBy(static document => document.Id, StringComparer.Ordinal)
                            .Select(RaftCommandCodec.FromDomain)
                            .ToArray();
                    }
                    finally
                    {
                        collectionLock.Release();
                    }
                }

                collections.Add(new RaftCollectionSnapshot
                {
                    Definition = RaftCommandCodec.FromDomain(collection),
                    Documents = documents,
                });
            }

            return MemoryPackSerializer.Serialize(new RaftApplicationSnapshot
            {
                GroupId = groupId,
                Collections = collections.ToArray(),
            });
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask RestoreSnapshotAsync(
        string groupId,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (snapshot.IsEmpty)
        {
            return;
        }

        RaftApplicationSnapshot state = MemoryPackSerializer.Deserialize<RaftApplicationSnapshot>(snapshot.Span)
            ?? throw new InvalidDataException("The Raft application snapshot is empty.");
        if (state.FormatVersion != 1 || !string.Equals(state.GroupId, groupId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Raft application snapshot version or group id is incompatible.");
        }

        CollectionDefinition[] definitions = state.Collections
            .Select(static collection => RaftCommandCodec.ToDomain(collection.Definition))
            .ToArray();
        if (string.Equals(groupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal))
        {
            await RestoreCatalogSnapshotAsync(definitions, cancellationToken).ConfigureAwait(false);
            StateChanged?.Invoke(null);
            return;
        }

        foreach ((RaftCollectionSnapshot collectionSnapshot, CollectionDefinition definition) in state.Collections.Zip(definitions))
        {
            if (!CollectionBelongsToGroup(definition, groupId))
            {
                throw new InvalidDataException($"Collection '{definition.Id}' does not belong to Raft group '{groupId}'.");
            }

            await EnsureCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
            SemaphoreSlim collectionLock = _collectionLocks.GetOrAdd(definition.Id, static _ => new SemaphoreSlim(1, 1));
            await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IReadOnlyDictionary<string, DocumentRecord> current = await _storage
                    .LoadDocumentsAsync(definition.Id, cancellationToken)
                    .ConfigureAwait(false);
                DocumentRecord[] restored = collectionSnapshot.Documents
                    .Select(RaftCommandCodec.ToDomain)
                    .ToArray();
                HashSet<string> restoredIds = restored.Select(static document => document.Id).ToHashSet(StringComparer.Ordinal);
                StorageOperation[] replacement = [
                    .. current.Keys
                        .Where(id => string.Equals(GetShardRoute(definition, id).DataGroupId, groupId, StringComparison.Ordinal) &&
                            !restoredIds.Contains(id))
                        .Select(id => StorageOperation.Delete(id, current[id].Version + 1)),
                    .. restored.Select(StorageOperation.Upsert),
                ];
                if (replacement.Length > 0)
                {
                    await _storage.AppendAsync(definition.Id, replacement, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                collectionLock.Release();
            }

            StateChanged?.Invoke(definition.Id);
        }
    }

    public void Dispose()
    {
        _catalogLock.Dispose();
        foreach (SemaphoreSlim collectionLock in _collectionLocks.Values)
        {
            collectionLock.Dispose();
        }

        _collectionLocks.Clear();
    }

    private async ValueTask ApplyCatalogUpsertAsync(
        CatalogUpsertCommand command,
        CancellationToken cancellationToken)
    {
        CollectionDefinition incoming = RaftCommandCodec.ToDomain(command.Collection);
        incoming = incoming.Placement is null
            ? incoming with { Placement = CollectionPlacement.Create(incoming.Id, [command.DataGroupId]) }
            : incoming;
        await EnsureCollectionAsync(incoming, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke(incoming.Id);
    }

    private async ValueTask ApplyCatalogDeleteAsync(
        CatalogDeleteCommand command,
        CancellationToken cancellationToken)
    {
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            CollectionDefinition? existing = collections.FirstOrDefault(collection => collection.Id == command.CollectionId);
            if (existing is not null)
            {
                await _storage.DeleteCollectionAsync(existing.Name, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _catalogLock.Release();
        }

        StateChanged?.Invoke(command.CollectionId);
    }

    private async ValueTask ApplyDataBatchAsync(
        DataBatchCommand command,
        string groupId,
        CancellationToken cancellationToken)
    {
        CollectionDefinition collection = RaftCommandCodec.ToDomain(command.Collection);
        if (collection.Id != command.CollectionId)
        {
            throw new InvalidDataException("The Raft data batch collection identifiers do not match.");
        }

        if (collection.Placement is not null && command.RoutingEpoch > 0)
        {
            if (command.RoutingEpoch != collection.Placement.Epoch)
            {
                throw new DomainException(
                    ErrorCodes.RoutingEpochMismatch,
                    $"Routing epoch {command.RoutingEpoch} does not match catalog epoch {collection.Placement.Epoch}.");
            }

            foreach (RaftStorageOperation operation in command.Operations)
            {
                ShardRoute route = collection.Placement.Resolve(collection.Id, operation.Id);
                if (!string.Equals(route.DataGroupId, groupId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Document '{operation.Id}' belongs to data group '{route.DataGroupId}', not '{groupId}'.");
                }

                if (command.ShardId >= 0 && route.ShardId != command.ShardId)
                {
                    throw new InvalidDataException(
                        $"Document '{operation.Id}' belongs to virtual shard {route.ShardId}, not {command.ShardId}.");
                }
            }
        }

        await EnsureCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        SemaphoreSlim collectionLock = _collectionLocks.GetOrAdd(collection.Id, static _ => new SemaphoreSlim(1, 1));
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyDictionary<string, DocumentRecord> current = await _storage
                .LoadDocumentsAsync(collection.Id, cancellationToken)
                .ConfigureAwait(false);
            Dictionary<string, DocumentRecord> working = current.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            List<StorageOperation> effective = [];
            foreach (RaftStorageOperation serialized in command.Operations)
            {
                StorageOperation operation = RaftCommandCodec.ToStorage(serialized);
                if (operation.Kind == DocumentMutationKind.Delete)
                {
                    if (working.TryGetValue(operation.Id, out DocumentRecord? existing) &&
                        (operation.Version == 0 || existing.Version < operation.Version))
                    {
                        effective.Add(operation);
                        working.Remove(operation.Id);
                    }

                    continue;
                }

                DocumentRecord document = operation.Document!;
                DomainValidation.ValidateDocument(document, collection.Dimension);
                if (!working.TryGetValue(document.Id, out DocumentRecord? currentDocument) ||
                    currentDocument.Version < document.Version)
                {
                    effective.Add(operation);
                    working[document.Id] = document;
                }
            }

            if (effective.Count > 0)
            {
                await _storage.AppendAsync(collection.Id, effective, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            collectionLock.Release();
        }

        StateChanged?.Invoke(collection.Id);
    }

    private async ValueTask ApplyShardBatchAsync(
        ShardBatchCommand command,
        string groupId,
        CancellationToken cancellationToken)
    {
        if (command.Batches.Length == 0)
        {
            throw new InvalidDataException("A Raft shard batch may not be empty.");
        }

        foreach (DataBatchCommand batch in command.Batches)
        {
            await ApplyDataBatchAsync(batch, groupId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureCollectionAsync(
        CollectionDefinition incoming,
        CancellationToken cancellationToken)
    {
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            CollectionDefinition? byId = collections.FirstOrDefault(collection => collection.Id == incoming.Id);
            if (byId is null)
            {
                CollectionDefinition? byName = collections.FirstOrDefault(collection =>
                    string.Equals(collection.Name, incoming.Name, StringComparison.Ordinal));
                if (byName is not null)
                {
                    throw new InvalidDataException(
                        $"Collection name '{incoming.Name}' belongs to id '{byName.Id}', not '{incoming.Id}'.");
                }

                await _storage.CreateCollectionAsync(incoming, cancellationToken).ConfigureAwait(false);
            }
            else if (incoming.UpdatedAt > byId.UpdatedAt)
            {
                await _storage.UpdateCollectionAsync(incoming, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async ValueTask RestoreCatalogSnapshotAsync(
        IReadOnlyList<CollectionDefinition> definitions,
        CancellationToken cancellationToken)
    {
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CollectionDefinition> current = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            HashSet<Guid> restoredIds = definitions.Select(static definition => definition.Id).ToHashSet();
            foreach (CollectionDefinition obsolete in current.Where(collection => !restoredIds.Contains(collection.Id)))
            {
                await _storage.DeleteCollectionAsync(obsolete.Name, cancellationToken).ConfigureAwait(false);
            }

            current = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (CollectionDefinition definition in definitions)
            {
                CollectionDefinition? existing = current.FirstOrDefault(collection => collection.Id == definition.Id);
                if (existing is null)
                {
                    await _storage.CreateCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
                }
                else if (existing != definition)
                {
                    await _storage.UpdateCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private string GetDataGroupId(Guid collectionId) =>
        RaftGroupAssignment.GetDataGroupId(collectionId, _dataGroupIds);

    private bool CollectionBelongsToGroup(CollectionDefinition collection, string groupId) =>
        collection.Placement?.ReadRoutes().Any(route => string.Equals(route.DataGroupId, groupId, StringComparison.Ordinal)) ??
        string.Equals(GetDataGroupId(collection.Id), groupId, StringComparison.Ordinal);

    private ShardRoute GetShardRoute(CollectionDefinition collection, string documentId) =>
        collection.Placement?.Resolve(collection.Id, documentId) ??
        new ShardRoute(0, GetDataGroupId(collection.Id), 0);
}
