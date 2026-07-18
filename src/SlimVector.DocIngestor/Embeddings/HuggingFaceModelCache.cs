using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Embeddings;

internal sealed class HuggingFaceModelCache : IDisposable
{
    private const string TokenizerFile = "tokenizer.json";
    private readonly HttpClient _httpClient;
    private readonly HuggingFaceEmbeddingOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _variant;

    public HuggingFaceModelCache(HttpClient httpClient, HuggingFaceEmbeddingOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _variant = SelectVariant();
    }

    public string Variant => _variant;

    public string TokenizerPath => Path.Combine(Path.GetFullPath(_options.ModelDirectory), TokenizerFile);

    public string ModelPath => Path.Combine(Path.GetFullPath(_options.ModelDirectory), Path.GetFileName(Variant));

    public bool IsReady => IsArtifactReady(TokenizerPath, GetArtifact(TokenizerFile)) &&
        IsArtifactReady(ModelPath, GetArtifact(Variant));

    public void Dispose() => _gate.Dispose();

    public async ValueTask EnsureReadyAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        if (!_options.AutoDownload)
        {
            throw new DocumentIngestionException(
                "embedding_model_missing",
                $"The local embedding model is missing from '{Path.GetFullPath(_options.ModelDirectory)}' and automatic download is disabled.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetFullPath(_options.ModelDirectory));
            if (!File.Exists(TokenizerPath))
            {
                await DownloadAsync(GetArtifact(TokenizerFile), TokenizerPath, progress, cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(ModelPath))
            {
                await DownloadAsync(GetArtifact(Variant), ModelPath, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(
        ModelArtifact artifact,
        string destination,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string repositoryPath = artifact.RepositoryPath;
        string escapedPath = string.Join('/', repositoryPath.Split('/').Select(Uri.EscapeDataString));
        Uri uri = new($"https://huggingface.co/{_options.ModelId}/resolve/{_options.Revision}/{escapedPath}?download=true");
        string temporary = $"{destination}.{Guid.NewGuid():N}.download";
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[1024 * 128];
            long received = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                double percent = total is > 0 ? received * 100d / total.Value : 0;
                progress?.Report(new ModelDownloadProgress(Path.GetFileName(repositoryPath), received, total, percent));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            if (artifact.Length is { } expectedLength && received != expectedLength)
            {
                throw new DocumentIngestionException(
                    "embedding_model_download_invalid",
                    $"Downloaded '{repositoryPath}' has {received} bytes; expected {expectedLength}.");
            }

            if (artifact.Sha256 is { } expectedSha256)
            {
                await using FileStream verification = File.OpenRead(temporary);
                byte[] hash = await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.Ordinal))
                {
                    throw new DocumentIngestionException(
                        "embedding_model_checksum_invalid",
                        $"Downloaded '{repositoryPath}' failed SHA-256 verification.");
                }
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    private static string SelectVariant()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return "onnx/model_qint8_arm64.onnx";
        }

        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported)
        {
            return "onnx/model_quint8_avx2.onnx";
        }

        return "onnx/model.onnx";
    }

    private ModelArtifact GetArtifact(string repositoryPath)
    {
        if (!string.Equals(_options.ModelId, HuggingFaceEmbeddingOptions.DefaultModelId, StringComparison.Ordinal) ||
            !string.Equals(_options.Revision, HuggingFaceEmbeddingOptions.DefaultRevision, StringComparison.Ordinal))
        {
            return new ModelArtifact(repositoryPath, null, null);
        }

        return repositoryPath switch
        {
            TokenizerFile => new ModelArtifact(
                repositoryPath,
                9_081_518,
                "2c3387be76557bd40970cec13153b3bbf80407865484b209e655e5e4729076b8"),
            "onnx/model_qint8_arm64.onnx" => new ModelArtifact(
                repositoryPath,
                118_412_398,
                "783fea82d71a58179b830a4dbd2d58447e640609e98eedf9ffa12622d375a672"),
            "onnx/model_quint8_avx2.onnx" => new ModelArtifact(
                repositoryPath,
                118_453_870,
                "98a01d88b7de996cdea58c32ca71208c09968d143798814b2ea09d3439dc334f"),
            "onnx/model.onnx" => new ModelArtifact(
                repositoryPath,
                470_301_610,
                "10f7a088420252b26caf819236ca2c9d2987afd0fc06fec7553b542a5655a05a"),
            _ => new ModelArtifact(repositoryPath, null, null),
        };
    }

    private static bool IsArtifactReady(string path, ModelArtifact artifact)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        long length = new FileInfo(path).Length;
        return artifact.Length is { } expectedLength ? length == expectedLength : length > 0;
    }

    private sealed record ModelArtifact(string RepositoryPath, long? Length, string? Sha256);
}
