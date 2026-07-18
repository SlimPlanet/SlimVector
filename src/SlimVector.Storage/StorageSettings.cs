namespace SlimVector.Storage;

public sealed record StorageSettings
{
    public required string Path { get; init; }

    public bool FlushToDisk { get; init; } = true;

    public int MaximumSegmentsBeforeCompaction { get; init; } = 64;
}
