using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class SlimVectorDatabaseTests
{
    [Fact]
    public async Task CrudAndHybridSearchWorkAcrossRestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using (DatabaseFixture first = await DatabaseFixture.CreateAsync(directory.Path, cancellationToken))
        {
            await first.Database.CreateCollectionAsync("articles", 3, DistanceMetric.Cosine, cancellationToken: cancellationToken);
            BatchMutationResult result = await first.Database.MutateAsync(
                "articles",
                [
                    Add("one", "distributed vector database", [1, 0, 0], 2024),
                    Add("two", "relational database", [0, 1, 0], 2020),
                ],
                atomic: true,
                cancellationToken: cancellationToken);
            Assert.Equal(2, result.Succeeded);
        }

        await using DatabaseFixture restarted = await DatabaseFixture.CreateAsync(directory.Path, cancellationToken);
        SearchResponse response = await restarted.Database.SearchAsync("articles", new SearchRequest
        {
            Text = "vector database",
            Vector = [1, 0, 0],
            Mode = SearchMode.Hybrid,
            Limit = 2,
            Filter = new MetadataFilter
            {
                Operator = MetadataOperator.GreaterThan,
                Field = "year",
                Value = MetadataValue.From(2021L),
            },
        }, cancellationToken);

        Assert.Single(response.Hits);
        Assert.Equal("one", response.Hits[0].Id);
        Assert.Equal(2, await restarted.Database.CountDocumentsAsync("articles", cancellationToken));
    }

    [Fact]
    public async Task AtomicBatchDoesNotPersistPartialSuccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync(directory.Path, cancellationToken);
        await fixture.Database.CreateCollectionAsync("atomic", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);

        BatchMutationResult result = await fixture.Database.MutateAsync(
            "atomic",
            [
                Add("valid", "valid", [1, 0], 1),
                Add("invalid", "invalid", [1, 0, 0], 1),
            ],
            atomic: true,
            cancellationToken: cancellationToken);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(0, await fixture.Database.CountDocumentsAsync("atomic", cancellationToken));
    }

    [Fact]
    public async Task PersistentHnswIsLoadedAfterRestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using (DatabaseFixture first = await DatabaseFixture.CreateAsync(directory.Path, cancellationToken))
        {
            await first.Database.CreateCollectionAsync(
                "approximate",
                2,
                DistanceMetric.Euclidean,
                new VectorIndexConfiguration { Kind = VectorIndexKind.Hnsw, HnswM = 8, HnswEfConstruction = 64, HnswEfSearch = 32 },
                cancellationToken);
            await first.Database.MutateAsync(
                "approximate",
                [Add("near", "near", [1, 1], 1), Add("far", "far", [10, 10], 1)],
                atomic: true,
                cancellationToken: cancellationToken);
        }

        Assert.Single(Directory.EnumerateFiles(directory.Path, "search-index-v1.bin", SearchOption.AllDirectories));
        await using DatabaseFixture restarted = await DatabaseFixture.CreateAsync(directory.Path, cancellationToken);
        SearchResponse response = await restarted.Database.SearchAsync(
            "approximate",
            new SearchRequest { Mode = SearchMode.Vector, Vector = [1.1f, 1.1f], Limit = 1 },
            cancellationToken);

        Assert.Equal("near", Assert.Single(response.Hits).Id);
        SearchResponse textAndMetadata = await restarted.Database.SearchAsync(
            "approximate",
            new SearchRequest
            {
                Mode = SearchMode.Text,
                Text = "near",
                Limit = 2,
                Filter = new MetadataFilter
                {
                    Operator = MetadataOperator.Equal,
                    Field = "year",
                    Value = MetadataValue.From(1L),
                },
            },
            cancellationToken);
        Assert.Equal("near", Assert.Single(textAndMetadata.Hits).Id);
    }

    [Fact]
    public async Task ConfiguredTextAndFilterLimitsAreEnforcedBeforeStorage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync(
            directory.Path,
            cancellationToken,
            new TextIndexOptions { MaximumTermsPerDocument = 2 },
            new MetadataIndexOptions { MaximumFilterDepth = 2 });
        await fixture.Database.CreateCollectionAsync("limits", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);

        BatchMutationResult rejected = await fixture.Database.MutateAsync(
            "limits",
            [Add("large", "one two three", [1, 0], 1)],
            atomic: true,
            cancellationToken: cancellationToken);

        Assert.Equal(0, rejected.Succeeded);
        Assert.Equal(ErrorCodes.TextTooLarge, Assert.Single(rejected.Results).ErrorCode);
        Assert.Equal(0, await fixture.Database.CountDocumentsAsync("limits", cancellationToken));
        MetadataFilter tooDeep = new()
        {
            Operator = MetadataOperator.Not,
            Operands =
            [
                new MetadataFilter
                {
                    Operator = MetadataOperator.Not,
                    Operands =
                    [
                        new MetadataFilter
                        {
                            Operator = MetadataOperator.Equal,
                            Field = "year",
                            Value = MetadataValue.From(1L),
                        },
                    ],
                },
            ],
        };
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => fixture.Database.SearchAsync(
            "limits",
            new SearchRequest { Mode = SearchMode.Metadata, Limit = 10, Filter = tooDeep },
            cancellationToken).AsTask());
        Assert.Equal(ErrorCodes.InvalidFilter, exception.Code);
    }

    [Fact]
    public async Task MetadataIndexDefaultCanDisablePostingsWithoutDisablingFilters()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using DatabaseFixture fixture = await DatabaseFixture.CreateAsync(
            directory.Path,
            cancellationToken,
            metadataIndexOptions: new MetadataIndexOptions { IndexByDefault = false });
        CollectionDefinition definition = await fixture.Database.CreateCollectionAsync(
            "scan-filter",
            2,
            DistanceMetric.Cosine,
            cancellationToken: cancellationToken);
        Assert.False(definition.MetadataIndexed);
        await fixture.Database.MutateAsync(
            "scan-filter",
            [Add("one", "scan", [1, 0], 2026), Add("two", "scan", [0, 1], 2024)],
            atomic: true,
            cancellationToken: cancellationToken);

        SearchResponse response = await fixture.Database.SearchAsync(
            "scan-filter",
            new SearchRequest
            {
                Mode = SearchMode.Metadata,
                Limit = 10,
                Filter = new MetadataFilter
                {
                    Operator = MetadataOperator.GreaterThan,
                    Field = "year",
                    Value = MetadataValue.From(2025L),
                },
            },
            cancellationToken);

        Assert.Equal("one", Assert.Single(response.Hits).Id);
    }

    private static DocumentMutation Add(string id, string text, float[] vector, long year) => new()
    {
        Kind = DocumentMutationKind.Add,
        Id = id,
        Document = new DocumentRecord
        {
            Id = id,
            Text = text,
            Vector = vector,
            Metadata = new Dictionary<string, MetadataValue> { ["year"] = MetadataValue.From(year) },
        },
    };

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly IStorageEngine _storage;
        private readonly StorageRaftCommandApplier _applier;
        private readonly IConsensusCoordinator _consensus;
        private readonly IWriteScheduler _writeScheduler;

        private DatabaseFixture(
            ISlimVectorDatabase database,
            IStorageEngine storage,
            StorageRaftCommandApplier applier,
            IConsensusCoordinator consensus,
            IWriteScheduler writeScheduler)
        {
            Database = database;
            _storage = storage;
            _applier = applier;
            _consensus = consensus;
            _writeScheduler = writeScheduler;
        }

        public ISlimVectorDatabase Database { get; }

        public static async Task<DatabaseFixture> CreateAsync(
            string path,
            CancellationToken cancellationToken,
            TextIndexOptions? textIndexOptions = null,
            MetadataIndexOptions? metadataIndexOptions = null)
        {
            IStorageEngine storage = new FileSystemStorageEngine(new StorageSettings { Path = path, FlushToDisk = false });
            StorageRaftCommandApplier applier = new(storage);
            IConsensusCoordinator consensus = new DirectConsensusCoordinator(applier);
            IWriteScheduler writeScheduler = new AdaptiveWriteScheduler(
                consensus,
                Options.Create(new AdaptiveBatchingOptions()),
                Options.Create(new BackpressureOptions()),
                TimeProvider.System);
            SlimVectorDatabase database = new(
                storage,
                consensus,
                writeScheduler,
                TimeProvider.System,
                Options.Create(new CollectionsOptions()),
                Options.Create(new VectorIndexOptions()),
                Options.Create(textIndexOptions ?? new TextIndexOptions()),
                Options.Create(metadataIndexOptions ?? new MetadataIndexOptions()));
            await database.InitializeAsync(cancellationToken);
            return new DatabaseFixture(database, storage, applier, consensus, writeScheduler);
        }

        public async ValueTask DisposeAsync()
        {
            await _writeScheduler.DisposeAsync();
            await _consensus.DisposeAsync();
            _applier.Dispose();
            _storage.Dispose();
        }
    }
}
