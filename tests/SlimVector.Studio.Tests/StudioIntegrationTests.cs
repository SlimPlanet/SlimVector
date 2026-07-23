using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using MessagePack;
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
            string javascript = await client.GetStringAsync("/app.js", cancellationToken);
            string messagePack = await client.GetStringAsync("/msgpack.js", cancellationToken);
            using JsonDocument bootstrap = await ReadJsonAsync(client, "/studio/api/bootstrap", cancellationToken);

            Assert.Contains("SlimVector Studio", html, StringComparison.Ordinal);
            Assert.Contains("<html lang=\"fr\"", html, StringComparison.Ordinal);
            Assert.Contains("Extraire, découper, vectoriser", html, StringComparison.Ordinal);
            Assert.Contains("Laboratoire de requêtes", html, StringComparison.Ordinal);
            Assert.Contains("Fragments indexés", html, StringComparison.Ordinal);
            Assert.Contains("Régulation de charge", html, StringComparison.Ordinal);
            Assert.Contains("name=\"targetTokens\" value=\"500\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"maximumTokens\" value=\"600\"", html, StringComparison.Ordinal);
            Assert.Contains("porté à 1 200", html, StringComparison.Ordinal);
            Assert.Contains("name=\"wireFormat\" value=\"messagepack\"", html, StringComparison.Ordinal);
            Assert.Contains("MessagePack réduit la taille", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Query lab", html, StringComparison.Ordinal);
            Assert.Contains("fragments produits", javascript, StringComparison.Ordinal);
            Assert.Contains("Le mode nœud unique", javascript, StringComparison.Ordinal);
            Assert.Contains("application/vnd.msgpack", javascript, StringComparison.Ordinal);
            Assert.Contains("SlimVectorMessagePack.encode", javascript, StringComparison.Ordinal);
            Assert.Contains("initializeSlimVectorMessagePack", messagePack, StringComparison.Ordinal);
            Assert.Contains("Object.freeze({ encode, decode })", messagePack, StringComparison.Ordinal);
            Assert.DoesNotContain("chunks produits", javascript, StringComparison.Ordinal);
            JsonElement collection = Assert.Single(bootstrap.RootElement.GetProperty("collections").EnumerateArray());
            Assert.Equal("documents", collection.GetProperty("definition").GetProperty("name").GetString());
            Assert.Equal(384, collection.GetProperty("definition").GetProperty("dimension").GetInt32());
            Assert.False(collection.GetProperty("definition").TryGetProperty("placement", out _));
            Assert.True(bootstrap.RootElement.GetProperty("model").GetProperty("isReady").GetBoolean());
            JsonElement chunking = bootstrap.RootElement.GetProperty("chunking");
            Assert.Equal(500, chunking.GetProperty("targetTokens").GetInt32());
            Assert.Equal(600, chunking.GetProperty("maximumTokens").GetInt32());
            Assert.Equal(100, chunking.GetProperty("overlapTokens").GetInt32());
            Assert.Equal(1200, chunking.GetProperty("maximumAllowedTokens").GetInt32());
            using JsonDocument runtime = await ReadJsonAsync(client, "/studio/api/runtime", cancellationToken);
            Assert.Equal(0, runtime.RootElement.GetProperty("openCollections").GetInt32());
            Assert.Equal(0, runtime.RootElement.GetProperty("operations").GetProperty("indexLoads").GetInt64());

            using HttpResponseMessage invalidIngestion = await client.PostAsync(
                "/studio/api/ingest",
                null,
                cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalidIngestion.StatusCode);
            using JsonDocument problem = await JsonDocument.ParseAsync(
                await invalidIngestion.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            Assert.Equal("Requête invalide", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal(
                "Un contenu multipart/form-data est requis.",
                problem.RootElement.GetProperty("detail").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StudioSearchNegotiatesMessagePackRequestsResponsesAndProblems()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = TestRoot();
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();
            using StringContent mutation = new(
                """
                {
                  "kind": "upsert",
                  "atomic": true,
                  "documents": [
                    {
                      "id": "transport:1",
                      "text": "MessagePack binary transport for vector search",
                      "autoVectorize": true,
                      "metadata": { "transport": "messagepack" }
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage mutated = await client.PostAsync(
                "/studio/api/collections/documents/documents/mutate",
                mutation,
                cancellationToken);
            mutated.EnsureSuccessStatusCode();

            using ByteArrayContent query = new(CreateMessagePackSearchRequest());
            query.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.msgpack");
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "/studio/api/collections/documents/search")
            {
                Content = query,
            };
            request.Headers.Accept.ParseAdd("application/vnd.msgpack");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/vnd.msgpack", response.Content.Headers.ContentType?.MediaType);
            using JsonDocument result = MessagePackAsJson(
                await response.Content.ReadAsByteArrayAsync(cancellationToken));
            Assert.True(result.RootElement.GetProperty("queryWasVectorized").GetBoolean());
            Assert.Equal("transport:1", Assert.Single(
                result.RootElement.GetProperty("hits").EnumerateArray()).GetProperty("id").GetString());

            using ByteArrayContent invalidContent = new([0xc1]);
            invalidContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.msgpack");
            using HttpRequestMessage invalidRequest = new(
                HttpMethod.Post,
                "/studio/api/collections/documents/search")
            {
                Content = invalidContent,
            };
            invalidRequest.Headers.Accept.ParseAdd("application/vnd.msgpack");
            using HttpResponseMessage invalid = await client.SendAsync(invalidRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("application/vnd.msgpack", invalid.Content.Headers.ContentType?.MediaType);
            using JsonDocument problem = MessagePackAsJson(
                await invalid.Content.ReadAsByteArrayAsync(cancellationToken));
            Assert.Equal("Requête invalide", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal("invalid_request", problem.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StudioUsesLargeChunkDefaultsAndEnforcesMaximum()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = TestRoot();
        try
        {
            await using WebApplicationFactory<Program> factory = CreateFactory(root);
            using HttpClient client = factory.CreateClient();
            string text = string.Join(' ', Enumerable.Repeat("concept", 700));
            using MultipartFormDataContent content = new();
            using ByteArrayContent file = new(Encoding.UTF8.GetBytes(text));
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            content.Add(file, "files", "large.txt");
            content.Add(new StringContent("documents"), "collection");
            content.Add(new StringContent("true"), "previewOnly");

            using HttpResponseMessage ingestion = await client.PostAsync(
                "/studio/api/ingest",
                content,
                cancellationToken);
            ingestion.EnsureSuccessStatusCode();
            using JsonDocument result = await JsonDocument.ParseAsync(
                await ingestion.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            JsonElement chunks = result.RootElement[0].GetProperty("chunks");
            Assert.InRange(chunks.GetArrayLength(), 2, 5);
            Assert.All(
                chunks.EnumerateArray(),
                static chunk => Assert.InRange(chunk.GetProperty("estimatedTokens").GetInt32(), 1, 600));
            Assert.Contains(
                chunks.EnumerateArray(),
                static chunk => chunk.GetProperty("estimatedTokens").GetInt32() >= 400);

            using MultipartFormDataContent invalidContent = new();
            using ByteArrayContent invalidFile = new(Encoding.UTF8.GetBytes("contenu"));
            invalidFile.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            invalidContent.Add(invalidFile, "files", "invalid.txt");
            invalidContent.Add(new StringContent("documents"), "collection");
            invalidContent.Add(new StringContent("500"), "targetTokens");
            invalidContent.Add(new StringContent("1201"), "maximumTokens");
            invalidContent.Add(new StringContent("100"), "overlapTokens");
            invalidContent.Add(new StringContent("true"), "previewOnly");

            using HttpResponseMessage invalid = await client.PostAsync(
                "/studio/api/ingest",
                invalidContent,
                cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using JsonDocument problem = await JsonDocument.ParseAsync(
                await invalid.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            Assert.Equal("Argument invalide", problem.RootElement.GetProperty("title").GetString());
            Assert.Contains(
                "ne peut pas dépasser 1200",
                problem.RootElement.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
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
            using (JsonDocument runtime = await ReadJsonAsync(client, "/studio/api/runtime", cancellationToken))
            {
                Assert.Equal(1, runtime.RootElement.GetProperty("openCollections").GetInt32());
                Assert.Equal(1, runtime.RootElement.GetProperty("operations").GetProperty("indexLoads").GetInt64());
            }

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

    private static byte[] CreateMessagePackSearchRequest()
    {
        ArrayBufferWriter<byte> buffer = new();
        MessagePackWriter writer = new(buffer);
        writer.WriteMapHeader(11);
        writer.Write("query");
        writer.Write("binary transport");
        writer.Write("mode");
        writer.Write("hybrid");
        writer.Write("limit");
        writer.Write(5);
        writer.Write("consistency");
        writer.Write("leader");
        writer.Write("vectorWeight");
        writer.Write(0.5);
        writer.Write("textWeight");
        writer.Write(0.5);
        writer.Write("filter");
        writer.WriteMapHeader(3);
        writer.Write("operator");
        writer.Write("equal");
        writer.Write("field");
        writer.Write("transport");
        writer.Write("value");
        writer.Write("messagepack");
        writer.Write("includeText");
        writer.Write(true);
        writer.Write("includeMetadata");
        writer.Write(true);
        writer.Write("includeScores");
        writer.Write(true);
        writer.Write("includeVector");
        writer.Write(false);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static JsonDocument MessagePackAsJson(byte[] payload) =>
        JsonDocument.Parse(MessagePackSerializer.ConvertToJson(payload));

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
