using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Indexing;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Routing;

public interface ILocalDataQueryService
{
    ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        IReadOnlyList<string>? ids,
        int limit,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DocumentRecord>> GetRawDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<long> CountDocumentsLocalAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<Bm25CorpusStatistics> GetTextCorpusStatisticsLocalAsync(
        string collectionName,
        string dataGroupId,
        string query,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<SearchResponse> SearchLocalAsync(
        string collectionName,
        string dataGroupId,
        SearchRequest request,
        Bm25CorpusStatistics? corpusStatistics,
        CancellationToken cancellationToken = default);
}

public interface IDataNodeQueryClient
{
    ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        string collectionName,
        string dataGroupId,
        IReadOnlyList<string>? ids,
        int limit,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DocumentRecord>> GetRawDocumentsAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<long> CountDocumentsAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<Bm25CorpusStatistics> GetTextCorpusStatisticsAsync(
        string collectionName,
        string dataGroupId,
        string query,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default);

    ValueTask<SearchResponse> SearchAsync(
        string collectionName,
        string dataGroupId,
        SearchRequest request,
        Bm25CorpusStatistics? corpusStatistics,
        CancellationToken cancellationToken = default);
}

public interface IDataNodeQueryReceiver
{
    ValueTask<byte[]> ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string? signature,
        CancellationToken cancellationToken = default);
}

public enum DataNodeQueryKind
{
    TextCorpusStatistics = 1,
    Search = 2,
    Documents = 3,
    Count = 4,
    RawDocuments = 5,
}

[MemoryPackable]
public sealed partial class DataNodeQueryRequest
{
    public int FormatVersion { get; set; } = 1;

    public DataNodeQueryKind Kind { get; set; }

    public string CollectionName { get; set; } = string.Empty;

    public string DataGroupId { get; set; } = string.Empty;

    public RaftSearchRequest Search { get; set; } = new();

    public Bm25CorpusStatistics? CorpusStatistics { get; set; }

    public string[]? DocumentIds { get; set; }
}

[MemoryPackable]
public sealed partial class DataNodeQueryResponse
{
    public int FormatVersion { get; set; } = 1;

    public RaftSearchHit[] Hits { get; set; } = [];

    public long TookMicroseconds { get; set; }

    public Bm25CorpusStatistics? CorpusStatistics { get; set; }

    public RaftDocument[] Documents { get; set; } = [];

    public long DocumentCount { get; set; }
}

[MemoryPackable]
public sealed partial class RaftSearchRequest
{
    public string? Text { get; set; }

    public float[]? Vector { get; set; }

    public SearchMode Mode { get; set; }

    public int Limit { get; set; }

    public RaftMetadataFilter? Filter { get; set; }

    public IncludeFields Include { get; set; }

    public ReadConsistency Consistency { get; set; }

    public double VectorWeight { get; set; }

    public double TextWeight { get; set; }
}

[MemoryPackable]
public sealed partial class RaftMetadataFilter
{
    public MetadataOperator Operator { get; set; }

    public string? Field { get; set; }

    public RaftMetadataValue? Value { get; set; }

    public RaftMetadataValue[]? Values { get; set; }

    public RaftMetadataFilter[]? Operands { get; set; }
}

[MemoryPackable]
public sealed partial class RaftSearchHit
{
    public string Id { get; set; } = string.Empty;

    public string? Text { get; set; }

    public float[]? Vector { get; set; }

    public RaftMetadataEntry[]? Metadata { get; set; }

    public double? Score { get; set; }

    public int? VectorRank { get; set; }

    public int? TextRank { get; set; }
}

public sealed class DataNodeQueryClient : IDataNodeQueryClient
{
    private const string EndpointPath = "/internal/data/query";
    private readonly HttpClient _httpClient;
    private readonly IClusterTopologyStore _topologyStore;
    private readonly string _localNodeId;
    private readonly byte[]? _sharedSecret;
    private readonly DataNodeRpcMetrics _metrics;

    public DataNodeQueryClient(
        HttpClient httpClient,
        IClusterTopologyStore topologyStore,
        IOptions<RaftOptions> raftOptions,
        IOptions<ApiOptions> apiOptions,
        DataNodeRpcMetrics metrics)
    {
        _httpClient = httpClient;
        _topologyStore = topologyStore;
        _localNodeId = raftOptions.Value.NodeId;
        _sharedSecret = string.IsNullOrEmpty(apiOptions.Value.AdminApiKey)
            ? null
            : Encoding.UTF8.GetBytes(apiOptions.Value.AdminApiKey);
        _metrics = metrics;
    }

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(
        string collectionName,
        string dataGroupId,
        IReadOnlyList<string>? ids,
        int limit,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        DataNodeQueryResponse response = await SendAsync(
            new DataNodeQueryRequest
            {
                Kind = DataNodeQueryKind.Documents,
                CollectionName = collectionName,
                DataGroupId = dataGroupId,
                DocumentIds = ids?.ToArray(),
                Search = FromDomain(new SearchRequest { Limit = limit, Consistency = consistency }),
            },
            cancellationToken).ConfigureAwait(false);
        return response.Documents.Select(RaftCommandCodec.ToDomain).ToArray();
    }

