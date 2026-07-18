using System.Text;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.DocIngestor.Extractors;

public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown" };

    public string Name => "Plain text";

    public bool CanExtract(DocumentSource source) => Extensions.Contains(Path.GetExtension(source.FileName));

    public async ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            using StreamReader reader = new(source.Stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string text = TextNormalization.Normalize(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            return new ExtractedDocument
            {
                FileName = source.FileName,
                Format = IngestedDocumentFormat.PlainText,
                Title = Path.GetFileNameWithoutExtension(source.FileName),
                Sections = text.Length == 0
                    ? []
                    : [new ExtractedSection { Sequence = 0, Location = "Document", Text = text }],
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentIngestionException("text_extraction_failed", $"Unable to extract text document '{source.FileName}'.", exception);
        }
    }
}
