using System.Security.Cryptography;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Routing;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Backups;

public sealed class BackupService : IBackupService, IDisposable
{
    private const string ManifestsPrefix = "manifests";
    private const string BlobsPrefix = "blobs";
    private readonly BackupOptions _options;
    private readonly IStorageEngine _storage;
    private readonly IDataGroupStorage? _dataGroups;
    private readonly IDataNodeQueryClient? _queryClient;
    private readonly ILocalDataQueryService? _localQueries;
    private readonly ILocalRaftGroupManager? _localGroups;
    private readonly IConsensusCoordinator _consensus;
    private readonly IBackupObjectStore _objects;
    private readonly TimeProvider _timeProvider;
    private readonly byte[]? _encryptionKey;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private long _successfulBackups;
    private long _failedBackups;
    private long _successfulRestores;
    private long _failedRestores;
    private long _deduplicatedBlobs;
    private long _lastSuccessfulBackupTicks;
    private long _lastSuccessfulRestoreTicks;

    public BackupService(
        IOptions<BackupOptions> options,
        IStorageEngine storage,
        IConsensusCoordinator consensus,
        TimeProvider timeProvider,
        IHttpClientFactory? httpClientFactory = null,
        IDataGroupStorage? dataGroups = null,
        IDataNodeQueryClient? queryClient = null,
        ILocalDataQueryService? localQueries = null,
        ILocalRaftGroupManager? localGroups = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(consensus);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _storage = storage;
        _dataGroups = dataGroups;
        _queryClient = queryClient;
        _localQueries = localQueries;
        _localGroups = localGroups;
        _consensus = consensus;
        _timeProvider = timeProvider;
        _encryptionKey = _options.Encrypt ? Convert.FromBase64String(_options.EncryptionKey!) : null;
        _objects = _options.Provider switch
        {
            BackupProvider.FileSystem => new FileSystemBackupObjectStore(_options.Path),
            BackupProvider.S3 when httpClientFactory is not null =>
                new S3BackupObjectStore(_options, httpClientFactory.CreateClient("SlimVector.Backup.S3"), timeProvider),
            BackupProvider.S3 => throw new InvalidOperationException("An HTTP client factory is required for S3 backups."),
            _ => throw new OptionsValidationException(
                BackupOptions.SectionName,
                typeof(BackupOptions),
                [$"Unsupported backup provider '{_options.Provider}'."]),
        };
    }

