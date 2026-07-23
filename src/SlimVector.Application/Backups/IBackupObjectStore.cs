namespace SlimVector.Application.Backups;

internal interface IBackupObjectStore
{
    ValueTask<bool> PutIfAbsentAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask<byte[]> GetAsync(string key, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken);

    ValueTask DeleteAsync(string key, CancellationToken cancellationToken);
}
