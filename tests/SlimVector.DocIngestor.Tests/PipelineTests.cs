using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Chunking;
using SlimVector.DocIngestor.Extractors;
using SlimVector.DocIngestor.Models;
using SlimVector.DocIngestor.Pipeline;

namespace SlimVector.DocIngestor.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task PipelineExtractsChunksAndEmbedsWithStableDocumentIds()
    {
        FakeEmbeddingGenerator embeddings = new();
        DocumentIngestionPipeline pipeline = new(
            new DocumentExtractorRouter([new PlainTextExtractor()]),
            new RecursiveTextChunker(new ApproximateTokenCounter()),
            embeddings);
        byte[] content = System.Text.Encoding.UTF8.GetBytes(
            "SlimVector stores vectors locally. Hybrid search combines semantic similarity with BM25 relevance.");
        IngestionOptions options = new()
        {
            Chunking = new ChunkingOptions { TargetTokens = 16, MaximumTokens = 24, OverlapTokens = 3, MinimumChunkTokens = 2 },
        };

        IngestionResult first;
        await using (MemoryStream stream = new(content))
        {
            first = await pipeline.IngestAsync(
                new DocumentSource(stream, "guide.md"),
                options,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        IngestionResult second;
        await using (MemoryStream stream = new(content))
        {
            second = await pipeline.IngestAsync(
                new DocumentSource(stream, "guide.md"),
                options,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.Equal(64, first.ContentSha256.Length);
        Assert.NotEmpty(first.Chunks);
        Assert.All(first.Chunks, chunk => Assert.Equal(3, chunk.Vector.Length));
        Assert.All(first.Chunks, chunk => Assert.StartsWith(first.DocumentId, chunk.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PipelineReportsEmptySelectableText()
    {
        DocumentIngestionPipeline pipeline = new(
            new DocumentExtractorRouter([new PlainTextExtractor()]),
            new RecursiveTextChunker(new ApproximateTokenCounter()),
            new FakeEmbeddingGenerator());
        await using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes("  \n  "));

        DocumentIngestionException exception = await Assert.ThrowsAsync<DocumentIngestionException>(async () =>
            await pipeline.IngestAsync(
                new DocumentSource(stream, "empty.txt"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("document_contains_no_text", exception.Code);
    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        public string ModelId => "fake";

        public int Dimension => 3;

        public ValueTask<EmbeddingModelStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EmbeddingModelStatus
            {
                ModelId = ModelId,
                Revision = "test",
                Variant = "fake.onnx",
                Directory = "/tmp/fake",
                Dimension = Dimension,
                MaximumSequenceLength = 128,
                IsReady = true,
            });

        public ValueTask EnsureReadyAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<float[]>> GenerateAsync(
            IReadOnlyList<string> texts,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<float[]> vectors = texts.Select(static text => new[] { (float)text.Length, 1f, 0.5f }).ToArray();
            progress?.Report(100);
            return ValueTask.FromResult(vectors);
        }
    }
}