    public async ValueTask<BackupDescriptor> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _consensus
                .ApplyReadBarrierAsync(collectionId: null, ReadConsistency.Linearizable, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<CollectionDefinition> collections = await _storage
                .ListCollectionsAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<BackupDescriptor> existingBackups = await ListBackupsCoreAsync(cancellationToken).ConfigureAwait(false);
            BackupDescriptor? parent = existingBackups.Count == 0 ? null : existingBackups[0];
            DateTimeOffset createdAt = _timeProvider.GetUtcNow();
            string backupId = CreateBackupId(createdAt);
            List<BackupCollectionEntry> entries = new(collections.Count);
            foreach (CollectionDefinition collection in collections.OrderBy(static item => item.Id))
            {
                await _consensus
                    .ApplyReadBarrierAsync(collection.Id, ReadConsistency.Linearizable, cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyDictionary<string, DocumentRecord> documents = await LoadBackupDocumentsAsync(
                    collection,
                    cancellationToken).ConfigureAwait(false);
                BackupCollectionPayload payload = new()
                {
                    Collection = RaftCommandCodec.FromDomain(collection),
                    Documents = documents.Values
                        .OrderBy(static document => document.Id, StringComparer.Ordinal)
                        .Select(RaftCommandCodec.FromDomain)
                        .ToArray(),
                };
                byte[] plaintext = MemoryPackSerializer.Serialize(payload);
                string hash = Convert.ToHexStringLower(SHA256.HashData(plaintext));
                bool created = await _objects
                    .PutIfAbsentAsync(BlobKey(hash), BackupCryptography.Pack(plaintext, _encryptionKey), cancellationToken)
                    .ConfigureAwait(false);
                if (!created)
                {
                    Interlocked.Increment(ref _deduplicatedBlobs);
                }

                entries.Add(new BackupCollectionEntry
                {
                    CollectionId = collection.Id,
                    CollectionName = collection.Name,
                    BlobHash = hash,
                    DocumentCount = documents.Count,
                });
            }

            BackupManifest manifest = new()
            {
                BackupId = backupId,
                CreatedAt = createdAt,
                ParentBackupId = parent?.BackupId,
                Collections = entries.ToArray(),
            };
            byte[] manifestBytes = MemoryPackSerializer.Serialize(manifest);
            await _objects
                .PutAsync(ManifestKey(backupId), BackupCryptography.Pack(manifestBytes, _encryptionKey), cancellationToken)
                .ConfigureAwait(false);
            await VerifyManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            await ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _successfulBackups);
            Interlocked.Exchange(ref _lastSuccessfulBackupTicks, createdAt.UtcTicks);
            return ToDescriptor(manifest);
        }
        catch
        {
            Interlocked.Increment(ref _failedBackups);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public ValueTask<IReadOnlyList<BackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
        ListBackupsCoreAsync(cancellationToken);

    public async ValueTask VerifyBackupAsync(string backupId, CancellationToken cancellationToken = default)
    {
        BackupManifest manifest = await ReadManifestAsync(backupId, cancellationToken).ConfigureAwait(false);
        await VerifyManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RestoreFullAsync(string backupId, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackupManifest manifest = await ReadManifestAsync(backupId, cancellationToken).ConfigureAwait(false);
            await VerifyManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CollectionDefinition> current = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (CollectionDefinition collection in current)
            {
                await _consensus.DeleteCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
            }

            foreach (BackupCollectionEntry entry in manifest.Collections)
            {
                BackupCollectionPayload payload = await ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
                await RestorePayloadAsync(payload, restoredName: null, cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            Interlocked.Increment(ref _successfulRestores);
            Interlocked.Exchange(ref _lastSuccessfulRestoreTicks, now.UtcTicks);
        }
        catch
        {
            Interlocked.Increment(ref _failedRestores);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask RestoreCollectionAsync(
        string backupId,
        string collectionName,
        string? restoredName = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        DomainValidation.ValidateCollectionName(collectionName);
        if (restoredName is not null)
        {
            DomainValidation.ValidateCollectionName(restoredName);
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackupManifest manifest = await ReadManifestAsync(backupId, cancellationToken).ConfigureAwait(false);
            BackupCollectionEntry entry = manifest.Collections.FirstOrDefault(item =>
                    string.Equals(item.CollectionName, collectionName, StringComparison.Ordinal))
                ?? throw new DomainException(
                    ErrorCodes.CollectionNotFound,
                    $"Collection '{collectionName}' is not present in backup '{backupId}'.");
            string targetName = restoredName ?? collectionName;
            CollectionDefinition? existing = await _storage.GetCollectionAsync(targetName, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !overwrite)
            {
                throw new DomainException(ErrorCodes.CollectionAlreadyExists, $"Collection '{targetName}' already exists.");
            }

            if (existing is not null)
            {
                await _consensus.DeleteCollectionAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            BackupCollectionPayload payload = await ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
            await RestorePayloadAsync(payload, restoredName, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Interlocked.Increment(ref _successfulRestores);
            Interlocked.Exchange(ref _lastSuccessfulRestoreTicks, now.UtcTicks);
        }
        catch
        {
            Interlocked.Increment(ref _failedRestores);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public BackupMetricsSnapshot GetMetrics()
    {
        long backupTicks = Volatile.Read(ref _lastSuccessfulBackupTicks);
        long restoreTicks = Volatile.Read(ref _lastSuccessfulRestoreTicks);
        return new BackupMetricsSnapshot
        {
            SuccessfulBackups = Volatile.Read(ref _successfulBackups),
            FailedBackups = Volatile.Read(ref _failedBackups),
            SuccessfulRestores = Volatile.Read(ref _successfulRestores),
            FailedRestores = Volatile.Read(ref _failedRestores),
            DeduplicatedBlobs = Volatile.Read(ref _deduplicatedBlobs),
            LastSuccessfulBackupAt = backupTicks == 0 ? null : new DateTimeOffset(backupTicks, TimeSpan.Zero),
            LastSuccessfulRestoreAt = restoreTicks == 0 ? null : new DateTimeOffset(restoreTicks, TimeSpan.Zero),
        };
    }

    public void Dispose() => _operationLock.Dispose();

    private async ValueTask<IReadOnlyList<BackupDescriptor>> ListBackupsCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keys = await _objects.ListAsync(ManifestsPrefix, cancellationToken).ConfigureAwait(false);
        List<BackupDescriptor> result = new(keys.Count);
        foreach (string key in keys.Where(static key => key.EndsWith(".manifest", StringComparison.Ordinal)))
        {
            byte[] packed = await _objects.GetAsync(key, cancellationToken).ConfigureAwait(false);
            BackupManifest manifest = DeserializeManifest(BackupCryptography.Unpack(packed, _encryptionKey));
            result.Add(ToDescriptor(manifest));
        }

        return result.OrderByDescending(static backup => backup.CreatedAt).ToArray();
    }

    private async ValueTask<BackupManifest> ReadManifestAsync(string backupId, CancellationToken cancellationToken)
    {
        ValidateBackupId(backupId);
        byte[] packed = await _objects.GetAsync(ManifestKey(backupId), cancellationToken).ConfigureAwait(false);
        BackupManifest manifest = DeserializeManifest(BackupCryptography.Unpack(packed, _encryptionKey));
        if (!string.Equals(manifest.BackupId, backupId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The backup manifest id does not match its object key.");
        }

        return manifest;
    }

    private async ValueTask VerifyManifestAsync(BackupManifest manifest, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        foreach (BackupCollectionEntry entry in manifest.Collections)
        {
            _ = await ReadPayloadAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<BackupCollectionPayload> ReadPayloadAsync(
        BackupCollectionEntry entry,
        CancellationToken cancellationToken)
    {
        byte[] packed = await _objects.GetAsync(BlobKey(entry.BlobHash), cancellationToken).ConfigureAwait(false);
        byte[] plaintext = BackupCryptography.Unpack(packed, _encryptionKey);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(plaintext));
        if (!string.Equals(actualHash, entry.BlobHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Backup blob for collection '{entry.CollectionName}' failed its SHA-256 integrity check.");
        }

        BackupCollectionPayload payload = MemoryPackSerializer.Deserialize<BackupCollectionPayload>(plaintext)
            ?? throw new InvalidDataException($"Backup blob for collection '{entry.CollectionName}' is empty.");
        if (payload.FormatVersion != 1 || payload.Collection.Id != entry.CollectionId ||
            payload.Documents.LongLength != entry.DocumentCount)
        {
            throw new InvalidDataException($"Backup blob for collection '{entry.CollectionName}' has inconsistent metadata.");
        }

        return payload;
    }

    private async ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadBackupDocumentsAsync(
        CollectionDefinition collection,
        CancellationToken cancellationToken)
    {
        if (_dataGroups is null)
        {
            return await _storage.LoadDocumentsAsync(collection.Id, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, DocumentRecord> documents = new(StringComparer.Ordinal);
        if (_queryClient is not null && _localQueries is not null && _localGroups is not null && collection.Placement is not null)
        {
            HashSet<string> hosted = _localGroups.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
            foreach (string groupId in collection.Placement.ReadRoutes()
                         .Select(static route => route.DataGroupId)
                         .Distinct(StringComparer.Ordinal))
            {
                IReadOnlyList<DocumentRecord> groupDocuments = hosted.Contains(groupId)
                    ? await _localQueries.GetDocumentsLocalAsync(
                        collection.Name,
                        groupId,
                        ids: null,
                        int.MaxValue,
                        ReadConsistency.Linearizable,
                        cancellationToken).ConfigureAwait(false)
                    : await _queryClient.GetDocumentsAsync(
                        collection.Name,
                        groupId,
                        ids: null,
                        int.MaxValue,
                        ReadConsistency.Linearizable,
                        cancellationToken).ConfigureAwait(false);
                MergeDocuments(documents, groupDocuments);
            }

            return documents;
        }

        foreach (string groupId in _dataGroups.GetLocalDataGroupIds())
        {
            IReadOnlyDictionary<string, DocumentRecord> groupDocuments = await _dataGroups
                .LoadDocumentsAsync(groupId, collection.Id, cancellationToken)
                .ConfigureAwait(false);
            MergeDocuments(documents, groupDocuments.Values);
        }

        return documents;
    }

    private static void MergeDocuments(
        Dictionary<string, DocumentRecord> target,
        IEnumerable<DocumentRecord> incoming)
    {
        foreach (DocumentRecord document in incoming)
        {
            if (!target.TryGetValue(document.Id, out DocumentRecord? existing) ||
                document.Version > existing.Version ||
                document.Version == existing.Version && document.UpdatedAt > existing.UpdatedAt)
            {
                target[document.Id] = document;
            }
        }
    }

    private async ValueTask RestorePayloadAsync(
        BackupCollectionPayload payload,
        string? restoredName,
        CancellationToken cancellationToken)
    {
        CollectionDefinition definition = RaftCommandCodec.ToDomain(payload.Collection);
        if (restoredName is not null)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            definition = definition with
            {
                Id = Guid.NewGuid(),
                Name = restoredName,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        if (_dataGroups is not null)
        {
            definition = definition with
            {
                Placement = _consensus.CreateInitialPlacement(
                    definition.Id,
                    CollectionPlacement.DefaultVirtualShardCount),
            };
        }

        await _consensus.UpsertCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
        foreach (RaftDocument[] chunk in payload.Documents.Chunk(500))
        {
            StorageOperation[] operations = chunk
                .Select(RaftCommandCodec.ToDomain)
                .Select(document => restoredName is null ? document : document with { UpdatedAt = definition.UpdatedAt })
                .Select(StorageOperation.Upsert)
                .ToArray();
            await _consensus.AppendAsync(definition, operations, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupDescriptor> backups = await ListBackupsCoreAsync(cancellationToken).ConfigureAwait(false);
        foreach (BackupDescriptor obsolete in backups.Skip(_options.RetentionCount))
        {
            await _objects.DeleteAsync(ManifestKey(obsolete.BackupId), cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> manifestKeys = await _objects.ListAsync(ManifestsPrefix, cancellationToken).ConfigureAwait(false);
        HashSet<string> referenced = new(StringComparer.Ordinal);
        foreach (string manifestKey in manifestKeys)
        {
            byte[] packed = await _objects.GetAsync(manifestKey, cancellationToken).ConfigureAwait(false);
            BackupManifest manifest = DeserializeManifest(BackupCryptography.Unpack(packed, _encryptionKey));
            referenced.UnionWith(manifest.Collections.Select(static entry => entry.BlobHash));
        }

        IReadOnlyList<string> blobKeys = await _objects.ListAsync(BlobsPrefix, cancellationToken).ConfigureAwait(false);
        foreach (string blobKey in blobKeys)
        {
            string hash = Path.GetFileNameWithoutExtension(blobKey);
            if (!referenced.Contains(hash))
            {
                await _objects.DeleteAsync(blobKey, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static BackupManifest DeserializeManifest(ReadOnlySpan<byte> bytes)
    {
        BackupManifest manifest = MemoryPackSerializer.Deserialize<BackupManifest>(bytes)
            ?? throw new InvalidDataException("The backup manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != 1)
        {
            throw new InvalidDataException($"Backup manifest version '{manifest.FormatVersion}' is unsupported.");
        }

        ValidateBackupId(manifest.BackupId);
        if (manifest.Collections.Select(static entry => entry.CollectionId).Distinct().Count() != manifest.Collections.Length ||
            manifest.Collections.Select(static entry => entry.CollectionName).Distinct(StringComparer.Ordinal).Count() != manifest.Collections.Length)
        {
            throw new InvalidDataException("The backup manifest contains duplicate collections.");
        }

        foreach (BackupCollectionEntry entry in manifest.Collections)
        {
            if (entry.BlobHash.Length != 64 || !entry.BlobHash.All(static character => char.IsAsciiHexDigit(character)))
            {
                throw new InvalidDataException("The backup manifest contains an invalid blob hash.");
            }
        }
    }

    private static BackupDescriptor ToDescriptor(BackupManifest manifest) => new()
    {
        BackupId = manifest.BackupId,
        CreatedAt = manifest.CreatedAt,
        ParentBackupId = manifest.ParentBackupId,
        CollectionCount = manifest.Collections.Length,
        DocumentCount = manifest.Collections.Sum(static entry => entry.DocumentCount),
    };

    private static string CreateBackupId(DateTimeOffset timestamp) =>
        $"{timestamp:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid():N}";

    private static void ValidateBackupId(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        if (backupId.Length > 96 || backupId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The backup id contains invalid characters.", nameof(backupId));
        }
    }

    private static string ManifestKey(string backupId) => $"{ManifestsPrefix}/{backupId}.manifest";

    private static string BlobKey(string hash) => $"{BlobsPrefix}/{hash}.blob";
}
