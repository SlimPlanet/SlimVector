using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Indexes;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class AutoIndexMigrationTests
{
    [Fact(Timeout = 60_000)]
    public async Task FlatToHnswToIvfMigrationRemainsSearchableAndRollbackSurvivesRestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        AutoIndexOptions auto = MigrationOptions();
        auto.AllowedIndexes = [VectorIndexKind.Flat];
        DiskAnnOptions diskAnn = new() { Path = Path.Combine(directory.Path, "diskann") };

        await using (AutoDatabaseFixture first = await AutoDatabaseFixture.CreateAsync(
                         directory.Path,
                         auto,
                         diskAnn,
                         cancellationToken))
        {
            await first.Database.CreateCollectionAsync(
                "automatic",
                16,
                DistanceMetric.Euclidean,
                AutoConfiguration(),
                cancellationToken);
            DocumentMutation[] mutations = Enumerable.Range(0, 5_000)
                .Select(static index => CreateMutation(index))
                .ToArray();
            BatchMutationResult written = await first.Database.MutateAsync(
                "automatic",
                mutations,
                atomic: true,
                cancellationToken: cancellationToken);
            Assert.Equal(mutations.Length, written.Succeeded);
            auto.AllowedIndexes = [VectorIndexKind.Hnsw];

            int successfulSearches = 0;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                SearchResponse response = await first.Database.SearchAsync(
                    "automatic",
                    Query(mutations[attempt].Document!.Vector),
                    cancellationToken);
                successfulSearches += response.Hits.Count > 0 ? 1 : 0;
                IndexMigrationStatus status = await first.Database.GetIndexStatusAsync("automatic", cancellationToken);
                if (status.State == "active")
                {
                    break;
                }

                await Task.Delay(10, cancellationToken);
            }

            IndexMigrationStatus active = await WaitForStateAsync(
                first.Database,
                "automatic",
                "active",
                cancellationToken);
            Assert.Equal(VectorIndexKind.Hnsw, active.ActiveKind);
            Assert.Equal(2, active.ActiveGeneration);
            Assert.Equal(1, active.PreviousGeneration);
            Assert.True(successfulSearches > 0);

            DocumentMutation recent = CreateMutation(10_000, 100F);
            Assert.Equal(1, (await first.Database.MutateAsync(
                "automatic",
                [recent],
                atomic: true,
                cancellationToken: cancellationToken)).Succeeded);

            auto.AllowedIndexes = [VectorIndexKind.IvfFlat];
            _ = await first.Database.SearchAsync(
                "automatic",
                Query(recent.Document!.Vector),
                cancellationToken);
            IndexMigrationStatus ivf = await WaitForStateAsync(
                first.Database,
                "automatic",
                "active",
                cancellationToken,
                VectorIndexKind.IvfFlat);
            Assert.Equal(3, ivf.ActiveGeneration);
            Assert.Equal(2, ivf.PreviousGeneration);

            DocumentMutation afterIvf = CreateMutation(20_000, 200F);
            Assert.Equal(1, (await first.Database.MutateAsync(
                "automatic",
                [afterIvf],
                atomic: true,
                cancellationToken: cancellationToken)).Succeeded);
        }

        await using AutoDatabaseFixture restarted = await AutoDatabaseFixture.CreateAsync(
            directory.Path,
            auto,
            diskAnn,
            cancellationToken);
        IndexMigrationStatus restored = await restarted.Database.GetIndexStatusAsync("automatic", cancellationToken);
        Assert.Equal(VectorIndexKind.IvfFlat, restored.ActiveKind);
        Assert.Equal(2, restored.PreviousGeneration);
        Assert.True(await restarted.Database.RollbackIndexAsync("automatic", cancellationToken));
        IndexMigrationStatus rolledBack = await restarted.Database.GetIndexStatusAsync("automatic", cancellationToken);
        Assert.Equal(VectorIndexKind.Hnsw, rolledBack.ActiveKind);
        Assert.Equal(2, rolledBack.ActiveGeneration);

        SearchResponse recentResult = await restarted.Database.SearchAsync(
            "automatic",
            Query(Enumerable.Repeat(200F, 16).ToArray(), marker: 20_000),
            cancellationToken);
        Assert.Equal("doc-20000", Assert.Single(recentResult.Hits).Id);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedCandidateBuildKeepsTheActiveGeneration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        string invalidDiskPath = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(invalidDiskPath, "file blocks directory creation");
        AutoIndexOptions auto = MigrationOptions();
        auto.AllowedIndexes = [VectorIndexKind.Flat];
        auto.DiskAnnMemoryRatio = 0;
        await using AutoDatabaseFixture fixture = await AutoDatabaseFixture.CreateAsync(
            directory.Path,
            auto,
            new DiskAnnOptions { Path = invalidDiskPath },
            cancellationToken);
        await fixture.Database.CreateCollectionAsync(
            "failed-build",
            16,
            DistanceMetric.Euclidean,
            AutoConfiguration(),
            cancellationToken);
        await fixture.Database.MutateAsync(
            "failed-build",
            Enumerable.Range(0, 50).Select(static index => CreateMutation(index)).ToArray(),
            atomic: true,
            cancellationToken: cancellationToken);

        auto.AllowedIndexes = [VectorIndexKind.DiskAnn];
        _ = await fixture.Database.SearchAsync(
            "failed-build",
            Query(CreateMutation(0).Document!.Vector),
            cancellationToken);
        IndexMigrationStatus failed = await WaitForStateAsync(
            fixture.Database,
            "failed-build",
            "failed",
            cancellationToken);

        Assert.Equal(VectorIndexKind.Flat, failed.ActiveKind);
        Assert.Equal(1, failed.ActiveGeneration);
        Assert.Null(failed.PreviousGeneration);
        Assert.False(await fixture.Database.RollbackIndexAsync("failed-build", cancellationToken));
        Assert.NotEmpty((await fixture.Database.SearchAsync(
            "failed-build",
            Query(CreateMutation(0).Document!.Vector),
            cancellationToken)).Hits);
    }

    private static AutoIndexOptions MigrationOptions() => new()
    {
        Enabled = true,
        AllowedIndexes = [VectorIndexKind.Flat, VectorIndexKind.Hnsw],
        AssessmentInterval = TimeSpan.Zero,
        MinimumMigrationInterval = TimeSpan.Zero,
        MinimumPerformanceGain = 0,
        MinimumRecall = 0,
        HysteresisRatio = 0,
        HnswMinimumVectors = 10,
        IvfMinimumVectors = int.MaxValue,
        PqMinimumVectors = int.MaxValue,
        ValidationSampleSize = 8,
    };

    private static VectorIndexConfiguration AutoConfiguration() => new()
    {
        Kind = VectorIndexKind.Auto,
        HnswM = 8,
        HnswEfConstruction = 64,
        HnswEfSearch = 512,
        IvfListCount = 64,
        IvfProbeCount = 1,
        IvfTrainingIterations = 4,
        PqSubvectorCount = 8,
    };

    private static DocumentMutation CreateMutation(int index) => CreateMutation(index, index / 1_000F);

    private static DocumentMutation CreateMutation(int index, float value) => new()
    {
        Kind = DocumentMutationKind.Upsert,
        Id = "doc-" + index,
        Document = new DocumentRecord
        {
            Id = "doc-" + index,
            Text = "automatic index document",
            Vector = Enumerable.Range(0, 16).Select(dimension => value + dimension / 100F).ToArray(),
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["marker"] = MetadataValue.From((long)index),
            },
        },
    };

    private static SearchRequest Query(float[] vector, long? marker = null) => new()
    {
        Mode = SearchMode.Vector,
        Vector = vector,
        Limit = 1,
        Filter = marker.HasValue
            ? new MetadataFilter
            {
                Operator = MetadataOperator.Equal,
                Field = "marker",
                Value = MetadataValue.From(marker.Value),
            }
            : null,
    };

    private static async Task<IndexMigrationStatus> WaitForStateAsync(
        SlimVectorDatabase database,
        string collection,
        string state,
        CancellationToken cancellationToken,
        VectorIndexKind? activeKind = null)
    {
        IndexMigrationStatus? last = null;
        for (int attempt = 0; attempt < 500; attempt++)
        {
            IndexMigrationStatus status = await database.GetIndexStatusAsync(collection, cancellationToken);
            last = status;
            if (string.Equals(status.State, state, StringComparison.Ordinal) &&
                (!activeKind.HasValue || status.ActiveKind == activeKind.Value))
            {
                return status;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(
            $"Index migration did not enter state '{state}'; last state was '{last?.State}' on '{last?.ActiveKind}' ({last?.Reason}).");
    }

    private sealed class AutoDatabaseFixture : IAsyncDisposable
    {
        private readonly IStorageEngine _storage;
        private readonly StorageRaftCommandApplier _applier;
        private readonly IConsensusCoordinator _consensus;
        private readonly IWriteScheduler _scheduler;

        private AutoDatabaseFixture(
            SlimVectorDatabase database,
            IStorageEngine storage,
            StorageRaftCommandApplier applier,
            IConsensusCoordinator consensus,
            IWriteScheduler scheduler)
        {
            Database = database;
            _storage = storage;
            _applier = applier;
            _consensus = consensus;
            _scheduler = scheduler;
        }

        public SlimVectorDatabase Database { get; }

        public static async Task<AutoDatabaseFixture> CreateAsync(
            string path,
            AutoIndexOptions auto,
            DiskAnnOptions diskAnn,
            CancellationToken cancellationToken)
        {
            IStorageEngine storage = new FileSystemStorageEngine(new StorageSettings { Path = path, FlushToDisk = false });
            StorageRaftCommandApplier applier = new(storage);
            IConsensusCoordinator consensus = new DirectConsensusCoordinator(applier);
            IWriteScheduler scheduler = new AdaptiveWriteScheduler(
                consensus,
                Options.Create(new AdaptiveBatchingOptions()),
                Options.Create(new BackpressureOptions()),
                TimeProvider.System);
            SlimVectorDatabase database = new(
                storage,
                consensus,
                scheduler,
                TimeProvider.System,
                Options.Create(new CollectionsOptions { IdleTimeout = TimeSpan.Zero }),
                Options.Create(new VectorIndexOptions()),
                autoIndexOptions: Options.Create(auto),
                diskAnnOptions: Options.Create(diskAnn));
            await database.InitializeAsync(cancellationToken);
            return new AutoDatabaseFixture(database, storage, applier, consensus, scheduler);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.EvictInactiveCollectionsAsync();
            await _scheduler.DisposeAsync();
            await _consensus.DisposeAsync();
            _applier.Dispose();
            _storage.Dispose();
        }
    }
}
