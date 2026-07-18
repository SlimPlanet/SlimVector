using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Extractors;

public sealed class DocumentExtractorRouter
{
    private readonly IDocumentTextExtractor[] _extractors;

    public DocumentExtractorRouter(IEnumerable<IDocumentTextExtractor> extractors)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToArray();
        if (_extractors.Length == 0)
        {
            throw new ArgumentException("At least one document extractor must be registered.", nameof(extractors));
        }
    }

    public IReadOnlyList<string> ExtractorNames => _extractors.Select(static extractor => extractor.Name).ToArray();

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        IDocumentTextExtractor? extractor = _extractors.FirstOrDefault(candidate => candidate.CanExtract(source));
        if (extractor is null)
        {
            throw new DocumentIngestionException(
                "unsupported_document_format",
                $"No extractor is available for '{source.FileName}'. Supported extensions: .pdf, .docx, .pptx, .txt, and .md.");
        }

        if (source.Stream.CanSeek)
        {
            source.Stream.Position = 0;
        }

        return extractor.ExtractAsync(source, cancellationToken);
    }
}
