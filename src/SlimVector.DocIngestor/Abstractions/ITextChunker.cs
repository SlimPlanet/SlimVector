using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Abstractions;

public interface ITokenCounter
{
    int CountTokens(string text);
}

public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(ExtractedDocument document, ChunkingOptions options);
}
