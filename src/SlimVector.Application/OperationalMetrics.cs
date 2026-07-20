using SlimVector.Domain;

namespace SlimVector.Application;

public sealed class OperationalMetrics
{
    private long _searches;
    private long _searchFailures;
    private long _slowSearches;
    private long _searchMicroseconds;
    private long _vectorSearches;
    private long _textSearches;
    private long _hybridSearches;
    private long _metadataSearches;
    private long _fanOutSearches;
    private long _fanOutPartitions;
    private long _indexLoads;
    private long _indexLoadFailures;
    private long _indexLoadMicroseconds;
    private long _indexedDocumentsLoaded;
    private long _flatIndexLoads;
    private long _hnswIndexLoads;
    private long _ivfFlatIndexLoads;
    private long _ivfPqIndexLoads;
    private long _diskAnnIndexLoads;
    private long _hnswCacheHits;
    private long _hnswCacheMisses;
    private long _indexMigrations;
    private long _indexMigrationFailures;
    private long _indexMigrationMicroseconds;
    private long _lastIndexMigrationRecallMillionths;

    public void RecordSearch(SearchMode mode, TimeSpan elapsed, bool succeeded, bool slow)
    {
        Interlocked.Increment(ref _searches);
        Interlocked.Add(ref _searchMicroseconds, ToMicroseconds(elapsed));
        if (!succeeded)
        {
            Interlocked.Increment(ref _searchFailures);
        }

        if (slow)
        {
            Interlocked.Increment(ref _slowSearches);
        }

        switch (mode)
        {
            case SearchMode.Vector:
                Interlocked.Increment(ref _vectorSearches);
                break;
            case SearchMode.Text:
                Interlocked.Increment(ref _textSearches);
                break;
            case SearchMode.Hybrid:
                Interlocked.Increment(ref _hybridSearches);
                break;
            case SearchMode.Metadata:
                Interlocked.Increment(ref _metadataSearches);
                break;
            default:
                break;
        }
    }

    public void RecordIndexLoad(
        VectorIndexKind kind,
        int documentCount,
        TimeSpan elapsed,
        bool succeeded,
        bool hnswCacheAvailable)
    {
        Interlocked.Increment(ref _indexLoads);
        Interlocked.Add(ref _indexLoadMicroseconds, ToMicroseconds(elapsed));
        if (!succeeded)
        {
            Interlocked.Increment(ref _indexLoadFailures);
            return;
        }

        Interlocked.Add(ref _indexedDocumentsLoaded, documentCount);
        switch (kind)
        {
            case VectorIndexKind.Hnsw:
                Interlocked.Increment(ref _hnswIndexLoads);
                if (hnswCacheAvailable)
                {
                    Interlocked.Increment(ref _hnswCacheHits);
                }
                else
                {
                    Interlocked.Increment(ref _hnswCacheMisses);
                }

                break;
            case VectorIndexKind.IvfFlat:
                Interlocked.Increment(ref _ivfFlatIndexLoads);
                break;
            case VectorIndexKind.IvfPq:
                Interlocked.Increment(ref _ivfPqIndexLoads);
                break;
            case VectorIndexKind.DiskAnn:
                Interlocked.Increment(ref _diskAnnIndexLoads);
                break;
            case VectorIndexKind.Auto:
            case VectorIndexKind.Flat:
            default:
                Interlocked.Increment(ref _flatIndexLoads);
                break;
        }
    }

    public void RecordSearchFanOut(int partitions)
    {
        if (partitions < 2)
        {
            return;
        }

        Interlocked.Increment(ref _fanOutSearches);
        Interlocked.Add(ref _fanOutPartitions, partitions);
    }

