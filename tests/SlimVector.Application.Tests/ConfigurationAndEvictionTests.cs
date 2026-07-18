using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class ConfigurationAndEvictionTests
{
    [Fact]
    public void InvalidCriticalSecretsAndClusterTopologyFailOptionsValidation()
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Enabled"] = "true",
            ["Backup:Encrypt"] = "true",
            ["Backup:EncryptionKey"] = "invalid",
            ["GeoReplication:Enabled"] = "true",
            ["GeoReplication:SecondaryEndpoint"] = "http://secondary:8080",
            ["GeoReplication:SharedSecret"] = "short",
            ["Raft:Mode"] = "Cluster",
            ["Raft:PublicEndpoint"] = "http://127.0.0.1:3262",
            ["Raft:PublicApiEndpoint"] = "http://127.0.0.1:8080",
            ["Raft:Members:0"] = "http://127.0.0.1:3262",
            ["Raft:Members:1"] = "http://127.0.0.2:3262",
            ["Raft:Members:2"] = "http://127.0.0.3:3262",
        });
        ServiceCollection services = new();
        services.AddSlimVector(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<BackupOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GeoReplicationOptions>>().Value);
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RaftOptions>>().Value);
    }

    [Fact]
    public async Task IdleCollectionIsEvictedAndReloadedOnDemand()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        ManualTimeProvider timeProvider = new();
        using FileSystemStorageEngine storage = new(new StorageSettings
        {
            Path = directory.Path,
            FlushToDisk = false,
        }, timeProvider);
        using StorageRaftCommandApplier applier = new(storage);
        await using DirectConsensusCoordinator consensus = new(applier);
        await using AdaptiveWriteScheduler scheduler = new(
            consensus,
            Options.Create(new AdaptiveBatchingOptions()),
            Options.Create(new BackpressureOptions()),
            timeProvider);
        SlimVectorDatabase database = new(
            storage,
            consensus,
            scheduler,
            timeProvider,
            Options.Create(new CollectionsOptions
            {
                IdleTimeout = TimeSpan.FromMinutes(1),
                EvictionSweepInterval = TimeSpan.FromSeconds(10),
            }),
            Options.Create(new VectorIndexOptions()));
        await database.InitializeAsync(cancellationToken);
        await database.CreateCollectionAsync("lazy", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        Assert.Equal(0, await database.CountDocumentsAsync("lazy", cancellationToken));
        Assert.Equal(1, database.OpenCollectionCount);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await database.EvictInactiveCollectionsAsync(cancellationToken));
        Assert.Equal(0, database.OpenCollectionCount);
        Assert.Equal(0, await database.CountDocumentsAsync("lazy", cancellationToken));
        Assert.Equal(1, database.OpenCollectionCount);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }
}
