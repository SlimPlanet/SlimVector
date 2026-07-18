using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SlimVector.Api.Contracts;
using SlimVector.Application.Backups;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Replication;
using SlimVector.Storage;

namespace SlimVector.Api.Tests;

public sealed class SlimVectorApiTests
{
    [Fact]
    public async Task ApiSupportsCollectionDocumentsAndHybridQuery()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string dataPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(dataPath);
            using HttpClient client = factory.CreateClient();

            HttpResponseMessage create = await client.PostAsJsonAsync(
                "/api/v1/collections",
                new CreateCollectionRequest { Name = "articles", Dimension = 2, Metric = DistanceMetric.Cosine },
                ApiJsonContext.Default.CreateCollectionRequest,
                cancellationToken);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            DocumentBatchRequest documents = new()
            {
                Documents =
                [
                    new DocumentInput { Id = "one", Text = "vector search", Vector = [1, 0] },
                    new DocumentInput { Id = "two", Text = "other text", Vector = [0, 1] },
                ],
            };
            HttpResponseMessage add = await client.PostAsJsonAsync(
                "/api/v1/collections/articles/documents/add",
                documents,
                ApiJsonContext.Default.DocumentBatchRequest,
                cancellationToken);
            add.EnsureSuccessStatusCode();

            HttpResponseMessage query = await client.PostAsJsonAsync(
                "/api/v1/collections/articles/documents/query",
                new QueryRequest { Text = "vector", Vector = [1, 0], Mode = SearchMode.Hybrid, Limit = 1 },
                ApiJsonContext.Default.QueryRequest,
                cancellationToken);
            query.EnsureSuccessStatusCode();
            QueryResponse? response = await query.Content.ReadFromJsonAsync(ApiJsonContext.Default.QueryResponse, cancellationToken);

