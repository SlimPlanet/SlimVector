namespace SlimVector.DocIngestor.Embeddings;

public sealed class HuggingFaceEmbeddingOptions
{
    public const string DefaultModelId = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2";
    public const string DefaultRevision = "e8f8c211226b894fcb81acc59f3b34ba3efd5f42";

    public string ModelId { get; set; } = DefaultModelId;

    public string Revision { get; set; } = DefaultRevision;

    public string ModelDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SlimVector",
        "models",
        "paraphrase-multilingual-MiniLM-L12-v2");

    public bool AutoDownload { get; set; } = true;

    public int Dimension { get; set; } = 384;

    public int MaximumSequenceLength { get; set; } = 128;

    public int BatchSize { get; set; } = 8;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelDirectory);
        if (Dimension < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Dimension));
        }

        if (MaximumSequenceLength < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSequenceLength));
        }

        if (BatchSize is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        }
    }
}
