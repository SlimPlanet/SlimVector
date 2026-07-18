using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;

namespace SlimVector.Application;

internal sealed class SlimVectorHostedService : BackgroundService
{
    private readonly ISlimVectorDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly CollectionsOptions _options;

    public SlimVectorHostedService(
        ISlimVectorDatabase database,
        TimeProvider timeProvider,
        IOptions<CollectionsOptions> options)
    {
        _database = database;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_options.EvictionSweepInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await _database.EvictInactiveCollectionsAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
