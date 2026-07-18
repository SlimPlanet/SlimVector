using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Abstractions;

public interface IEmbeddingGenerator
{
    string ModelId { get; }

    int Dimension { get; }

    ValueTask<EmbeddingModelStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask EnsureReadyAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<float[]>> GenerateAsync(
        IReadOnlyList<string> texts,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
