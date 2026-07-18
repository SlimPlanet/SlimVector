using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Abstractions;

public interface IDocumentTextExtractor
{
    string Name { get; }

    bool CanExtract(DocumentSource source);

    ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default);
}
