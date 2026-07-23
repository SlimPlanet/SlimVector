using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;

namespace SlimVector.Application.Backups;

internal sealed partial class BackupHostedService : BackgroundService
{
    private readonly IBackupService _backups;
    private readonly BackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackupHostedService> _logger;

    public BackupHostedService(
        IBackupService backups,
        IOptions<BackupOptions> options,
        TimeProvider timeProvider,
        ILogger<BackupHostedService> logger)
    {
        _backups = backups;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(_options.Interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                BackupDescriptor backup = await _backups.CreateBackupAsync(stoppingToken).ConfigureAwait(false);
                BackupCompleted(_logger, backup.BackupId, backup.CollectionCount, backup.DocumentCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                BackupFailed(_logger, exception);
            }
        }
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Backup {BackupId} completed. Collections={CollectionCount} Documents={DocumentCount}")]
    private static partial void BackupCompleted(ILogger logger, string backupId, int collectionCount, long documentCount);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Scheduled backup failed")]
    private static partial void BackupFailed(ILogger logger, Exception exception);
}
