using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using A = DocumentFormat.OpenXml.Drawing;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.DocIngestor.Extractors;

public sealed class PptxTextExtractor : IDocumentTextExtractor
{
    public string Name => "PowerPoint Open XML";

    public bool CanExtract(DocumentSource source) =>
        Path.GetExtension(source.FileName).Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source.ContentType, "application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            using PresentationDocument document = PresentationDocument.Open(source.Stream, false);
            PresentationPart? presentationPart = document.PresentationPart;
            SlideIdList? slideIds = presentationPart?.Presentation?.SlideIdList;
            if (presentationPart is null || slideIds is null)
            {
                throw new DocumentIngestionException("powerpoint_document_empty", $"PowerPoint presentation '{source.FileName}' has no slides.");
            }

            List<ExtractedSection> sections = [];
            int slideNumber = 0;
            foreach (SlideId slideId in slideIds.Elements<SlideId>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                slideNumber++;
                string? relationshipId = slideId.RelationshipId?.Value;
                if (relationshipId is null || presentationPart.GetPartById(relationshipId) is not SlidePart slidePart)
                {
                    continue;
                }

                if (slidePart.Slide is not { } slide)
                {
                    continue;
                }

                string[] paragraphs = slide.Descendants<A.Paragraph>()
                    .Select(static paragraph => TextNormalization.Normalize(string.Concat(paragraph.Descendants<A.Text>().Select(static text => text.Text))))
                    .Where(static text => text.Length > 0)
                    .ToArray();
                if (paragraphs.Length == 0)
                {
                    continue;
                }

                string? heading = paragraphs[0];
                sections.Add(new ExtractedSection
                {
                    Sequence = sections.Count,
                    Location = $"Slide {slideNumber}",
                    Heading = heading,
                    Text = TextNormalization.Normalize(string.Join("\n\n", paragraphs)),
                    SlideNumber = slideNumber,
                });
            }

            Dictionary<string, string> properties = new(StringComparer.Ordinal)
            {
                ["slideCount"] = slideNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            Add(properties, "author", document.PackageProperties.Creator);
            Add(properties, "subject", document.PackageProperties.Subject);
            return ValueTask.FromResult(new ExtractedDocument
            {
                FileName = source.FileName,
                Format = IngestedDocumentFormat.PowerPoint,
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
            throw new DocumentIngestionException("powerpoint_extraction_failed", $"Unable to extract PowerPoint presentation '{source.FileName}'.", exception);
        }
    }

    private static void Add(Dictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[name] = value;
        }
    }
}
