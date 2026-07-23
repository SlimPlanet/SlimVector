using BenchmarkDotNet.Attributes;
using SlimVector.DocIngestor.Chunking;
using SlimVector.DocIngestor.Models;
using IngestedDocumentFormat = SlimVector.DocIngestor.Models.DocumentFormat;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class DocumentChunkingBenchmarks
{
    private readonly RecursiveTextChunker _chunker = new(new ApproximateTokenCounter());
    private ExtractedDocument _document = null!;

    [GlobalSetup]
    public void Setup()
    {
        string paragraph = string.Join(
            ' ',
            Enumerable.Range(0, 20_000).Select(static index => $"concept{index % 100}"));
        _document = new ExtractedDocument
        {
            FileName = "benchmark.txt",
            Format = IngestedDocumentFormat.PlainText,
            Sections =
            [
                new ExtractedSection
                {
                    Sequence = 0,
                    Text = paragraph,
                    Location = "document",
                },
            ],
            Properties = new Dictionary<string, string>(),
        };
    }

    [Benchmark(Baseline = true)]
    public IReadOnlyList<TextChunk> LibraryDefaults90To120() =>
        _chunker.Chunk(_document, new ChunkingOptions());

    [Benchmark]
    public IReadOnlyList<TextChunk> StudioDefaults500To600() =>
        _chunker.Chunk(_document, new ChunkingOptions
        {
            TargetTokens = 500,
            MaximumTokens = 600,
            OverlapTokens = 100,
        });
}