    public async ValueTask<long> CountDocumentsAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        DataNodeQueryResponse response = await SendAsync(
            new DataNodeQueryRequest
            {
                Kind = DataNodeQueryKind.Count,
                CollectionName = collectionName,
                DataGroupId = dataGroupId,
                Search = FromDomain(new SearchRequest { Limit = 1, Consistency = consistency }),
            },
            cancellationToken).ConfigureAwait(false);
        return response.DocumentCount;
    }

    public async ValueTask<IReadOnlyList<DocumentRecord>> GetRawDocumentsAsync(
        string collectionName,
        string dataGroupId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        DataNodeQueryResponse response = await SendAsync(
            new DataNodeQueryRequest
            {
                Kind = DataNodeQueryKind.RawDocuments,
                CollectionName = collectionName,
                DataGroupId = dataGroupId,
                Search = FromDomain(new SearchRequest { Limit = 1, Consistency = consistency }),
            },
            cancellationToken).ConfigureAwait(false);
        return response.Documents.Select(RaftCommandCodec.ToDomain).ToArray();
    }

    public async ValueTask<Bm25CorpusStatistics> GetTextCorpusStatisticsAsync(
        string collectionName,
        string dataGroupId,
        string query,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        DataNodeQueryResponse response = await SendAsync(
            new DataNodeQueryRequest
            {
                Kind = DataNodeQueryKind.TextCorpusStatistics,
                CollectionName = collectionName,
                DataGroupId = dataGroupId,
                Search = FromDomain(new SearchRequest
                {
                    Text = query,
                    Mode = SearchMode.Text,
                    Limit = 1,
                    Consistency = consistency,
                }),
            },
            cancellationToken).ConfigureAwait(false);
        return response.CorpusStatistics ?? throw new InvalidDataException(
            $"Data group '{dataGroupId}' returned no BM25 corpus statistics.");
    }

    public async ValueTask<SearchResponse> SearchAsync(
        string collectionName,
        string dataGroupId,
        SearchRequest request,
        Bm25CorpusStatistics? corpusStatistics,
        CancellationToken cancellationToken = default)
    {
        DataNodeQueryResponse response = await SendAsync(
            new DataNodeQueryRequest
            {
                Kind = DataNodeQueryKind.Search,
                CollectionName = collectionName,
                DataGroupId = dataGroupId,
                Search = FromDomain(request),
                CorpusStatistics = corpusStatistics,
            },
            cancellationToken).ConfigureAwait(false);
        return new SearchResponse
        {
            Hits = response.Hits.Select(ToDomain).ToArray(),
            TookMicroseconds = response.TookMicroseconds,
        };
    }

    private async ValueTask<DataNodeQueryResponse> SendAsync(
        DataNodeQueryRequest query,
        CancellationToken cancellationToken)
    {
        byte[] payload = MemoryPackSerializer.Serialize(query);
        ClusterTopology topology = await _topologyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        DataGroupDescriptor group = topology.DataGroups.FirstOrDefault(candidate =>
                string.Equals(candidate.GroupId, query.DataGroupId, StringComparison.Ordinal) &&
                candidate.State != DataGroupState.Removed)
            ?? throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"Data group '{query.DataGroupId}' is absent from topology epoch {topology.Epoch}.");
        Dictionary<string, ClusterNodeDescriptor> nodes = topology.Nodes.ToDictionary(
            static node => node.NodeId,
            StringComparer.Ordinal);
        IEnumerable<DataGroupReplica> orderedReplicas = query.Search.Consistency == ReadConsistency.Stale
            ? group.Replicas
                .OrderByDescending(static replica => replica.Healthy)
                .ThenBy(static replica => replica.ObservedReplicationLag ?? long.MaxValue)
                .ThenBy(static replica => replica.NodeId, StringComparer.Ordinal)
            : group.Replicas.OrderBy(static replica => replica.NodeId, StringComparer.Ordinal);
        ClusterNodeDescriptor[] targets = orderedReplicas
            .Where(replica => !string.Equals(replica.NodeId, _localNodeId, StringComparison.Ordinal))
            .Select(replica => nodes.GetValueOrDefault(replica.NodeId))
            .Where(static node => node is not null && node.State is ClusterNodeState.Active or ClusterNodeState.Draining)
            .Cast<ClusterNodeDescriptor>()
            .ToArray();
        Exception? lastFailure = null;
        foreach (ClusterNodeDescriptor target in targets)
        {
            long started = Stopwatch.GetTimestamp();
            bool succeeded = false;
            try
            {
                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    target.InternalEndpoint.TrimEnd('/') + EndpointPath)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                    Content = new ByteArrayContent(payload),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");
                if (_sharedSecret is not null)
                {
                    request.Headers.TryAddWithoutValidation(
                        "X-SlimVector-Signature",
                        Convert.ToHexStringLower(HMACSHA256.HashData(_sharedSecret, payload)));
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    byte[] responsePayload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    DataNodeQueryResponse result = MemoryPackSerializer.Deserialize<DataNodeQueryResponse>(responsePayload)
                        ?? throw new InvalidDataException("The internal query response is empty.");
                    if (result.FormatVersion != 1)
                    {
                        throw new InvalidDataException($"Internal query response version '{result.FormatVersion}' is unsupported.");
                    }

                    succeeded = true;
                    return result;
                }

                string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lastFailure = new HttpRequestException(
                    $"Data node '{target.NodeId}' returned HTTP {(int)response.StatusCode}: {detail}",
                    inner: null,
                    response.StatusCode);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                lastFailure = exception;
            }
            finally
            {
                _metrics.RecordOutgoing(Stopwatch.GetElapsedTime(started), succeeded);
            }
        }

        throw new ConsensusUnavailableException(
            query.DataGroupId,
            ConsensusFailureKind.QuorumUnavailable,
            leaderEndpoint: null,
            lastFailure ?? new InvalidOperationException($"No remote replica is available for '{query.DataGroupId}'."));
    }

    internal static RaftSearchRequest FromDomain(SearchRequest request) => new()
    {
        Text = request.Text,
        Vector = request.Vector,
        Mode = request.Mode,
        Limit = request.Limit,
        Filter = request.Filter is null ? null : FromDomain(request.Filter),
        Include = request.Include,
        Consistency = request.Consistency,
        VectorWeight = request.VectorWeight,
        TextWeight = request.TextWeight,
    };

    internal static SearchRequest ToDomain(RaftSearchRequest request) => new()
    {
        Text = request.Text,
        Vector = request.Vector,
        Mode = request.Mode,
        Limit = request.Limit,
        Filter = request.Filter is null ? null : ToDomain(request.Filter),
        Include = request.Include,
        Consistency = request.Consistency,
        VectorWeight = request.VectorWeight,
        TextWeight = request.TextWeight,
    };

    internal static RaftSearchHit FromDomain(SearchHit hit) => new()
    {
        Id = hit.Id,
        Text = hit.Text,
        Vector = hit.Vector,
        Metadata = hit.Metadata?.Select(static pair => new RaftMetadataEntry
        {
            Key = pair.Key,
            Value = RaftCommandCodec.FromDomain(pair.Value),
        }).ToArray(),
        Score = hit.Score,
        VectorRank = hit.VectorRank,
        TextRank = hit.TextRank,
    };

    internal static SearchHit ToDomain(RaftSearchHit hit) => new()
    {
        Id = hit.Id,
        Text = hit.Text,
        Vector = hit.Vector,
        Metadata = hit.Metadata?.ToDictionary(
            static entry => entry.Key,
            static entry => RaftCommandCodec.ToDomain(entry.Value),
            StringComparer.Ordinal),
        Score = hit.Score,
        VectorRank = hit.VectorRank,
        TextRank = hit.TextRank,
    };

    private static RaftMetadataFilter FromDomain(MetadataFilter filter) => new()
    {
        Operator = filter.Operator,
        Field = filter.Field,
        Value = filter.Value is null ? null : RaftCommandCodec.FromDomain(filter.Value),
        Values = filter.Values?.Select(RaftCommandCodec.FromDomain).ToArray(),
        Operands = filter.Operands?.Select(FromDomain).ToArray(),
    };

    private static MetadataFilter ToDomain(RaftMetadataFilter filter) => new()
    {
        Operator = filter.Operator,
        Field = filter.Field,
        Value = filter.Value is null ? null : RaftCommandCodec.ToDomain(filter.Value),
        Values = filter.Values?.Select(RaftCommandCodec.ToDomain).ToArray(),
        Operands = filter.Operands?.Select(ToDomain).ToArray(),
    };
}