    public OperationalMetricsSnapshot GetSnapshot() => new()
    {
        Searches = Volatile.Read(ref _searches),
        SearchFailures = Volatile.Read(ref _searchFailures),
        SlowSearches = Volatile.Read(ref _slowSearches),
        SearchMicroseconds = Volatile.Read(ref _searchMicroseconds),
        VectorSearches = Volatile.Read(ref _vectorSearches),
        TextSearches = Volatile.Read(ref _textSearches),
        HybridSearches = Volatile.Read(ref _hybridSearches),
        MetadataSearches = Volatile.Read(ref _metadataSearches),
        FanOutSearches = Volatile.Read(ref _fanOutSearches),
        FanOutPartitions = Volatile.Read(ref _fanOutPartitions),
        IndexLoads = Volatile.Read(ref _indexLoads),
        IndexLoadFailures = Volatile.Read(ref _indexLoadFailures),
        IndexLoadMicroseconds = Volatile.Read(ref _indexLoadMicroseconds),
        IndexedDocumentsLoaded = Volatile.Read(ref _indexedDocumentsLoaded),
        FlatIndexLoads = Volatile.Read(ref _flatIndexLoads),
        HnswIndexLoads = Volatile.Read(ref _hnswIndexLoads),
        IvfFlatIndexLoads = Volatile.Read(ref _ivfFlatIndexLoads),
        IvfPqIndexLoads = Volatile.Read(ref _ivfPqIndexLoads),
        DiskAnnIndexLoads = Volatile.Read(ref _diskAnnIndexLoads),
        HnswCacheHits = Volatile.Read(ref _hnswCacheHits),
        HnswCacheMisses = Volatile.Read(ref _hnswCacheMisses),
        IndexMigrations = Volatile.Read(ref _indexMigrations),
        IndexMigrationFailures = Volatile.Read(ref _indexMigrationFailures),
        IndexMigrationMicroseconds = Volatile.Read(ref _indexMigrationMicroseconds),
        LastIndexMigrationRecall = Volatile.Read(ref _lastIndexMigrationRecallMillionths) / 1_000_000D,
    };

    public void RecordIndexMigration(TimeSpan elapsed, bool succeeded, double recall)
    {
        Interlocked.Increment(ref _indexMigrations);
        Interlocked.Add(ref _indexMigrationMicroseconds, ToMicroseconds(elapsed));
        if (!succeeded)
        {
            Interlocked.Increment(ref _indexMigrationFailures);
        }

        Interlocked.Exchange(
            ref _lastIndexMigrationRecallMillionths,
            (long)(Math.Clamp(recall, 0, 1) * 1_000_000));
    }

    private static long ToMicroseconds(TimeSpan elapsed) =>
        (long)Math.Min(long.MaxValue, Math.Max(0, elapsed.TotalMicroseconds));
}

public sealed record OperationalMetricsSnapshot
{
    public required long Searches { get; init; }

    public required long SearchFailures { get; init; }

    public required long SlowSearches { get; init; }

    public required long SearchMicroseconds { get; init; }

    public required long VectorSearches { get; init; }

    public required long TextSearches { get; init; }

    public required long HybridSearches { get; init; }

    public required long MetadataSearches { get; init; }

    public required long FanOutSearches { get; init; }

    public required long FanOutPartitions { get; init; }

    public required long IndexLoads { get; init; }

    public required long IndexLoadFailures { get; init; }

    public required long IndexLoadMicroseconds { get; init; }

    public required long IndexedDocumentsLoaded { get; init; }

    public required long FlatIndexLoads { get; init; }

    public required long HnswIndexLoads { get; init; }

    public required long IvfFlatIndexLoads { get; init; }

    public required long IvfPqIndexLoads { get; init; }

    public required long DiskAnnIndexLoads { get; init; }

    public required long HnswCacheHits { get; init; }

    public required long HnswCacheMisses { get; init; }

    public required long IndexMigrations { get; init; }

    public required long IndexMigrationFailures { get; init; }

    public required long IndexMigrationMicroseconds { get; init; }

    public required double LastIndexMigrationRecall { get; init; }
}
