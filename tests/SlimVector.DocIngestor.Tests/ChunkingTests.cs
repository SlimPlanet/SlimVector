using SlimVector.DocIngestor.Chunking;
using SlimVector.DocIngestor.Models;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.DocIngestor.Tests;

public sealed class ChunkingTests
{
    [Fact]
    public void RecursiveChunkerProducesBoundedOrderedChunksWithOverlap()
    {
        ApproximateTokenCounter counter = new();
        RecursiveTextChunker chunker = new(counter);
        ExtractedDocument document = CreateDocument(string.Join(' ', Enumerable.Range(1, 180).Select(index => $"concept{index}")));
        ChunkingOptions options = new()
        {
            TargetTokens = 30,
            MaximumTokens = 40,
            OverlapTokens = 6,
            MinimumChunkTokens = 4,
        };

        IReadOnlyList<TextChunk> chunks = chunker.Chunk(document, options);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.InRange(chunk.EstimatedTokens, 1, options.MaximumTokens));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static chunk => chunk.Sequence));
        string previousLastWord = chunks[0].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Contains(previousLastWord, chunks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkingIsDeterministicAndCarriesSourceLocations()
    {
        RecursiveTextChunker chunker = new(new ApproximateTokenCounter());
        ExtractedDocument document = new()
        {
            FileName = "slides.pptx",
            Format = IngestedDocumentFormat.PowerPoint,
            Sections =
            [
                new ExtractedSection { Sequence = 0, Location = "Slide 1", Heading = "Intro", Text = "Vector search finds semantically similar content." },
                new ExtractedSection { Sequence = 1, Location = "Slide 2", Heading = "Hybrid", Text = "BM25 and vector ranks are fused for hybrid search." },
            ],
            Properties = new Dictionary<string, string>(),
        };
        ChunkingOptions options = new() { TargetTokens = 24, MaximumTokens = 32, OverlapTokens = 4, MinimumChunkTokens = 2 };

        IReadOnlyList<TextChunk> first = chunker.Chunk(document, options);
        IReadOnlyList<TextChunk> second = chunker.Chunk(document, options);

        Assert.Equal(first.Select(static chunk => chunk.Text), second.Select(static chunk => chunk.Text));
        Assert.Equal(first.SelectMany(static chunk => chunk.Locations), second.SelectMany(static chunk => chunk.Locations));
        Assert.Contains(first.SelectMany(static chunk => chunk.Locations), location => location == "Slide 1");
        Assert.Contains(first.SelectMany(static chunk => chunk.Locations), location => location == "Slide 2");
    }

    [Theory]
    [InlineData(7, 12, 2, 1)]
    [InlineData(20, 10, 2, 1)]
    [InlineData(20, 24, 20, 1)]
    [InlineData(20, 24, 2, 21)]
    public void InvalidOptionsAreRejected(int target, int maximum, int overlap, int minimum)
    {
        ChunkingOptions options = new()
        {
            TargetTokens = target,
            MaximumTokens = maximum,
            OverlapTokens = overlap,
            MinimumChunkTokens = minimum,
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    private static ExtractedDocument CreateDocument(string text) => new()
    {
        FileName = "long.txt",
        Format = IngestedDocumentFormat.PlainText,
        Sections = [new ExtractedSection { Sequence = 0, Location = "Document", Text = text }],
        Properties = new Dictionary<string, string>(),
    };
}