            Assert.NotNull(response);
            Assert.Equal("one", Assert.Single(response.Hits).Id);
            string metrics = await client.GetStringAsync("/metrics", cancellationToken);
            Assert.Contains("slimvector_search_requests_total 1", metrics, StringComparison.Ordinal);
            Assert.True(ReadMetric(metrics, "slimvector_index_loads_total") >= 1);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApiRejectsOversizedBodiesWithStableProblemCode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string dataPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Storage:Path"] = dataPath,
                        ["Storage:FlushToDisk"] = "false",
                        ["Api:MaximumRequestBodyBytes"] = "32",
                    })));
            using HttpClient client = factory.CreateClient();
            using StringContent content = new(new string('x', 64), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.PostAsync("/api/v1/collections", content, cancellationToken);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("request_too_large", body, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AdminBackupEndpointsRequireKeyAndRestoreData()
    {
        const string adminKey = "0123456789abcdef0123456789abcdef";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string rootPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Storage:Path"] = Path.Combine(rootPath, "data"),
                        ["Storage:FlushToDisk"] = "false",
                        ["Backup:Path"] = Path.Combine(rootPath, "backups"),
                        ["Api:AdminEndpointsEnabled"] = "true",
                        ["Api:AdminApiKey"] = adminKey,
                    })));
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage createCollection = await client.PostAsJsonAsync(
                "/api/v1/collections",
                new CreateCollectionRequest { Name = "restore-me", Dimension = 2 },
                ApiJsonContext.Default.CreateCollectionRequest,
                cancellationToken);
            createCollection.EnsureSuccessStatusCode();
            using HttpResponseMessage add = await client.PostAsJsonAsync(
                "/api/v1/collections/restore-me/documents/add",
                new DocumentBatchRequest
                {
                    Documents = [new DocumentInput { Id = "one", Text = "durable", Vector = [1, 0] }],
                },
                ApiJsonContext.Default.DocumentBatchRequest,
                cancellationToken);
            add.EnsureSuccessStatusCode();

            using HttpResponseMessage unauthorized = await client.PostAsync(
                "/api/v1/admin/backups",
                content: null,
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using HttpRequestMessage createBackupRequest = AdminRequest(HttpMethod.Post, "/api/v1/admin/backups", adminKey);
            using HttpResponseMessage createBackup = await client.SendAsync(createBackupRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, createBackup.StatusCode);
            BackupResponse? backup = await createBackup.Content.ReadFromJsonAsync(ApiJsonContext.Default.BackupResponse, cancellationToken);
            Assert.NotNull(backup);

            using HttpRequestMessage delete = AdminRequest(HttpMethod.Delete, "/api/v1/collections/restore-me", adminKey);
            using HttpResponseMessage deleted = await client.SendAsync(delete, cancellationToken);
            deleted.EnsureSuccessStatusCode();
            using HttpRequestMessage restore = AdminRequest(
                HttpMethod.Post,
                $"/api/v1/admin/backups/{backup.BackupId}/restore",
                adminKey);
            using HttpResponseMessage restored = await client.SendAsync(restore, cancellationToken);
            restored.EnsureSuccessStatusCode();

            using HttpResponseMessage count = await client.GetAsync(
                "/api/v1/collections/restore-me/documents/count",
                cancellationToken);
            count.EnsureSuccessStatusCode();
            CountResponse? countResponse = await count.Content.ReadFromJsonAsync(ApiJsonContext.Default.CountResponse, cancellationToken);
            Assert.Equal(1, countResponse?.Count);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ServerTimeoutReturnsStableProblemDetails()
    {
        const string adminKey = "0123456789abcdef0123456789abcdef";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string rootPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Storage:Path"] = Path.Combine(rootPath, "data"),
                        ["Storage:FlushToDisk"] = "false",
                        ["Api:RequestTimeout"] = "00:00:00.050",
                        ["Api:AdminEndpointsEnabled"] = "true",
                        ["Api:AdminApiKey"] = adminKey,
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IBackupService>();
                    services.AddSingleton<IBackupService, SlowBackupService>();
                });
            });
            using HttpClient client = factory.CreateClient();
            using HttpRequestMessage request = AdminRequest(HttpMethod.Post, "/api/v1/admin/backups", adminKey);

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("request_timeout", body, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApiUsesStableProblemCode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string dataPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(dataPath);
            using HttpClient client = factory.CreateClient();

            HttpResponseMessage response = await client.GetAsync("/api/v1/collections/missing", cancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("collection_not_found", body, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TypedClientExecutesCrudAndQuery()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string dataPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(dataPath);
            using HttpClient httpClient = factory.CreateClient();
            SlimVector.Client.SlimVectorClient client = new(httpClient);
            await client.CreateCollectionAsync(
                new SlimVector.Client.CreateCollectionRequest { Name = "typed", Dimension = 2 },
                cancellationToken);
            await client.AddDocumentsAsync(
                "typed",
                [new SlimVector.Client.SlimVectorDocument { Id = "one", Text = "typed client", Vector = [1, 0] }],
                cancellationToken: cancellationToken);

            SlimVector.Client.SlimVectorQueryResult result = await client.QueryAsync(
                "typed",
                new SlimVector.Client.SlimVectorQuery
                {
                    Text = "typed",
                    Vector = [1, 0],
                    Mode = SearchMode.Hybrid,
                    Limit = 1,
                },
                cancellationToken);

            Assert.Equal("one", Assert.Single(result.Hits).Id);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SignedGeoReplicationEndpointAppliesAndDeduplicatesEvent()
    {
        const string sharedSecret = "0123456789abcdef0123456789abcdef";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string rootPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Storage:Path"] = Path.Combine(rootPath, "data"),
                        ["Storage:FlushToDisk"] = "false",
                        ["GeoReplication:AcceptIncoming"] = "true",
                        ["GeoReplication:OriginId"] = "secondary",
                        ["GeoReplication:SharedSecret"] = sharedSecret,
                        ["GeoReplication:StatePath"] = Path.Combine(rootPath, "geo"),
                    })));
            using HttpClient client = factory.CreateClient();
            GeoReplicationEvent replicationEvent = GeoReplicationEventFactory.CollectionUpsert(
                CollectionDefinition.Create("geo-http", 2, DistanceMetric.Cosine));
            replicationEvent.EventId = Guid.NewGuid();
            replicationEvent.Sequence = 1;
            replicationEvent.OriginId = "primary";
            replicationEvent.CreatedAt = DateTimeOffset.UtcNow;
            byte[] payload = MemoryPackSerializer.Serialize(replicationEvent);

            using HttpRequestMessage request = GeoRequest(payload, sharedSecret);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            using HttpRequestMessage duplicateRequest = GeoRequest(payload, sharedSecret);
            using HttpResponseMessage duplicate = await client.SendAsync(duplicateRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            using HttpResponseMessage collection = await client.GetAsync("/api/v1/collections/geo-http", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, collection.StatusCode);

            using HttpRequestMessage invalidRequest = GeoRequest(payload, new string('x', 32));
            using HttpResponseMessage invalid = await client.SendAsync(invalidRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClusterFollowerRedirectsWriteToMappedLeaderApi()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string dataPath = Path.Combine(Path.GetTempPath(), "SlimVector.Api.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Storage:Path"] = dataPath,
                        ["Storage:FlushToDisk"] = "false",
                        ["Raft:Mode"] = "Cluster",
                        ["Raft:NodeId"] = "node-1",
                        ["Raft:PublicEndpoint"] = "http://127.0.0.1:3262",
                        ["Raft:PublicApiEndpoint"] = "http://127.0.0.1:9001",
                        ["Raft:Members:0"] = "http://127.0.0.1:3262",
                        ["Raft:Members:1"] = "http://127.0.0.2:3262",
                        ["Raft:Members:2"] = "http://127.0.0.3:3262",
                        ["Raft:MemberApiEndpoints:0"] = "http://127.0.0.1:9001",
                        ["Raft:MemberApiEndpoints:1"] = "http://127.0.0.2:9002",
                        ["Raft:MemberApiEndpoints:2"] = "http://127.0.0.3:9003",
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConsensusCoordinator>();
                    services.AddSingleton<IConsensusCoordinator, RedirectingConsensus>();
                });
            });
            using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/collections",
                new CreateCollectionRequest { Name = "redirect", Dimension = 2 },
                ApiJsonContext.Default.CreateCollectionRequest,
                cancellationToken);

            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal("http://127.0.0.2:9002/api/v1/collections", response.Headers.Location?.AbsoluteUri);
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string dataPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Path"] = dataPath,
                ["Storage:FlushToDisk"] = "false",
            })));

    private static HttpRequestMessage GeoRequest(byte[] payload, string sharedSecret)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/internal/geo/replicate")
        {
            Content = new ByteArrayContent(payload),
        };
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), payload);
        request.Headers.TryAddWithoutValidation("X-SlimVector-Signature", Convert.ToHexStringLower(signature));
        return request;
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string path, string adminKey)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.TryAddWithoutValidation("X-SlimVector-Admin-Key", adminKey);
        return request;
    }

    private static long ReadMetric(string metrics, string name)
    {
        string line = metrics.Split('\n').Single(candidate => candidate.StartsWith(name + " ", StringComparison.Ordinal));
        return long.Parse(line.AsSpan(name.Length + 1), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class RedirectingConsensus : IConsensusCoordinator
    {
        public event Action<Guid?>? StateChanged
        {
            add { }
            remove { }
        }

        public ExecutionMode Mode => ExecutionMode.Cluster;

        public bool IsReady => true;

        public IReadOnlyList<RaftGroupStatus> GetStatuses() => [];

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public string GetDataGroupId(Guid collectionId) => "data-0";

        public ValueTask UpsertCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) => ValueTask.FromException(Unavailable());

        public ValueTask DeleteCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) => ValueTask.FromException(Unavailable());

        public ValueTask AppendAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            CancellationToken cancellationToken = default) => ValueTask.FromException(Unavailable());

        public ValueTask AppendBatchAsync(
            IReadOnlyList<CollectionWrite> writes,
            CancellationToken cancellationToken = default) => ValueTask.FromException(Unavailable());

        public ValueTask ApplyReadBarrierAsync(
            Guid? collectionId,
            ReadConsistency consistency,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ConsensusUnavailableException Unavailable() => new(
            "catalog",
            ConsensusFailureKind.NotLeader,
            "127.0.0.2:3262",
            new InvalidOperationException("not leader"));
    }

    private sealed class SlowBackupService : IBackupService
    {
        public async ValueTask<BackupDescriptor> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            throw new InvalidOperationException("The timeout middleware should cancel this operation.");
        }

        public ValueTask<IReadOnlyList<BackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<BackupDescriptor>>([]);

        public ValueTask VerifyBackupAsync(string backupId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RestoreFullAsync(string backupId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask RestoreCollectionAsync(
            string backupId,
            string collectionName,
            string? restoredName = null,
            bool overwrite = false,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public BackupMetricsSnapshot GetMetrics() => new()
        {
            SuccessfulBackups = 0,
            FailedBackups = 0,
            SuccessfulRestores = 0,
            FailedRestores = 0,
            DeduplicatedBlobs = 0,
        };
    }
}