public sealed class DataNodeQueryReceiver : IDataNodeQueryReceiver
{
    private readonly ILocalDataQueryService _queries;
    private readonly ILocalRaftGroupManager _groups;
    private readonly byte[]? _sharedSecret;
    private readonly DataNodeRpcMetrics _metrics;

    public DataNodeQueryReceiver(
        ILocalDataQueryService queries,
        ILocalRaftGroupManager groups,
        IOptions<ApiOptions> apiOptions,
        DataNodeRpcMetrics metrics)
    {
        _queries = queries;
        _groups = groups;
        _sharedSecret = string.IsNullOrEmpty(apiOptions.Value.AdminApiKey)
            ? null
            : Encoding.UTF8.GetBytes(apiOptions.Value.AdminApiKey);
        _metrics = metrics;
    }

    public async ValueTask<byte[]> ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string? signature,
        CancellationToken cancellationToken = default)
    {
        ValidateSignature(payload.Span, signature);
        DataNodeQueryRequest request = MemoryPackSerializer.Deserialize<DataNodeQueryRequest>(payload.Span)
            ?? throw new InvalidDataException("The internal query request is empty.");
        if (request.FormatVersion != 1 || string.IsNullOrWhiteSpace(request.CollectionName) ||
            string.IsNullOrWhiteSpace(request.DataGroupId) || !Enum.IsDefined(request.Kind))
        {
            throw new InvalidDataException("The internal query request is malformed.");
        }

        if (!_groups.GetHostedDataGroupIds().Contains(request.DataGroupId, StringComparer.Ordinal))
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"This node does not host data group '{request.DataGroupId}'.");
        }

        SearchRequest search = DataNodeQueryClient.ToDomain(request.Search);
        DataNodeQueryResponse response = request.Kind switch
        {
            DataNodeQueryKind.Documents => new DataNodeQueryResponse
            {
                Documents = (await _queries.GetDocumentsLocalAsync(
                    request.CollectionName,
                    request.DataGroupId,
                    request.DocumentIds,
                    search.Limit,
                    search.Consistency,
                    cancellationToken).ConfigureAwait(false)).Select(RaftCommandCodec.FromDomain).ToArray(),
            },
            DataNodeQueryKind.Count => new DataNodeQueryResponse
            {
                DocumentCount = await _queries.CountDocumentsLocalAsync(
                    request.CollectionName,
                    request.DataGroupId,
                    search.Consistency,
                    cancellationToken).ConfigureAwait(false),
            },
            DataNodeQueryKind.RawDocuments => new DataNodeQueryResponse
            {
                Documents = (await _queries.GetRawDocumentsLocalAsync(
                    request.CollectionName,
                    request.DataGroupId,
                    search.Consistency,
                    cancellationToken).ConfigureAwait(false)).Select(RaftCommandCodec.FromDomain).ToArray(),
            },
            DataNodeQueryKind.TextCorpusStatistics => new DataNodeQueryResponse
            {
                CorpusStatistics = await _queries.GetTextCorpusStatisticsLocalAsync(
                    request.CollectionName,
                    request.DataGroupId,
                    search.Text ?? throw new InvalidDataException("A text query is required for corpus statistics."),
                    search.Consistency,
                    cancellationToken).ConfigureAwait(false),
            },
            DataNodeQueryKind.Search => await SearchAsync(request, search, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Unknown internal query kind '{request.Kind}'."),
        };
        _metrics.RecordIncoming();
        return MemoryPackSerializer.Serialize(response);
    }

    private async ValueTask<DataNodeQueryResponse> SearchAsync(
        DataNodeQueryRequest request,
        SearchRequest search,
        CancellationToken cancellationToken)
    {
        SearchResponse response = await _queries.SearchLocalAsync(
            request.CollectionName,
            request.DataGroupId,
            search,
            request.CorpusStatistics,
            cancellationToken).ConfigureAwait(false);
        return new DataNodeQueryResponse
        {
            Hits = response.Hits.Select(DataNodeQueryClient.FromDomain).ToArray(),
            TookMicroseconds = response.TookMicroseconds,
        };
    }

    private void ValidateSignature(ReadOnlySpan<byte> payload, string? signature)
    {
        if (_sharedSecret is null)
        {
            return;
        }

        byte[] expected = HMACSHA256.HashData(_sharedSecret, payload);
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(signature ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new UnauthorizedAccessException("The internal RPC signature is invalid.");
        }

        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new UnauthorizedAccessException("The internal RPC signature is invalid.");
        }
    }
}
