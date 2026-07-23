using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using SlimVector.Application.Configuration;

namespace SlimVector.Application.Backups;

internal sealed class S3BackupObjectStore : IBackupObjectStore
{
    private const string ServiceName = "s3";
    private const string Terminator = "aws4_request";
    private static readonly byte[] EmptyPayload = [];
    private readonly Uri _endpoint;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public S3BackupObjectStore(BackupOptions options, HttpClient httpClient, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _endpoint = new Uri(options.S3Endpoint!.TrimEnd('/') + '/', UriKind.Absolute);
        _bucket = options.S3Bucket!;
        _prefix = options.Prefix.Trim('/');
        _region = options.S3Region;
        _accessKey = options.S3AccessKey!;
        _secretKey = options.S3SecretKey!;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    public async ValueTask<bool> PutIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(
            HttpMethod.Put,
            key,
            query: [],
            data,
            ifNoneMatch: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed || response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public async ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(
            HttpMethod.Put,
            key,
            query: [],
            data,
            ifNoneMatch: false,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(
            HttpMethod.Get,
            key,
            query: [],
            EmptyPayload,
            ifNoneMatch: false,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"S3 backup object '{key}' was not found.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        List<string> keys = [];
        string? continuationToken = null;
        do
        {
            List<KeyValuePair<string, string>> query =
            [
                new("list-type", "2"),
                new("prefix", PhysicalKey(prefix).TrimEnd('/') + '/'),
            ];
            if (continuationToken is not null)
            {
                query.Add(new KeyValuePair<string, string>("continuation-token", continuationToken));
            }

            using HttpResponseMessage response = await SendWithRetriesAsync(
                HttpMethod.Get,
                key: string.Empty,
                query,
                EmptyPayload,
                ifNoneMatch: false,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            (IReadOnlyList<string> page, continuationToken) = ReadListResponse(content);
            keys.AddRange(page.Select(ToLogicalKey));
        }
        while (continuationToken is not null);

        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(
            HttpMethod.Delete,
            key,
            query: [],
            EmptyPayload,
            ifNoneMatch: false,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpMethod method,
        string key,
        IReadOnlyList<KeyValuePair<string, string>> query,
        ReadOnlyMemory<byte> payload,
        bool ifNoneMatch,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                HttpRequestMessage request = CreateRequest(method, key, query, payload, ifNoneMatch);
                HttpResponseMessage response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                request.Dispose();
                if ((int)response.StatusCode < 500 || attempt == 3)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException exception) when (attempt < 3)
            {
                lastFailure = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new HttpRequestException("S3 backup request failed after retries.", lastFailure);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string key,
        IReadOnlyList<KeyValuePair<string, string>> query,
        ReadOnlyMemory<byte> payload,
        bool ifNoneMatch)
    {
        string physicalKey = string.IsNullOrEmpty(key) ? string.Empty : PhysicalKey(key);
        string canonicalPath = '/' + EscapePath(_bucket) + (physicalKey.Length == 0 ? string.Empty : '/' + EscapePath(physicalKey));
        string canonicalQuery = string.Join(
            '&',
            query.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Value, StringComparer.Ordinal)
                .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        UriBuilder uri = new(_endpoint)
        {
            Path = _endpoint.AbsolutePath.TrimEnd('/') + canonicalPath,
            Query = canonicalQuery,
        };
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload.Span));
        string host = uri.Uri.IsDefaultPort ? uri.Host : uri.Uri.Authority;
        string canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{timestamp}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        string canonicalRequest = $"{method.Method}\n{canonicalPath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        string scope = $"{date}/{_region}/{ServiceName}/{Terminator}";
        string stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{HashHex(canonicalRequest)}";
        byte[] signingKey = DeriveSigningKey(date);
        string signature = Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        HttpRequestMessage request = new(method, uri.Uri);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        if (ifNoneMatch)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(payload.ToArray());
        }

        return request;
    }

    private byte[] DeriveSigningKey(string date)
    {
        byte[] dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + _secretKey), Encoding.UTF8.GetBytes(date));
        byte[] regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(_region));
        byte[] serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(ServiceName));
        return HMACSHA256.HashData(serviceKey, Encoding.UTF8.GetBytes(Terminator));
    }

    private string PhysicalKey(string key) => string.IsNullOrEmpty(_prefix) ? key : $"{_prefix}/{key}";

    private string ToLogicalKey(string key)
    {
        string prefix = string.IsNullOrEmpty(_prefix) ? string.Empty : _prefix + '/';
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"S3 returned object key '{key}' outside the configured backup prefix.");
        }

        return key[prefix.Length..];
    }

    private static string HashHex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string EscapePath(string value) => string.Join(
        '/',
        value.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static (IReadOnlyList<string> Keys, string? ContinuationToken) ReadListResponse(Stream content)
    {
        List<string> keys = [];
        string? continuationToken = null;
        using XmlReader reader = XmlReader.Create(content, new XmlReaderSettings
        {
            Async = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "Key")
            {
                keys.Add(reader.ReadElementContentAsString());
            }
            else if (reader.LocalName == "NextContinuationToken")
            {
                continuationToken = reader.ReadElementContentAsString();
            }
        }

        return (keys, continuationToken);
    }
}
