using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.DocIngestor.Extractors;

public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public string Name => "PDF (PdfPig)";

    public bool CanExtract(DocumentSource source) =>
        Path.GetExtension(source.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            using PdfDocument document = PdfDocument.Open(source.Stream);
            List<ExtractedSection> sections = new(document.NumberOfPages);
            foreach (UglyToad.PdfPig.Content.Page page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = TextNormalization.Normalize(ContentOrderTextExtractor.GetText(page));
                if (text.Length == 0)
                {
                    continue;
                }

                sections.Add(new ExtractedSection
                {
                    Sequence = sections.Count,
                    Location = $"Page {page.Number}",
                    Text = text,
                    PageNumber = page.Number,
                });
            }

            Dictionary<string, string> properties = new(StringComparer.Ordinal)
            {
                ["pageCount"] = document.NumberOfPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            string? title = document.Information?.Title;
            AddIfPresent(properties, "author", document.Information?.Author);
            AddIfPresent(properties, "subject", document.Information?.Subject);

            return ValueTask.FromResult(new ExtractedDocument
            {
                FileName = source.FileName,
                Format = IngestedDocumentFormat.Pdf,
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                Sections = sections,
                Properties = properties,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentIngestionException("pdf_extraction_failed", $"Unable to extract PDF '{source.FileName}'.", exception);
        }
    }

    private static void AddIfPresent(Dictionary<string, string> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value;
        }
    }
}
