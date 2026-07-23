namespace SlimVector.Application.Backups;

public interface IBackupService
{
    ValueTask<BackupDescriptor> CreateBackupAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<BackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken = default);

    ValueTask VerifyBackupAsync(string backupId, CancellationToken cancellationToken = default);

    ValueTask RestoreFullAsync(string backupId, CancellationToken cancellationToken = default);

    ValueTask RestoreCollectionAsync(
        string backupId,
        string collectionName,
        string? restoredName = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    BackupMetricsSnapshot GetMetrics();
}
