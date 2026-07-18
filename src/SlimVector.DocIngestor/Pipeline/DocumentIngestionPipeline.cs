using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Extractors;
using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Pipeline;

public sealed class DocumentIngestionPipeline : IDocumentIngestionPipeline
{
    private readonly DocumentExtractorRouter _router;
    private readonly ITextChunker _chunker;
    private readonly IEmbeddingGenerator _embeddingGenerator;

    public DocumentIngestionPipeline(
        DocumentExtractorRouter router,
        ITextChunker chunker,
        IEmbeddingGenerator embeddingGenerator)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
    }

    public async ValueTask<IngestionResult> IngestAsync(
        DocumentSource source,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new IngestionOptions();
        options.Chunking.Validate();

        progress?.Report(new IngestionProgress(IngestionStage.Reading, 0, $"Reading {source.FileName}"));
        byte[] content = await ReadAllAsync(source.Stream, cancellationToken).ConfigureAwait(false);
        if (content.Length == 0)
        {
            throw new DocumentIngestionException("document_empty", $"Document '{source.FileName}' is empty.");
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string documentId = CreateDocumentId(source.FileName, sha256);
        await using DocumentSource buffered = new(
            new MemoryStream(content, writable: false),
            source.FileName,
            source.ContentType,
            content.Length,
            ownsStream: true);

        Stopwatch stopwatch = Stopwatch.StartNew();
        progress?.Report(new IngestionProgress(IngestionStage.Extracting, 10, "Extracting structured text"));
        ExtractedDocument document = await _router.ExtractAsync(buffered, cancellationToken).ConfigureAwait(false);
        TimeSpan extractionDuration = stopwatch.Elapsed;
        if (document.Sections.Count == 0 || document.CharacterCount == 0)
        {
            throw new DocumentIngestionException(
                "document_contains_no_text",
                $"No selectable text was found in '{source.FileName}'. Scanned documents require OCR before ingestion.");
        }

        stopwatch.Restart();
        progress?.Report(new IngestionProgress(IngestionStage.Chunking, 35, "Creating overlapping chunks"));
        IReadOnlyList<TextChunk> chunks = _chunker.Chunk(document, options.Chunking);
        TimeSpan chunkingDuration = stopwatch.Elapsed;

        IReadOnlyList<float[]> vectors;
        stopwatch.Restart();
        if (options.GenerateEmbeddings)
        {
            progress?.Report(new IngestionProgress(IngestionStage.PreparingModel, 45, "Preparing the local multilingual model"));
            Progress<ModelDownloadProgress> downloadProgress = new(download =>
            {
                string total = download.TotalBytes is { } bytes ? $" / {FormatBytes(bytes)}" : string.Empty;
                progress?.Report(new IngestionProgress(
                    IngestionStage.PreparingModel,
                    45 + download.Percent * 0.15,
                    $"Downloading {download.FileName}: {FormatBytes(download.BytesReceived)}{total}"));
            });
            await _embeddingGenerator.EnsureReadyAsync(downloadProgress, cancellationToken).ConfigureAwait(false);
            Progress<double> embeddingProgress = new(percent => progress?.Report(new IngestionProgress(
                IngestionStage.Embedding,
                60 + percent * 0.38,
                $"Vectorizing chunks ({Math.Round(percent)}%)")));
            vectors = await _embeddingGenerator
                .GenerateAsync(chunks.Select(static chunk => chunk.Text).ToArray(), embeddingProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            vectors = Enumerable.Range(0, chunks.Count).Select(static _ => Array.Empty<float>()).ToArray();
        }

        TimeSpan embeddingDuration = stopwatch.Elapsed;
        EmbeddedChunk[] embedded = chunks.Select((chunk, index) => new EmbeddedChunk
        {
            Id = $"{documentId}:{index:D5}",
            Chunk = chunk,
            Vector = vectors[index],
        }).ToArray();
        progress?.Report(new IngestionProgress(IngestionStage.Completed, 100, $"Created {embedded.Length} chunks"));

        return new IngestionResult
        {
            DocumentId = documentId,
            ContentSha256 = sha256,
            Document = document,
            Chunks = embedded,
            ExtractionDuration = extractionDuration,
            ChunkingDuration = chunkingDuration,
            EmbeddingDuration = embeddingDuration,
        };
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using MemoryStream buffer = stream.CanSeek && stream.Length <= int.MaxValue
            ? new MemoryStream((int)stream.Length)
            : new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string CreateDocumentId(string fileName, string hash)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).Normalize(NormalizationForm.FormKC);
        StringBuilder safe = new(Math.Min(stem.Length, 96));
        foreach (char character in stem)
        {
            if (safe.Length == 96)
            {
                break;
            }

            safe.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
        }

        string normalized = safe.ToString().Trim('-', '.', '_');
        if (normalized.Length == 0)
        {
            normalized = "document";
        }

        return $"{normalized}:{hash[..16]}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
