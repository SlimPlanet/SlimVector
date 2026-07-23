using System.Collections.Concurrent;
using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Storage;

public sealed class FileSystemDataGroupStorage : IDataGroupStorage
{
    private const int StorageFormatVersion = 2;
    private const string FormatFileName = "storage-format-v2.json";
    private readonly StorageSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly StorageMetrics _metrics;
    private readonly ConcurrentDictionary<string, Lazy<Task<FileSystemStorageEngine>>> _groups =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public FileSystemDataGroupStorage(
        StorageSettings settings,
        TimeProvider? timeProvider = null,
        StorageMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Path);
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

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(DataGroupsPath);
            string formatPath = Path.Combine(_settings.Path, FormatFileName);
            if (File.Exists(formatPath))
            {
                await using FileStream input = new(
                    formatPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4_096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                DistributedStorageFormat? format = await JsonSerializer.DeserializeAsync(
                    input,
                    StorageJsonContext.Default.DistributedStorageFormat,
                    cancellationToken).ConfigureAwait(false);
                if (format?.FormatVersion != StorageFormatVersion)
                {
                    throw new DomainException(
                        ErrorCodes.StorageCorrupted,
                        $"Distributed storage format '{format?.FormatVersion}' is unsupported.");
                }
            }
            else
            {
                string temporary = $"{formatPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    long length;
                    await using (FileStream output = new(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        4_096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await JsonSerializer.SerializeAsync(
                            output,
                            new DistributedStorageFormat(),
                            StorageJsonContext.Default.DistributedStorageFormat,
                            cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (_settings.FlushToDisk)
                        {
                            output.Flush(flushToDisk: true);
                            _metrics.RecordDurableFlush();
                        }

                        length = output.Position;
                    }

                    _metrics.RecordWrite(length);
                    File.Move(temporary, formatPath);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public IReadOnlyList<string> GetLocalDataGroupIds()
    {
        EnsureInitialized();
        IEnumerable<string> persisted = Directory.EnumerateDirectories(DataGroupsPath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))!;
        return persisted.Concat(_groups.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask EnsureDataGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        _ = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveDataGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ValidateGroupId(groupId);
        if (_groups.TryRemove(groupId, out Lazy<Task<FileSystemStorageEngine>>? lazy) && lazy.IsValueCreated)
        {
            FileSystemStorageEngine engine = await lazy.Value.ConfigureAwait(false);
            engine.Dispose();
        }

        string path = GetGroupPath(groupId);
        if (!Directory.Exists(path))
        {
            return;
        }

        string deletedRoot = Path.Combine(_settings.Path, ".deleted-data-groups");
        Directory.CreateDirectory(deletedRoot);
        string destination = Path.Combine(
            deletedRoot,
            $"{groupId}-{_timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.Move(path, destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask EnsureCollectionAsync(
        string groupId,
        CollectionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        CollectionDefinition? existing = await group.GetCollectionAsync(definition.Name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await group.CreateCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        else if (definition.UpdatedAt > existing.UpdatedAt ||
            definition.Placement?.Epoch != existing.Placement?.Epoch ||
            definition.VectorIndex != existing.VectorIndex)
        {
            await group.UpdateCollectionAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteCollectionAsync(
        string groupId,
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        CollectionDefinition? existing = await group.GetCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await group.DeleteCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadDocumentsAsync(
        string groupId,
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await group.LoadDocumentsAsync(collectionId, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException exception) when (exception.Code == ErrorCodes.CollectionNotFound)
        {
            return new Dictionary<string, DocumentRecord>(StringComparer.Ordinal);
        }
    }

    public async ValueTask<long> CountDocumentsAsync(
        string groupId,
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await group.CountDocumentsAsync(collectionId, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException exception) when (exception.Code == ErrorCodes.CollectionNotFound)
        {
            return 0;
        }
    }

    public async ValueTask AppendAsync(
        string groupId,
        Guid collectionId,
        IReadOnlyList<StorageOperation> operations,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        await group.AppendAsync(collectionId, operations, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> ReadDerivedDataAsync(
        string groupId,
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        return await group.ReadDerivedDataAsync(collectionId, name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteDerivedDataAsync(
        string groupId,
        Guid collectionId,
        string name,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        FileSystemStorageEngine group = await GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        await group.WriteDerivedDataAsync(collectionId, name, data, cancellationToken).ConfigureAwait(false);
    }

    public long GetAllocatedBytes(string groupId)
    {
        EnsureInitialized();
        ValidateGroupId(groupId);
        string path = GetGroupPath(groupId);
        return !Directory.Exists(path)
            ? 0
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(static file => new FileInfo(file).Length);
    }

    public void Dispose()
    {
        foreach (Lazy<Task<FileSystemStorageEngine>> lazy in _groups.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                lazy.Value.Result.Dispose();
            }
        }

        _groups.Clear();
        _initializationLock.Dispose();
    }

    private string DataGroupsPath => Path.Combine(_settings.Path, "data-groups");

    private async ValueTask<FileSystemStorageEngine> GetGroupAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        ValidateGroupId(groupId);
        Lazy<Task<FileSystemStorageEngine>> lazy = _groups.GetOrAdd(
            groupId,
            id => new Lazy<Task<FileSystemStorageEngine>>(
                () => CreateGroupAsync(id, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        FileSystemStorageEngine result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<FileSystemStorageEngine> CreateGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        FileSystemStorageEngine engine = new(
            _settings with { Path = GetGroupPath(groupId) },
            _timeProvider,
            _metrics);
        try
        {
            await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return engine;
        }
        catch
        {
            engine.Dispose();
            _groups.TryRemove(groupId, out _);
            throw;
        }
    }

    private string GetGroupPath(string groupId) => Path.Combine(DataGroupsPath, groupId);

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Distributed data-group storage is not initialized.");
        }
    }

    private static void ValidateGroupId(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (groupId.Length > 128 || groupId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Data-group ids may contain only ASCII letters, digits, '-', '_' and '.'.", nameof(groupId));
        }
    }
}
