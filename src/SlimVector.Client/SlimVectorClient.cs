using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SlimVector.Client;

public sealed class SlimVectorClient
{
    private readonly HttpClient _httpClient;

    public SlimVectorClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("The HTTP client must have a base address.", nameof(httpClient));
        }

        _httpClient = httpClient;
    }

    public Task<CollectionInfo> CreateCollectionAsync(CreateCollectionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/collections", request, ClientJsonContext.Default.CreateCollectionRequest, ClientJsonContext.Default.CollectionInfo, cancellationToken);

    public Task<CollectionInfo> GetOrCreateCollectionAsync(CreateCollectionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/collections/get-or-create", request, ClientJsonContext.Default.CreateCollectionRequest, ClientJsonContext.Default.CollectionInfo, cancellationToken);

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        CollectionList response = await GetAsync("api/v1/collections", ClientJsonContext.Default.CollectionList, cancellationToken).ConfigureAwait(false);
        return response.Collections;
    }

    public Task<CollectionInfo> GetCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        GetAsync(CollectionPath(name), ClientJsonContext.Default.CollectionInfo, cancellationToken);

    public Task<CollectionInfo> UpdateCollectionAsync(
        string name,
        UpdateCollectionRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, CollectionPath(name), request, ClientJsonContext.Default.UpdateCollectionRequest, ClientJsonContext.Default.CollectionInfo, cancellationToken);

    public async Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, CollectionPath(name));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<BatchResult> AddDocumentsAsync(
        string collection,
        IReadOnlyList<SlimVectorDocument> documents,
        bool atomic = true,
        CancellationToken cancellationToken = default) =>
        MutateDocumentsAsync(collection, "add", documents, atomic, cancellationToken);

    public Task<BatchResult> UpsertDocumentsAsync(
        string collection,
        IReadOnlyList<SlimVectorDocument> documents,
        bool atomic = true,
        CancellationToken cancellationToken = default) =>
        MutateDocumentsAsync(collection, "upsert", documents, atomic, cancellationToken);

    public Task<BatchResult> UpdateDocumentsAsync(
        string collection,
        IReadOnlyList<SlimVectorDocumentUpdate> documents,
        bool atomic = true,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Patch,
            DocumentsPath(collection),
            new DocumentUpdateBatch { Documents = documents.ToArray(), Atomic = atomic },
            ClientJsonContext.Default.DocumentUpdateBatch,
            ClientJsonContext.Default.BatchResult,
            cancellationToken);

    public async Task<IReadOnlyList<SlimVectorDocument>> GetDocumentsAsync(
        string collection,
        IReadOnlyList<string>? ids = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        StringBuilder query = new($"{DocumentsPath(collection)}?offset={offset}&limit={limit}");
        if (ids is not null)
        {
            foreach (string id in ids)
            {
                query.Append("&ids=").Append(Uri.EscapeDataString(id));
            }
        }

        DocumentList response = await GetAsync(query.ToString(), ClientJsonContext.Default.DocumentList, cancellationToken).ConfigureAwait(false);
        return response.Documents;
    }

    public Task<BatchResult> DeleteDocumentsAsync(
        string collection,
        IReadOnlyList<string> ids,
        bool atomic = true,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            $"{DocumentsPath(collection)}/delete",
            new DocumentDelete { Ids = ids.ToArray(), Atomic = atomic },
            ClientJsonContext.Default.DocumentDelete,
            ClientJsonContext.Default.BatchResult,
            cancellationToken);

    public async Task<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken = default)
    {
        DocumentCount response = await GetAsync($"{DocumentsPath(collection)}/count", ClientJsonContext.Default.DocumentCount, cancellationToken).ConfigureAwait(false);
        return response.Count;
    }

    public Task<SlimVectorQueryResult> QueryAsync(
        string collection,
        SlimVectorQuery query,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            $"{DocumentsPath(collection)}/query",
            query,
            ClientJsonContext.Default.SlimVectorQuery,
            ClientJsonContext.Default.SlimVectorQueryResult,
            cancellationToken);

    public async Task<IndexStatusInfo> GetIndexStatusAsync(
        string collection,
        string adminApiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"api/v1/admin/collections/{Uri.EscapeDataString(collection)}/index");
        request.Headers.Add("X-SlimVector-Admin-Key", adminApiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, ClientJsonContext.Default.IndexStatusInfo, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RollbackIndexAsync(
        string collection,
        string adminApiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"api/v1/admin/collections/{Uri.EscapeDataString(collection)}/index/rollback");
        request.Headers.Add("X-SlimVector-Admin-Key", adminApiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        AdminOperationInfo result = await ReadResponseAsync(
            response,
            ClientJsonContext.Default.AdminOperationInfo,
            cancellationToken).ConfigureAwait(false);
        return string.Equals(result.Status, "rolled-back", StringComparison.Ordinal);
    }

    public async Task<ClusterMembershipInfo> GetClusterMembershipAsync(
        string adminApiKey,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "api/v1/admin/cluster/membership");
        request.Headers.Add("X-SlimVector-Admin-Key", adminApiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(
            response,
            ClientJsonContext.Default.ClusterMembershipInfo,
            cancellationToken).ConfigureAwait(false);
    }

    public Task AddClusterMemberAsync(
        string groupId,
        string endpoint,
        string adminApiKey,
        CancellationToken cancellationToken = default) =>
        ChangeClusterMembershipAsync("add", groupId, endpoint, adminApiKey, cancellationToken);

    public Task PromoteClusterMemberAsync(
        string groupId,
        string endpoint,
        string adminApiKey,
        CancellationToken cancellationToken = default) =>
        ChangeClusterMembershipAsync("promote", groupId, endpoint, adminApiKey, cancellationToken);

    public Task DemoteClusterMemberAsync(
        string groupId,
        string endpoint,
        string adminApiKey,
        CancellationToken cancellationToken = default) =>
        ChangeClusterMembershipAsync("demote", groupId, endpoint, adminApiKey, cancellationToken);

    public Task RemoveClusterMemberAsync(
        string groupId,
        string endpoint,
        string adminApiKey,
        CancellationToken cancellationToken = default) =>
        ChangeClusterMembershipAsync("remove", groupId, endpoint, adminApiKey, cancellationToken);

    public Task TransferClusterLeadershipAsync(
        string groupId,
        string adminApiKey,
        CancellationToken cancellationToken = default) =>
        ChangeClusterMembershipAsync("transfer-leadership", groupId, endpoint: null, adminApiKey, cancellationToken);

    private Task<BatchResult> MutateDocumentsAsync(
        string collection,
        string operation,
        IReadOnlyList<SlimVectorDocument> documents,
        bool atomic,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            $"{DocumentsPath(collection)}/{operation}",
            new DocumentBatch { Documents = documents.ToArray(), Atomic = atomic },
            ClientJsonContext.Default.DocumentBatch,
            ClientJsonContext.Default.BatchResult,
            cancellationToken);

    private async Task ChangeClusterMembershipAsync(
        string operation,
        string groupId,
        string? endpoint,
        string adminApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (!string.Equals(operation, "transfer-leadership", StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        }

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"api/v1/admin/cluster/membership/{operation}")
        {
            Content = JsonContent.Create(
                new MembershipChange { GroupId = groupId, Endpoint = endpoint },
                ClientJsonContext.Default.MembershipChange),
        };
        request.Headers.Add("X-SlimVector-Admin-Key", adminApiKey);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await ReadResponseAsync(
            response,
            ClientJsonContext.Default.AdminOperationInfo,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> GetAsync<TResponse>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path)
        {
            Content = JsonContent.Create(body, requestType),
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, responseType, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse> ReadResponseAsync<TResponse>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(responseType, cancellationToken).ConfigureAwait(false)
            ?? throw new SlimVectorClientException(response.StatusCode, "empty_response", "SlimVector returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiProblem? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ApiProblem, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Preserve the HTTP status when a proxy returned a non-JSON response.
        }

        throw new SlimVectorClientException(
            response.StatusCode,
            problem?.Code,
            problem?.Detail ?? problem?.Title ?? $"SlimVector returned HTTP {(int)response.StatusCode}.");
    }

    private static string CollectionPath(string name) => $"api/v1/collections/{Uri.EscapeDataString(name)}";

    private static string DocumentsPath(string collection) => $"{CollectionPath(collection)}/documents";
}
