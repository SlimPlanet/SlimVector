using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.DocIngestor.Extractors;

public sealed class DocxTextExtractor : IDocumentTextExtractor
{
    public string Name => "Word Open XML";

    public bool CanExtract(DocumentSource source) =>
        Path.GetExtension(source.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source.ContentType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            using WordprocessingDocument document = WordprocessingDocument.Open(source.Stream, false);
            Body? body = document.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                throw new DocumentIngestionException("word_document_empty", $"Word document '{source.FileName}' has no body.");
            }

            List<ExtractedSection> sections = [];
            List<string> paragraphs = [];
            string? heading = null;

            foreach (Paragraph paragraph in body.Descendants<Paragraph>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = TextNormalization.Normalize(string.Concat(paragraph.Descendants<Text>().Select(static node => node.Text)));
                if (text.Length == 0)
                {
                    continue;
                }

                string? style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                bool isHeading = style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true ||
                    style?.StartsWith("Titre", StringComparison.OrdinalIgnoreCase) == true;
                if (isHeading)
                {
                    FlushSection(sections, paragraphs, heading);
                    heading = text;
                }
                else
                {
                    paragraphs.Add(text);
                }
            }

            FlushSection(sections, paragraphs, heading);
            Dictionary<string, string> properties = PackageProperties(document);
            return ValueTask.FromResult(new ExtractedDocument
            {
                FileName = source.FileName,
                Format = IngestedDocumentFormat.Word,
                Title = document.PackageProperties.Title,
                Sections = sections,
                Properties = properties,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DocumentIngestionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentIngestionException("word_extraction_failed", $"Unable to extract Word document '{source.FileName}'.", exception);
        }
    }

    private static void FlushSection(List<ExtractedSection> sections, List<string> paragraphs, string? heading)
    {
        if (paragraphs.Count == 0 && string.IsNullOrWhiteSpace(heading))
        {
            return;
        }

        string text = TextNormalization.Normalize(string.Join("\n\n", paragraphs));
        if (text.Length > 0 || !string.IsNullOrWhiteSpace(heading))
        {
            sections.Add(new ExtractedSection
            {
                Sequence = sections.Count,
                Location = string.IsNullOrWhiteSpace(heading) ? $"Section {sections.Count + 1}" : heading,
                Heading = heading,
                Text = string.IsNullOrWhiteSpace(heading) ? text : TextNormalization.Normalize($"{heading}\n\n{text}"),
            });
        }

        paragraphs.Clear();
    }

    private static Dictionary<string, string> PackageProperties(WordprocessingDocument document)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        Add(result, "author", document.PackageProperties.Creator);
        Add(result, "subject", document.PackageProperties.Subject);
        Add(result, "keywords", document.PackageProperties.Keywords);
        return result;
    }

    private static void Add(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[name] = value;
        }
    }
}
