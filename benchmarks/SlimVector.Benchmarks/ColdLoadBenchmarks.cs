using BenchmarkDotNet.Attributes;
using SlimVector.Domain;
using SlimVector.Storage;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class ColdLoadBenchmarks : IDisposable
{
    private string _path = string.Empty;
    private CollectionDefinition _collection = null!;
    private FileSystemStorageEngine _storage = null!;

    [Params(1_000, 10_000)]
    public int DocumentCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), "SlimVector.Benchmarks", Guid.NewGuid().ToString("N"));
        _storage = CreateStorage();
        await _storage.InitializeAsync();
        _collection = CollectionDefinition.Create("cold-load", 64, DistanceMetric.Cosine);
        await _storage.CreateCollectionAsync(_collection);
        foreach (StorageOperation[] chunk in Enumerable.Range(0, DocumentCount)
                     .Select(index => StorageOperation.Upsert(new DocumentRecord
                     {
                         Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         Text = "cold load benchmark document",
                         Vector = Enumerable.Repeat((float)index, 64).ToArray(),
                         Metadata = [],
                         Version = 1,
                     }))
                     .Chunk(500))
        {
            await _storage.AppendAsync(_collection.Id, chunk);
        }

        _storage.Dispose();
    }

    [IterationSetup]
    public void OpenStorage()
    {
        _storage = CreateStorage();
        _storage.InitializeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public ValueTask<IReadOnlyDictionary<string, DocumentRecord>> LoadDocuments() =>
        _storage.LoadDocumentsAsync(_collection.Id);

    [IterationCleanup]
    public void CloseStorage() => _storage.Dispose();

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }

    public void Dispose()
    {
        _storage?.Dispose();
        GC.SuppressFinalize(this);
    }

    private FileSystemStorageEngine CreateStorage() => new(new StorageSettings
    {
        Path = _path,
        FlushToDisk = false,
        MaximumSegmentsBeforeCompaction = 64,
    });
}
