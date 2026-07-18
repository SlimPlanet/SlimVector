using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SlimVector.DocIngestor.Extractors;
using SlimVector.DocIngestor.Models;
using A = DocumentFormat.OpenXml.Drawing;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SlimVector.DocIngestor.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task PdfExtractorPreservesPageLocation()
    {
        await using MemoryStream stream = new(CreatePdf("SlimVector PDF extraction"));
        DocumentSource source = new(stream, "sample.pdf", "application/pdf");

        ExtractedDocument document = await new PdfTextExtractor().ExtractAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(IngestedDocumentFormat.Pdf, document.Format);
        ExtractedSection section = Assert.Single(document.Sections);
        Assert.Equal(1, section.PageNumber);
        Assert.Contains("SlimVector PDF extraction", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WordExtractorGroupsParagraphsUnderHeadings()
    {
        await using MemoryStream stream = CreateDocx();
        DocumentSource source = new(stream, "sample.docx");

        ExtractedDocument document = await new DocxTextExtractor().ExtractAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(IngestedDocumentFormat.Word, document.Format);
        ExtractedSection section = Assert.Single(document.Sections);
        Assert.Equal("Architecture", section.Heading);
        Assert.Contains("persistent vector database", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerPointExtractorPreservesSlideLocation()
    {
        await using MemoryStream stream = CreatePptx();
        DocumentSource source = new(stream, "sample.pptx");

        ExtractedDocument document = await new PptxTextExtractor().ExtractAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(IngestedDocumentFormat.PowerPoint, document.Format);
        ExtractedSection section = Assert.Single(document.Sections);
        Assert.Equal(1, section.SlideNumber);
        Assert.Contains("Hybrid search", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouterRejectsUnsupportedFileTypesWithStableCode()
    {
        DocumentExtractorRouter router = new([new PlainTextExtractor()]);
        await using MemoryStream stream = new([1, 2, 3]);
        DocumentSource source = new(stream, "archive.zip");

        DocumentIngestionException exception = await Assert.ThrowsAsync<DocumentIngestionException>(async () =>
            await router.ExtractAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal("unsupported_document_format", exception.Code);
    }

    private static MemoryStream CreateDocx()
    {
        MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("Architecture"))),
                new W.Paragraph(new W.Run(new W.Text("SlimVector is a persistent vector database.")))));
            document.PackageProperties.Title = "Test document";
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreatePptx()
    {
        MemoryStream stream = new();
        using (PresentationDocument document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            PresentationPart presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation();
            SlidePart slide = presentation.AddNewPart<SlidePart>();
            slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.Shape(new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text("Hybrid search with vectors and BM25"))))))));
            presentation.Presentation.SlideIdList = new P.SlideIdList(
                new P.SlideId { Id = 256U, RelationshipId = presentation.GetIdOfPart(slide) });
            document.PackageProperties.Title = "Test deck";
        }

        stream.Position = 0;
        return stream;
    }

    private static byte[] CreatePdf(string text)
    {
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        string content = $"BT /F1 18 Tf 72 720 Td ({escaped}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        StringBuilder builder = new("%PDF-1.4\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        for (int index = 1; index < offsets.Count; index++)
        {
            builder.Append(offsets[index].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
