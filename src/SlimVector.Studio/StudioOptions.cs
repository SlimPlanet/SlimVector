using SlimVector.DocIngestor.Models;

namespace SlimVector.Studio;

public sealed class StudioOptions
{
    public const string SectionName = "Studio";

    public const int MaximumChunkTokens = 1200;

    public long MaximumUploadBytes { get; set; } = 128L * 1024 * 1024;

    public string DefaultCollection { get; set; } = "documents";

    public string? ModelDirectory { get; set; }

    public bool AutoDownloadModel { get; set; } = true;

    public StudioChunkingOptions Chunking { get; set; } = new();

    public void Validate()
    {
        if (MaximumUploadBytes < 1024 * 1024)
        {
            throw new InvalidOperationException("Studio:MaximumUploadBytes doit être d’au moins 1 Mo.");
        }

        ArgumentNullException.ThrowIfNull(Chunking);
        new ChunkingOptions
        {
            TargetTokens = Chunking.TargetTokens,
            MaximumTokens = Chunking.MaximumTokens,
            OverlapTokens = Chunking.OverlapTokens,
        }.Validate();
        if (Chunking.MaximumTokens > MaximumChunkTokens)
        {
            throw new InvalidOperationException(
                $"Studio:Chunking:MaximumTokens ne peut pas dépasser {MaximumChunkTokens}.");
        }
    }
}

public sealed class StudioChunkingOptions
{
    public int TargetTokens { get; set; } = 500;

    public int MaximumTokens { get; set; } = 600;

    public int OverlapTokens { get; set; } = 100;
}
