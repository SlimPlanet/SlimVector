using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;

namespace SlimVector.Studio.Tests;

public sealed class StudioIntegrationTests
{
    [Fact]
    public async Task StudioServesInterfaceAndStartsDatabaseWithDefaultCollection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = TestRoot();
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();

            string html = await client.GetStringAsync("/", cancellationToken);
            using JsonDocument bootstrap = await ReadJsonAsync(client, "/studio/api/bootstrap", cancellationToken);

            Assert.Contains("SlimVector Studio", html, StringComparison.Ordinal);
            Assert.Contains("Extraire, découper, vectoriser", html, StringComparison.Ordinal);
            JsonElement collection = Assert.Single(bootstrap.RootElement.GetProperty("collections").EnumerateArray());
            Assert.Equal("documents", collection.GetProperty("definition").GetProperty("name").GetString());
            Assert.Equal(384, collection.GetProperty("definition").GetProperty("dimension").GetInt32());
            Assert.True(bootstrap.RootElement.GetProperty("model").GetProperty("isReady").GetBoolean());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StudioIngestsAndRunsEverySearchMode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = TestRoot();
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();
            using MultipartFormDataContent content = new();
            using ByteArrayContent file = new(Encoding.UTF8.GetBytes(
                "SlimVector persists immutable segments. Hybrid search fuses BM25 ranks with vector similarity."));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");
            content.Add(file, "files", "architecture.md");
            content.Add(new StringContent("documents"), "collection");
            content.Add(new StringContent("recursive"), "strategy");
            content.Add(new StringContent("24"), "targetTokens");
            content.Add(new StringContent("32"), "maximumTokens");
            content.Add(new StringContent("4"), "overlapTokens");
            content.Add(new StringContent("true"), "replaceExisting");
            content.Add(new StringContent("true"), "atomic");
            content.Add(new StringContent("false"), "previewOnly");
            content.Add(new StringContent("{\"suite\":\"integration\"}"), "metadata");

            using HttpResponseMessage ingestion = await client.PostAsync("/studio/api/ingest", content, cancellationToken);
            ingestion.EnsureSuccessStatusCode();
            using JsonDocument ingestionJson = await JsonDocument.ParseAsync(
                await ingestion.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            Assert.True(ingestionJson.RootElement[0].GetProperty("storedCount").GetInt32() > 0);

            foreach (string mode in new[] { "vector", "text", "hybrid", "metadata" })
            {
                string query = mode == "metadata"
                    ? "{\"query\":\"\",\"mode\":\"metadata\",\"limit\":5,\"filter\":{\"operator\":\"equal\",\"field\":\"suite\",\"value\":\"integration\"}}"
                    : $"{{\"query\":\"hybrid BM25 vector\",\"mode\":\"{mode}\",\"limit\":5}}";
                using StringContent queryContent = new(query, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync(
                    "/studio/api/collections/documents/search",
                    queryContent,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                using JsonDocument json = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                Assert.NotEmpty(json.RootElement.GetProperty("hits").EnumerateArray());
            }

            using JsonDocument documents = await ReadJsonAsync(
                client,
                "/studio/api/collections/documents/documents?offset=0&limit=25",
                cancellationToken);
            Assert.True(documents.RootElement.GetProperty("total").GetInt64() > 0);
            Assert.Equal(384, documents.RootElement.GetProperty("documents")[0].GetProperty("vectorDimension").GetInt32());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StudioSupportsCollectionMutationRuntimeAndBackups()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = TestRoot();
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();
            using StringContent createContent = new(
                "{\"name\":\"manual\",\"dimension\":384,\"metric\":\"cosine\",\"indexKind\":\"flat\"}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage create = await client.PostAsync(
                "/studio/api/collections",
                createContent,
                cancellationToken);
            create.EnsureSuccessStatusCode();

            using StringContent mutateContent = new(
                "{\"kind\":\"upsert\",\"atomic\":true,\"documents\":[{\"id\":\"manual:1\",\"text\":\"A manually vectorized note\",\"autoVectorize\":true,\"metadata\":{\"source\":\"studio\"}}]}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage mutate = await client.PostAsync(
                "/studio/api/collections/manual/documents/mutate",
                mutateContent,
                cancellationToken);
            mutate.EnsureSuccessStatusCode();

            using JsonDocument runtime = await ReadJsonAsync(client, "/studio/api/runtime", cancellationToken);
            Assert.True(runtime.RootElement.GetProperty("ready").GetBoolean());
            Assert.True(runtime.RootElement.GetProperty("writes").GetProperty("totalWrites").GetInt64() > 0);

            using HttpResponseMessage createBackup = await client.PostAsync("/studio/api/backups", null, cancellationToken);
            createBackup.EnsureSuccessStatusCode();
            using JsonDocument backup = await JsonDocument.ParseAsync(
                await createBackup.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            string backupId = backup.RootElement.GetProperty("backupId").GetString()!;
            using HttpResponseMessage verify = await client.PostAsync(
                $"/studio/api/backups/{Uri.EscapeDataString(backupId)}/verify",
                null,
                cancellationToken);
            verify.EnsureSuccessStatusCode();

            using HttpResponseMessage delete = await client.DeleteAsync("/studio/api/collections/manual", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
            using StringContent restoreContent = new(
                "{\"collectionName\":\"manual\",\"restoredName\":\"manual-restored\",\"overwrite\":false}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage restore = await client.PostAsync(
                $"/studio/api/backups/{Uri.EscapeDataString(backupId)}/restore-collection",
                restoreContent,
                cancellationToken);
            restore.EnsureSuccessStatusCode();
            using JsonDocument restored = await ReadJsonAsync(
                client,
                "/studio/api/collections/manual-restored/documents?offset=0&limit=10",
                cancellationToken);
            Assert.Equal(1, restored.RootElement.GetProperty("total").GetInt64());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:Path"] = Path.Combine(root, "data"),
                    ["Storage:FlushToDisk"] = "false",
                    ["Backup:Path"] = Path.Combine(root, "backups"),
                    ["Studio:ModelDirectory"] = Path.Combine(root, "model"),
                    ["Studio:AutoDownloadModel"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmbeddingGenerator>();
                services.AddSingleton<IEmbeddingGenerator, FakeEmbeddingGenerator>();
            });
        });

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private static string TestRoot() => Path.Combine(Path.GetTempPath(), "SlimVector.Studio.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public string ModelId => "test/fake-multilingual";

        public int Dimension => 384;

        public ValueTask<EmbeddingModelStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EmbeddingModelStatus
            {
                ModelId = ModelId,
                Revision = "test",
                Variant = "fake.onnx",
                Directory = Path.GetTempPath(),
                Dimension = Dimension,
                MaximumSequenceLength = 128,
                IsReady = true,
            });
        }

        public ValueTask EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<float[]>> GenerateAsync(
            IReadOnlyList<string> texts,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]> vectors = texts.Select(CreateVector).ToArray();
            progress?.Report(100);
            return ValueTask.FromResult(vectors);
        }

        private static float[] CreateVector(string text)
        {
            float[] vector = new float[384];
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
            for (int index = 0; index < vector.Length; index++)
            {
                vector[index] = (hash[index % hash.Length] - 127.5f) / 127.5f;
            }

            return vector;
        }
    }
}
