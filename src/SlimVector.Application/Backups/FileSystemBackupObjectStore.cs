namespace SlimVector.Application.Backups;

internal sealed class FileSystemBackupObjectStore : IBackupObjectStore
{
    private readonly string _rootPath;

    public FileSystemBackupObjectStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public async ValueTask<bool> PutIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        string destination = Resolve(key);
        if (File.Exists(destination))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + $".{Guid.NewGuid():N}.partial";
        await WriteDurablyAsync(temporary, data, cancellationToken).ConfigureAwait(false);
        try
        {
            File.Move(temporary, destination, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destination))
        {
            File.Delete(temporary);
            return false;
        }
    }

    public async ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        string destination = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + $".{Guid.NewGuid():N}.partial";
        await WriteDurablyAsync(temporary, data, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    public async ValueTask<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        string path = Resolve(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Backup object '{key}' was not found.", path);
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(prefix);
        if (!Directory.Exists(path))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        string[] keys = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(static file => !file.EndsWith(".partial", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(_rootPath, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<string>>(keys);
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string normalized = key.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(static part => part is ".." or "."))
        {
            throw new ArgumentException("Backup object keys must be relative and may not traverse directories.", nameof(key));
        }

        string result = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        string rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!result.StartsWith(rootPrefix, StringComparison.Ordinal) && !string.Equals(result, _rootPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup object key escapes the configured root.", nameof(key));
        }

        return result;
    }

    private static async Task WriteDurablyAsync(
        string path,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }
}
