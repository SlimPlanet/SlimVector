namespace SlimVector.Storage;

public sealed class StorageMetrics
{
    private long _bytesRead;
    private long _bytesWritten;
    private long _durableFlushes;

    public void RecordRead(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Add(ref _bytesRead, bytes);
    }

    public void RecordWrite(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Add(ref _bytesWritten, bytes);
    }

    public void RecordDurableFlush() => Interlocked.Increment(ref _durableFlushes);

    public StorageMetricsSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _bytesRead),
        Interlocked.Read(ref _bytesWritten),
        Interlocked.Read(ref _durableFlushes));
}

public sealed record StorageMetricsSnapshot(long BytesRead, long BytesWritten, long DurableFlushes);
