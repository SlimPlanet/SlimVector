namespace SlimVector.DocIngestor.Models;

public sealed record EmbeddingModelStatus
{
    public required string ModelId { get; init; }

    public required string Revision { get; init; }

    public required string Variant { get; init; }

    public required string Directory { get; init; }

    public required int Dimension { get; init; }

    public required int MaximumSequenceLength { get; init; }

    public required bool IsReady { get; init; }
}

public sealed record ModelDownloadProgress(
    string FileName,
    long BytesReceived,
    long? TotalBytes,
    double Percent);
