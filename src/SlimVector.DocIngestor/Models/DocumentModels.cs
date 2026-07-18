namespace SlimVector.DocIngestor.Models;

public enum DocumentFormat
{
    Pdf,
    Word,
    PowerPoint,
    PlainText,
}

public sealed class DocumentSource : IAsyncDisposable
{
    private readonly bool _ownsStream;

    public DocumentSource(
        Stream stream,
        string fileName,
        string? contentType = null,
        long? length = null,
        bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Stream = stream;
        FileName = Path.GetFileName(fileName);
        ContentType = contentType;
        Length = length ?? (stream.CanSeek ? stream.Length : null);
        _ownsStream = ownsStream;
    }

    public Stream Stream { get; }

    public string FileName { get; }

    public string? ContentType { get; }

    public long? Length { get; }

    public static DocumentSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream = File.OpenRead(path);
        return new DocumentSource(stream, Path.GetFileName(path), length: stream.Length, ownsStream: true);
    }

    public ValueTask DisposeAsync() => _ownsStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed record ExtractedSection
{
    public required int Sequence { get; init; }

    public required string Location { get; init; }

    public string? Heading { get; init; }

    public required string Text { get; init; }

    public int? PageNumber { get; init; }

    public int? SlideNumber { get; init; }
}

public sealed record ExtractedDocument
{
    public required string FileName { get; init; }

    public required DocumentFormat Format { get; init; }

    public string? Title { get; init; }

    public required IReadOnlyList<ExtractedSection> Sections { get; init; }

    public required IReadOnlyDictionary<string, string> Properties { get; init; }

    public int CharacterCount => Sections.Sum(static section => section.Text.Length);
}

public enum ChunkingStrategy
{
    Recursive,
    Paragraph,
    Sentence,
}

public sealed record ChunkingOptions
{
    public ChunkingStrategy Strategy { get; init; } = ChunkingStrategy.Recursive;

    public int TargetTokens { get; init; } = 90;

    public int MaximumTokens { get; init; } = 120;

    public int OverlapTokens { get; init; } = 18;

    public int MinimumChunkTokens { get; init; } = 8;

    public void Validate()
    {
        if (TargetTokens < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(TargetTokens), "TargetTokens must be at least 8.");
        }

        if (MaximumTokens < TargetTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTokens), "MaximumTokens must be greater than or equal to TargetTokens.");
        }

        if (OverlapTokens < 0 || OverlapTokens >= TargetTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(OverlapTokens), "OverlapTokens must be non-negative and smaller than TargetTokens.");
        }

        if (MinimumChunkTokens < 1 || MinimumChunkTokens > TargetTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumChunkTokens), "MinimumChunkTokens must be between 1 and TargetTokens.");
        }
    }
}

public sealed record TextChunk
{
    public required int Sequence { get; init; }

    public required string Text { get; init; }

    public required int EstimatedTokens { get; init; }

    public required IReadOnlyList<int> SectionSequences { get; init; }

    public required IReadOnlyList<string> Locations { get; init; }

    public string? Heading { get; init; }
}

public sealed record EmbeddedChunk
{
    public required string Id { get; init; }

    public required TextChunk Chunk { get; init; }

    public required float[] Vector { get; init; }
}

public sealed record IngestionOptions
{
    public ChunkingOptions Chunking { get; init; } = new();

    public bool GenerateEmbeddings { get; init; } = true;
}

public sealed record IngestionResult
{
    public required string DocumentId { get; init; }

    public required string ContentSha256 { get; init; }

    public required ExtractedDocument Document { get; init; }

    public required IReadOnlyList<EmbeddedChunk> Chunks { get; init; }

    public required TimeSpan ExtractionDuration { get; init; }

    public required TimeSpan ChunkingDuration { get; init; }

    public required TimeSpan EmbeddingDuration { get; init; }
}

public enum IngestionStage
{
    Reading,
    Extracting,
    Chunking,
    PreparingModel,
    Embedding,
    Completed,
}

public sealed record IngestionProgress(IngestionStage Stage, double Percent, string Message);
