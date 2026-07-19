using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Storage;

public sealed class FileSystemStorageEngine : IStorageEngine
{
    private const string CatalogFileName = "catalog.json";
    private readonly StorageSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly StorageMetrics _metrics;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _collectionLocks = new();
    private CatalogFile _catalog = new();
    private volatile bool _initialized;

    public FileSystemStorageEngine(
        StorageSettings settings,
        TimeProvider? timeProvider = null,
        StorageMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Path);
        if (settings.MaximumSegmentsBeforeCompaction < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.MaximumSegmentsBeforeCompaction,
                "At least two segments must be allowed before compaction.");
        }

        _settings = settings with { Path = Path.GetFullPath(settings.Path) };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics ?? new StorageMetrics();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_settings.Path);
            Directory.CreateDirectory(CollectionsPath);
            Directory.CreateDirectory(DeletedPath);
            string path = Path.Combine(_settings.Path, CatalogFileName);
            if (File.Exists(path))
            {
                _catalog = await ReadJsonAsync(path, StorageJsonContext.Default.CatalogFile, cancellationToken).ConfigureAwait(false)
                    ?? throw Corruption($"Catalog '{path}' is empty.");
                ValidateCatalog(_catalog);
            }
            else
            {
                _catalog = new CatalogFile();
                await WriteJsonAtomicAsync(path, _catalog, StorageJsonContext.Default.CatalogFile, cancellationToken).ConfigureAwait(false);
            }

            foreach (CollectionDefinition collection in _catalog.Collections)
            {
                await EnsureCollectionLayoutAsync(collection.Id, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<CollectionDefinition>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _catalog.Collections
                .OrderBy(static collection => collection.Name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask<CollectionDefinition?> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        DomainValidation.ValidateCollectionName(name);
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _catalog.Collections.Find(collection => string.Equals(collection.Name, name, StringComparison.Ordinal));
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask CreateCollectionAsync(CollectionDefinition definition, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(definition);
        DomainValidation.ValidateCollectionName(definition.Name);
        DomainValidation.ValidateDimension(definition.Dimension);
        DomainValidation.ValidateVectorIndex(definition.VectorIndex, definition.Dimension);

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog.Collections.Exists(collection => string.Equals(collection.Name, definition.Name, StringComparison.Ordinal)))
            {
                throw new DomainException(ErrorCodes.CollectionAlreadyExists, $"Collection '{definition.Name}' already exists.");
            }

            if (_catalog.Collections.Exists(collection => collection.Id == definition.Id))
            {
                throw new DomainException(ErrorCodes.CollectionAlreadyExists, $"Collection id '{definition.Id}' already exists.");
            }

            await EnsureCollectionLayoutAsync(definition.Id, cancellationToken).ConfigureAwait(false);
            CatalogFile next = _catalog with
            {
                Generation = _catalog.Generation + 1,
                Collections = [.. _catalog.Collections, definition],
            };
            await PersistCatalogAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask UpdateCollectionAsync(CollectionDefinition definition, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(definition);
        DomainValidation.ValidateCollectionName(definition.Name);
        DomainValidation.ValidateVectorIndex(definition.VectorIndex, definition.Dimension);

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = _catalog.Collections.FindIndex(collection => collection.Id == definition.Id);
            if (index < 0)
            {
                throw new DomainException(ErrorCodes.CollectionNotFound, $"Collection id '{definition.Id}' was not found.");
            }

            if (_catalog.Collections.Exists(collection =>
                    collection.Id != definition.Id && string.Equals(collection.Name, definition.Name, StringComparison.Ordinal)))
            {
                throw new DomainException(ErrorCodes.CollectionAlreadyExists, $"Collection '{definition.Name}' already exists.");
            }

            List<CollectionDefinition> definitions = [.. _catalog.Collections];
            definitions[index] = definition;
            CatalogFile next = _catalog with { Generation = _catalog.Generation + 1, Collections = definitions };
            await PersistCatalogAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        DomainValidation.ValidateCollectionName(name);
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollectionDefinition? definition = _catalog.Collections.Find(collection => string.Equals(collection.Name, name, StringComparison.Ordinal));
            if (definition is null)
            {
                throw new DomainException(ErrorCodes.CollectionNotFound, $"Collection '{name}' was not found.");
            }

            CatalogFile next = _catalog with
            {
                Generation = _catalog.Generation + 1,
                Collections = _catalog.Collections.Where(collection => collection.Id != definition.Id).ToList(),
            };
            await PersistCatalogAsync(next, cancellationToken).ConfigureAwait(false);

            string source = GetCollectionPath(definition.Id);
            if (Directory.Exists(source))
            {
                string suffix = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
                string destination = Path.Combine(DeletedPath, $"{definition.Id:N}-{suffix}");
                Directory.Move(source, destination);
            }

            _collectionLocks.TryRemove(definition.Id, out _);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadDocumentsAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        SemaphoreSlim collectionLock = GetCollectionLock(collectionId);
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureKnownCollection(collectionId);
            string segmentsPath = GetSegmentsPath(collectionId);
            Dictionary<string, DocumentRecord> documents = new(StringComparer.Ordinal);
            List<SegmentDescriptor> descriptors = [];
            foreach (string file in Directory.EnumerateFiles(segmentsPath, "*.segment").Order(StringComparer.Ordinal))
            {
                (SegmentPayload payload, SegmentDescriptor descriptor) = await ReadSegmentAsync(file, collectionId, cancellationToken).ConfigureAwait(false);
                foreach (StorageOperation operation in payload.Operations)
                {
                    Apply(documents, operation);
                }

                descriptors.Add(descriptor);
            }

            await ReconcileManifestAsync(collectionId, descriptors, cancellationToken).ConfigureAwait(false);
            return documents.ToDictionary(static pair => pair.Key, static pair => pair.Value.DeepCopy(), StringComparer.Ordinal);
        }
        finally
        {
            collectionLock.Release();
        }
    }

    public async ValueTask AppendAsync(
        Guid collectionId,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return;
        }

        SemaphoreSlim collectionLock = GetCollectionLock(collectionId);
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureKnownCollection(collectionId);
            CollectionManifest manifest = await ReadManifestAsync(collectionId, cancellationToken).ConfigureAwait(false);
            long sequence = manifest.Segments.Count == 0 ? 1 : checked(manifest.Segments.Max(static segment => segment.Sequence) + 1);
            SegmentPayload payload = new()
            {
                CollectionId = collectionId,
                Sequence = sequence,
                CreatedAt = _timeProvider.GetUtcNow(),
                Operations = operations.ToList(),
            };

            SegmentDescriptor descriptor = await WriteSegmentAsync(payload, cancellationToken).ConfigureAwait(false);
            CollectionManifest next = manifest with
            {
                Generation = manifest.Generation + 1,
                Segments = [.. manifest.Segments, descriptor],
            };
            await WriteManifestAsync(collectionId, next, cancellationToken).ConfigureAwait(false);
            if (next.Segments.Count >= _settings.MaximumSegmentsBeforeCompaction)
            {
                await CompactCoreAsync(collectionId, next, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            collectionLock.Release();
        }
    }

    public async ValueTask CompactAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        SemaphoreSlim collectionLock = GetCollectionLock(collectionId);
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureKnownCollection(collectionId);
            CollectionManifest manifest = await ReadManifestAsync(collectionId, cancellationToken).ConfigureAwait(false);
            await CompactCoreAsync(collectionId, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            collectionLock.Release();
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

    private async ValueTask CompactCoreAsync(
        Guid collectionId,
        CollectionManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.Segments.Count < 2)
        {
            return;
        }

        Dictionary<string, DocumentRecord> documents = new(StringComparer.Ordinal);
        foreach (SegmentDescriptor segment in manifest.Segments.OrderBy(static descriptor => descriptor.Sequence))
        {
            string file = Path.Combine(GetSegmentsPath(collectionId), segment.FileName);
            (SegmentPayload payload, _) = await ReadSegmentAsync(file, collectionId, cancellationToken).ConfigureAwait(false);
            foreach (StorageOperation operation in payload.Operations)
            {
                Apply(documents, operation);
            }
        }

        long sequence = checked(manifest.Segments.Max(static segment => segment.Sequence) + 1);
        SegmentPayload compacted = new()
        {
            CollectionId = collectionId,
            Sequence = sequence,
            CreatedAt = _timeProvider.GetUtcNow(),
            Operations = documents.Values.Select(StorageOperation.Upsert).ToList(),
        };
        SegmentDescriptor descriptor = await WriteSegmentAsync(compacted, cancellationToken).ConfigureAwait(false);

        string obsoletePath = Path.Combine(
            GetCollectionPath(collectionId),
            "obsolete",
            manifest.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(obsoletePath);
        foreach (SegmentDescriptor old in manifest.Segments)
        {
            string source = Path.Combine(GetSegmentsPath(collectionId), old.FileName);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(obsoletePath, old.FileName));
            }
        }

        CollectionManifest next = manifest with
        {
            Generation = manifest.Generation + 1,
            Segments = [descriptor],
        };
        await WriteManifestAsync(collectionId, next, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadDerivedDataAsync(
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateDerivedName(name);
        SemaphoreSlim collectionLock = GetCollectionLock(collectionId);
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureKnownCollection(collectionId);
            string path = GetDerivedPath(collectionId, name);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] contents = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            _metrics.RecordRead(contents.LongLength);
            int newline = Array.IndexOf(contents, (byte)'\n');
            if (newline != 64)
            {
                throw Corruption($"Derived data '{path}' has an invalid checksum header.");
            }

            ReadOnlySpan<byte> body = contents.AsSpan(newline + 1);
            string expectedChecksum = Encoding.ASCII.GetString(contents.AsSpan(0, newline));
            string actualChecksum = Convert.ToHexStringLower(SHA256.HashData(body));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expectedChecksum),
                    Encoding.ASCII.GetBytes(actualChecksum)))
            {
                throw Corruption($"Checksum verification failed for derived data '{path}'.");
            }

            return body.ToArray();
        }
        finally
        {
            collectionLock.Release();
        }
    }

    public async ValueTask WriteDerivedDataAsync(
        Guid collectionId,
        string name,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateDerivedName(name);
        SemaphoreSlim collectionLock = GetCollectionLock(collectionId);
        await collectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureKnownCollection(collectionId);
            string derivedPath = Path.Combine(GetCollectionPath(collectionId), "derived");
            Directory.CreateDirectory(derivedPath);
            string path = GetDerivedPath(collectionId, name);
            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                string checksum = Convert.ToHexStringLower(SHA256.HashData(data.Span));
                await using FileStream stream = new(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(checksum + "\n"), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                _metrics.RecordWrite(stream.Position);
                await FlushAsync(stream, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            collectionLock.Release();
        }
    }

    private string CollectionsPath => Path.Combine(_settings.Path, "collections");

    private string DeletedPath => Path.Combine(_settings.Path, ".deleted");

    private async ValueTask EnsureCollectionLayoutAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(GetSegmentsPath(collectionId));
        string manifestPath = GetManifestPath(collectionId);
        if (!File.Exists(manifestPath))
        {
            CollectionManifest manifest = new() { CollectionId = collectionId };
            await WriteManifestAsync(collectionId, manifest, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PersistCatalogAsync(CatalogFile next, CancellationToken cancellationToken)
    {
        await WriteJsonAtomicAsync(
            Path.Combine(_settings.Path, CatalogFileName),
            next,
            StorageJsonContext.Default.CatalogFile,
            cancellationToken).ConfigureAwait(false);
        _catalog = next;
    }

    private async ValueTask<CollectionManifest> ReadManifestAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        string path = GetManifestPath(collectionId);
        CollectionManifest manifest = await ReadJsonAsync(path, StorageJsonContext.Default.CollectionManifest, cancellationToken).ConfigureAwait(false)
            ?? throw Corruption($"Manifest '{path}' is empty.");
        if (manifest.FormatVersion != 1 || manifest.CollectionId != collectionId)
        {
            throw Corruption($"Manifest '{path}' has an unsupported format or collection id.");
        }

        return manifest;
    }

    private ValueTask WriteManifestAsync(Guid collectionId, CollectionManifest manifest, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(GetManifestPath(collectionId), manifest, StorageJsonContext.Default.CollectionManifest, cancellationToken);

    private async ValueTask ReconcileManifestAsync(
        Guid collectionId,
        List<SegmentDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        CollectionManifest manifest = await ReadManifestAsync(collectionId, cancellationToken).ConfigureAwait(false);
        bool equal = manifest.Segments.Count == descriptors.Count && manifest.Segments
            .OrderBy(static segment => segment.Sequence)
            .SequenceEqual(descriptors.OrderBy(static segment => segment.Sequence));
        if (!equal)
        {
            CollectionManifest repaired = manifest with
            {
                Generation = manifest.Generation + 1,
                Segments = descriptors.OrderBy(static segment => segment.Sequence).ToList(),
            };
            await WriteManifestAsync(collectionId, repaired, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<SegmentDescriptor> WriteSegmentAsync(SegmentPayload payload, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, StorageJsonContext.Default.SegmentPayload);
        string checksum = Convert.ToHexStringLower(SHA256.HashData(body));
        string fileName = $"{payload.Sequence:D20}.segment";
        string path = Path.Combine(GetSegmentsPath(payload.CollectionId), fileName);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        await using (FileStream stream = new(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] header = Encoding.ASCII.GetBytes(checksum + "\n");
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            _metrics.RecordWrite(stream.Position);
            await FlushAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: false);
        return new SegmentDescriptor { Sequence = payload.Sequence, FileName = fileName, Checksum = checksum, Length = body.LongLength };
    }

    private async ValueTask<(SegmentPayload Payload, SegmentDescriptor Descriptor)> ReadSegmentAsync(
        string path,
        Guid expectedCollectionId,
        CancellationToken cancellationToken)
    {
        byte[] contents = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        _metrics.RecordRead(contents.LongLength);
        int newline = Array.IndexOf(contents, (byte)'\n');
        if (newline != 64)
        {
            throw Corruption($"Segment '{path}' has an invalid checksum header.");
        }

        string expectedChecksum = Encoding.ASCII.GetString(contents.AsSpan(0, newline));
        ReadOnlySpan<byte> body = contents.AsSpan(newline + 1);
        string actualChecksum = Convert.ToHexStringLower(SHA256.HashData(body));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedChecksum),
                Encoding.ASCII.GetBytes(actualChecksum)))
        {
            throw Corruption($"Checksum verification failed for segment '{path}'.");
        }

        SegmentPayload payload = JsonSerializer.Deserialize(body, StorageJsonContext.Default.SegmentPayload)
            ?? throw Corruption($"Segment '{path}' has no payload.");
        if (payload.FormatVersion != 1 || payload.CollectionId != expectedCollectionId)
        {
            throw Corruption($"Segment '{path}' has an unsupported format or collection id.");
        }

        SegmentDescriptor descriptor = new()
        {
            Sequence = payload.Sequence,
            FileName = Path.GetFileName(path),
            Checksum = actualChecksum,
            Length = body.Length,
        };
        return (payload, descriptor);
    }

    private async ValueTask WriteJsonAtomicAsync<T>(
        string path,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
            _metrics.RecordWrite(stream.Position);
            await FlushAsync(stream, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async ValueTask<T?> ReadJsonAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        T? value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        _metrics.RecordRead(stream.Length);
        return value;
    }

    private async ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.FlushToDisk)
        {
            stream.Flush(flushToDisk: true);
            _metrics.RecordDurableFlush();
        }
    }

    private static void Apply(Dictionary<string, DocumentRecord> documents, StorageOperation operation)
    {
        switch (operation.Kind)
        {
            case DocumentMutationKind.Add:
            case DocumentMutationKind.Upsert:
            case DocumentMutationKind.Update:
                if (operation.Document is null)
                {
                    throw Corruption($"Persisted operation for '{operation.Id}' has no document.");
                }

                documents[operation.Id] = operation.Document;
                break;
            case DocumentMutationKind.Delete:
                documents.Remove(operation.Id);
                break;
            default:
                throw Corruption($"Unknown persisted operation '{operation.Kind}'.");
        }
    }

    private void EnsureKnownCollection(Guid collectionId)
    {
        if (!_catalog.Collections.Exists(collection => collection.Id == collectionId))
        {
            throw new DomainException(ErrorCodes.CollectionNotFound, $"Collection id '{collectionId}' was not found.");
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Storage must be initialized before it is used.");
        }
    }

    private static void ValidateCatalog(CatalogFile catalog)
    {
        if (catalog.FormatVersion != 1)
        {
            throw Corruption($"Catalog format version '{catalog.FormatVersion}' is unsupported.");
        }

        if (catalog.Collections.Select(static collection => collection.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Collections.Count ||
            catalog.Collections.Select(static collection => collection.Id).Distinct().Count() != catalog.Collections.Count)
        {
            throw Corruption("Catalog contains duplicate collection names or ids.");
        }
    }

    private SemaphoreSlim GetCollectionLock(Guid collectionId) =>
        _collectionLocks.GetOrAdd(collectionId, static _ => new SemaphoreSlim(1, 1));

    private string GetCollectionPath(Guid collectionId) => Path.Combine(CollectionsPath, collectionId.ToString("N"));

    private string GetSegmentsPath(Guid collectionId) => Path.Combine(GetCollectionPath(collectionId), "segments");

    private string GetManifestPath(Guid collectionId) => Path.Combine(GetCollectionPath(collectionId), "manifest.json");

    private string GetDerivedPath(Guid collectionId, string name) => Path.Combine(GetCollectionPath(collectionId), "derived", name + ".bin");

    private static void ValidateDerivedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Derived-data names may contain only ASCII letters, digits, '.', '_' and '-'.", nameof(name));
        }
    }

    private static DomainException Corruption(string message) => new(ErrorCodes.StorageCorrupted, message);
}
