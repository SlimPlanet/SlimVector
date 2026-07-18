using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using SlimVector.Application.Backups;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class BackupServiceTests
{
    private static readonly string EncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public async Task EncryptedIncrementalBackupIsDeduplicatedVerifiedAndFullyRestorable()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        await using BackupFixture fixture = await BackupFixture.CreateAsync(
            directory.Path,
            new BackupOptions
            {
                Path = Path.Combine(directory.Path, "backups"),
                Encrypt = true,
                EncryptionKey = EncryptionKey,
                RetentionCount = 3,
            },
            cancellationToken);
        await fixture.Database.CreateCollectionAsync("articles", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        await fixture.Database.MutateAsync(
            "articles",
            [Add("one", "durable vector backup")],
            atomic: true,
            cancellationToken: cancellationToken);

        BackupDescriptor first = await fixture.Backups.CreateBackupAsync(cancellationToken);
        BackupDescriptor second = await fixture.Backups.CreateBackupAsync(cancellationToken);
        Assert.Equal(first.BackupId, second.ParentBackupId);
        Assert.Equal(1, second.CollectionCount);
        Assert.Equal(1, second.DocumentCount);
        Assert.True(fixture.Backups.GetMetrics().DeduplicatedBlobs >= 1);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "backups", "blobs"), "*.blob"));
        await fixture.Backups.VerifyBackupAsync(second.BackupId, cancellationToken);

        await fixture.Database.DeleteCollectionAsync("articles", cancellationToken);
        await fixture.Database.CreateCollectionAsync("junk", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        await fixture.Backups.RestoreFullAsync(second.BackupId, cancellationToken);

        CollectionDefinition restored = Assert.Single(await fixture.Database.ListCollectionsAsync(cancellationToken));
        Assert.Equal("articles", restored.Name);
        SearchResponse response = await fixture.Database.SearchAsync("articles", new SearchRequest
        {
            Mode = SearchMode.Hybrid,
            Text = "vector backup",
            Vector = [1, 0],
            Limit = 1,
        }, cancellationToken);
        Assert.Equal("one", Assert.Single(response.Hits).Id);
        Assert.Equal(1, fixture.Backups.GetMetrics().SuccessfulRestores);
    }

    [Fact]
    public async Task CollectionCanRestoreUnderNewNameAndRetentionRemovesOldManifests()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        string backupPath = Path.Combine(directory.Path, "backups");
        await using BackupFixture fixture = await BackupFixture.CreateAsync(
            directory.Path,
            new BackupOptions { Path = backupPath, RetentionCount = 2 },
            cancellationToken);
        await fixture.Database.CreateCollectionAsync("source", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        await fixture.Database.MutateAsync(
            "source",
            [Add("one", "renamed restore")],
            atomic: true,
            cancellationToken: cancellationToken);
        BackupDescriptor first = await fixture.Backups.CreateBackupAsync(cancellationToken);
        _ = await fixture.Backups.CreateBackupAsync(cancellationToken);
        BackupDescriptor latest = await fixture.Backups.CreateBackupAsync(cancellationToken);

        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(backupPath, "manifests"), "*.manifest").Count());
        Assert.DoesNotContain(
            await fixture.Backups.ListBackupsAsync(cancellationToken),
            backup => backup.BackupId == first.BackupId);
        await fixture.Backups.RestoreCollectionAsync(
            latest.BackupId,
            "source",
            restoredName: "restored-copy",
            cancellationToken: cancellationToken);

        CollectionDefinition source = await fixture.Database.GetCollectionAsync("source", cancellationToken);
        CollectionDefinition restored = await fixture.Database.GetCollectionAsync("restored-copy", cancellationToken);
        Assert.NotEqual(source.Id, restored.Id);
        Assert.Equal(1, await fixture.Database.CountDocumentsAsync("restored-copy", cancellationToken));
    }

    [Fact]
    public async Task IntegrityVerificationDetectsBlobCorruption()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        string backupPath = Path.Combine(directory.Path, "backups");
        await using BackupFixture fixture = await BackupFixture.CreateAsync(
            directory.Path,
            new BackupOptions { Path = backupPath },
            cancellationToken);
        await fixture.Database.CreateCollectionAsync("corruption", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        BackupDescriptor backup = await fixture.Backups.CreateBackupAsync(cancellationToken);
        string blob = Assert.Single(Directory.EnumerateFiles(Path.Combine(backupPath, "blobs"), "*.blob"));
        byte[] bytes = await File.ReadAllBytesAsync(blob, cancellationToken);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(blob, bytes, cancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Backups.VerifyBackupAsync(backup.BackupId, cancellationToken).AsTask());
    }

    [Fact]
    public async Task S3BackendSignsRetriesDeduplicatesAndRestores()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        InMemoryS3Handler handler = new() { FailFirstPut = true };
        IHttpClientFactory httpClientFactory = new SingleHttpClientFactory(handler);
        await using BackupFixture fixture = await BackupFixture.CreateAsync(
            directory.Path,
            new BackupOptions
            {
                Provider = BackupProvider.S3,
                S3Endpoint = "http://s3.test",
                S3Bucket = "backups",
                S3Region = "eu-west-1",
                S3AccessKey = "access-key",
                S3SecretKey = "secret-key",
                Prefix = "slimvector-tests",
                RetentionCount = 3,
            },
            cancellationToken,
            httpClientFactory);
        await fixture.Database.CreateCollectionAsync("s3-source", 2, DistanceMetric.Cosine, cancellationToken: cancellationToken);
        await fixture.Database.MutateAsync(
            "s3-source",
            [Add("one", "s3 retry restore")],
            atomic: true,
            cancellationToken: cancellationToken);

        BackupDescriptor first = await fixture.Backups.CreateBackupAsync(cancellationToken);
        BackupDescriptor second = await fixture.Backups.CreateBackupAsync(cancellationToken);
        Assert.True(handler.PutAttempts >= 3);
        Assert.All(handler.AuthorizationHeaders, static header => Assert.StartsWith("AWS4-HMAC-SHA256 ", header));
        Assert.Contains(handler.Objects.Keys, static key => key.Contains("/blobs/", StringComparison.Ordinal));
        Assert.Contains(handler.Objects.Keys, static key => key.Contains("/manifests/", StringComparison.Ordinal));
        Assert.Equal(first.BackupId, second.ParentBackupId);
        Assert.True(fixture.Backups.GetMetrics().DeduplicatedBlobs >= 1);

        await fixture.Database.DeleteCollectionAsync("s3-source", cancellationToken);
        await fixture.Backups.RestoreFullAsync(second.BackupId, cancellationToken);
        Assert.Equal(1, await fixture.Database.CountDocumentsAsync("s3-source", cancellationToken));
    }

    private static DocumentMutation Add(string id, string text) => new()
    {
        Kind = DocumentMutationKind.Add,
        Id = id,
        Document = new DocumentRecord
        {
            Id = id,
            Text = text,
            Vector = [1, 0],
            Metadata = new Dictionary<string, MetadataValue> { ["kind"] = MetadataValue.From("backup") },
        },
    };

    private sealed class BackupFixture : IAsyncDisposable
    {
        private readonly FileSystemStorageEngine _storage;
        private readonly StorageRaftCommandApplier _applier;
        private readonly IConsensusCoordinator _consensus;
        private readonly IWriteScheduler _scheduler;

        private BackupFixture(
            SlimVectorDatabase database,
            BackupService backups,
            FileSystemStorageEngine storage,
            StorageRaftCommandApplier applier,
            IConsensusCoordinator consensus,
            IWriteScheduler scheduler)
        {
            Database = database;
            Backups = backups;
            _storage = storage;
            _applier = applier;
            _consensus = consensus;
            _scheduler = scheduler;
        }

        public SlimVectorDatabase Database { get; }

        public BackupService Backups { get; }

        public static async Task<BackupFixture> CreateAsync(
            string rootPath,
            BackupOptions backupOptions,
            CancellationToken cancellationToken,
            IHttpClientFactory? httpClientFactory = null)
        {
            FileSystemStorageEngine storage = new(new StorageSettings
            {
                Path = Path.Combine(rootPath, "data"),
                FlushToDisk = false,
            });
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
                Options.Create(new CollectionsOptions()),
                Options.Create(new VectorIndexOptions()));
            await database.InitializeAsync(cancellationToken);
            BackupService backups = new(
                Options.Create(backupOptions),
                storage,
                consensus,
                TimeProvider.System,
                httpClientFactory);
            return new BackupFixture(database, backups, storage, applier, consensus, scheduler);
        }

        public async ValueTask DisposeAsync()
        {
            Backups.Dispose();
            await _scheduler.DisposeAsync();
            await _consensus.DisposeAsync();
            _applier.Dispose();
            _storage.Dispose();
        }
    }

    private sealed class SingleHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class InMemoryS3Handler : HttpMessageHandler
    {
        private int _putAttempts;

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<string> AuthorizationHeaders { get; } = new();

        public bool FailFirstPut { get; set; }

        public int PutAttempts => Volatile.Read(ref _putAttempts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string authorization = request.Headers.Authorization?.ToString()
                ?? request.Headers.GetValues("Authorization").Single();
            AuthorizationHeaders.Enqueue(authorization);
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            string key = path.StartsWith("backups/", StringComparison.Ordinal) ? path["backups/".Length..] : string.Empty;
            if (request.Method == HttpMethod.Put)
            {
                int attempt = Interlocked.Increment(ref _putAttempts);
                if (FailFirstPut && attempt == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                if (request.Headers.IfNoneMatch.Count > 0 && Objects.ContainsKey(key))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                Objects[key] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.Query.Contains("list-type=2", StringComparison.Ordinal))
            {
                string prefix = QueryValue(request.RequestUri.Query, "prefix");
                string contents = string.Concat(Objects.Keys
                    .Where(objectKey => objectKey.StartsWith(prefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .Select(objectKey => $"<Contents><Key>{SecurityElement.Escape(objectKey)}</Key></Contents>"));
                string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult>{contents}<IsTruncated>false</IsTruncated></ListBucketResult>";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                };
            }

            if (request.Method == HttpMethod.Get)
            {
                return Objects.TryGetValue(key, out byte[]? data)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Delete)
            {
                Objects.TryRemove(key, out _);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static string QueryValue(string query, string name)
        {
            foreach (string component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = component.Split('=', 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return string.Empty;
        }
    }
}
