using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Abstractions;

public interface IDocumentIngestionPipeline
{
    ValueTask<IngestionResult> IngestAsync(
        DocumentSource source,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
