using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.Win32.SafeHandles;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class DiskAnnIndex : IVectorIndex, IBulkVectorIndex, IDisposable
{
    private const int HeaderSize = 32;
    private const int CandidatePoolLimit = 128;
    private static readonly byte[] Magic = "SLVDA001"u8.ToArray();
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly int _maxDegree;
    private readonly int _searchListSize;
    private readonly int _beamWidth;
    private readonly int _deltaThreshold;
    private readonly int _retainedGenerations;
    private readonly string _artifactDirectory;
    private readonly bool _ownsDirectory;
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]> _delta = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private FileStream? _generationStream;
    private DiskPageCache? _pageCache;
    private string[] _ids = [];
    private long _generation;
    private long? _previousGeneration;
    private bool _disposed;

    public DiskAnnIndex(
        int dimension,
        DistanceMetric metric,
        int maxDegree,
        int searchListSize,
        int beamWidth,
        int deltaThreshold,
        string? artifactDirectory = null,
        int pageSize = 4_096,
        int cachePages = 256,
        int retainedGenerations = 2)
    {
        DomainValidation.ValidateDimension(dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(searchListSize, maxDegree);
        ArgumentOutOfRangeException.ThrowIfLessThan(beamWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(deltaThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 512);
        ArgumentOutOfRangeException.ThrowIfLessThan(cachePages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedGenerations, 2);
        _dimension = dimension;
        _metric = metric;
        _maxDegree = maxDegree;
        _searchListSize = searchListSize;
        _beamWidth = beamWidth;
        _deltaThreshold = deltaThreshold;
        _ownsDirectory = string.IsNullOrWhiteSpace(artifactDirectory);
        _artifactDirectory = artifactDirectory is null
            ? Path.Combine(Path.GetTempPath(), "slimvector-diskann", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(artifactDirectory);
        PageSize = pageSize;
        CachePages = cachePages;
        _retainedGenerations = retainedGenerations;
        Directory.CreateDirectory(_artifactDirectory);
    }

    public int Count => checked(_ids.Length - _deleted.Count + _delta.Count);

    public long Generation => _generation;

    public long? PreviousGeneration => _previousGeneration;

    public int DeltaCount => _delta.Count + _deleted.Count;

    public int PageSize { get; }

    public int CachePages { get; }

    public long CacheHits => _pageCache?.Hits ?? 0;

    public long CacheMisses => _pageCache?.Misses ?? 0;

    public void Build(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vectors);
        List<(string Id, float[] Vector)> owned = new(vectors.Count);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach ((string id, float[] vector) in vectors.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            DomainValidation.ValidateDocumentId(id);
            VectorIndexUtilities.ValidateVector(vector, _dimension);
            if (!ids.Add(id))
            {
                throw new ArgumentException($"Duplicate vector id '{id}'.", nameof(vectors));
            }

            owned.Add((id, (float[])vector.Clone()));
        }

        CommitGeneration(owned);
    }

    public void Upsert(string id, ReadOnlySpan<float> vector)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DomainValidation.ValidateDocumentId(id);
        VectorIndexUtilities.ValidateVector(vector, _dimension);
        if (_positions.ContainsKey(id))
        {
            _deleted.Add(id);
        }

        _delta[id] = vector.ToArray();
        if (DeltaCount >= _deltaThreshold)
        {
            CommitDelta();
        }
    }

    public bool Remove(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool removedDelta = _delta.Remove(id);
        bool removedBase = _positions.ContainsKey(id) && _deleted.Add(id);
        if (DeltaCount >= _deltaThreshold)
        {
            CommitDelta();
        }

        return removedDelta || removedBase;
    }

    public IReadOnlyList<RankedResult> Search(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VectorIndexUtilities.ValidateVector(query, _dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        PriorityQueue<RankedResult, double> top = new();
        if (_ids.Length > 0)
        {
            if (candidates is not null && candidates.Count <= Math.Max(_searchListSize, limit * 8))
            {
                foreach (string id in candidates)
                {
                    if (_positions.TryGetValue(id, out int position) && !_deleted.Contains(id))
                    {
                        AddResult(query, id, ReadVector(position), limit, top);
                    }
                }
            }
            else
            {
                foreach (int position in Traverse(query))
                {
                    string id = _ids[position];
                    if (!_deleted.Contains(id) && (candidates is null || candidates.Contains(id)))
                    {
                        AddResult(query, id, ReadVector(position), limit, top);
                    }
                }
            }
        }

        foreach ((string id, float[] vector) in _delta)
        {
            if (candidates is null || candidates.Contains(id))
            {
                AddResult(query, id, vector, limit, top);
            }
        }

        return VectorIndexUtilities.Drain(top);
    }

    public bool Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_previousGeneration.HasValue)
        {
            return false;
        }

        long current = _generation;
        long target = _previousGeneration.Value;
        string targetData = DataPath(target);
        string targetIds = IdsPath(target);
        if (!File.Exists(targetData) || !File.Exists(targetIds))
        {
            return false;
        }

        string[] ids = ReadIds(targetIds);
        OpenGeneration(target, current, ids);
        WriteManifest(target, current, targetData);
        _delta.Clear();
        _deleted.Clear();
        return true;
    }

    public byte[] Serialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CommitDelta();
        DiskAnnSnapshot snapshot = new()
        {
            Dimension = _dimension,
            Metric = _metric,
            MaxDegree = _maxDegree,
            SearchListSize = _searchListSize,
            BeamWidth = _beamWidth,
            DeltaThreshold = _deltaThreshold,
            PageSize = PageSize,
            CachePages = CachePages,
            RetainedGenerations = _retainedGenerations,
            Generation = _generation,
            Ids = _ids,
            Artifact = _generation == 0 ? [] : File.ReadAllBytes(DataPath(_generation)),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public static DiskAnnIndex? Deserialize(
        ReadOnlySpan<byte> data,
        CollectionDefinition definition,
        string? artifactDirectory = null)
    {
        DiskAnnSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<DiskAnnSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        VectorIndexConfiguration configuration = definition.VectorIndex;
        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric || snapshot.MaxDegree != configuration.DiskAnnMaxDegree ||
            snapshot.SearchListSize != configuration.DiskAnnSearchListSize ||
            snapshot.BeamWidth != configuration.DiskAnnBeamWidth ||
            snapshot.DeltaThreshold != configuration.DiskAnnDeltaThreshold || snapshot.PageSize < 512 ||
            snapshot.CachePages < 1 || snapshot.Generation < 0 || snapshot.Ids is null || snapshot.Artifact is null ||
            snapshot.RetainedGenerations != configuration.DiskAnnRetainedGenerations ||
            snapshot.Ids.Distinct(StringComparer.Ordinal).Count() != snapshot.Ids.Length)
        {
            return null;
        }

        DiskAnnIndex index = new(
            snapshot.Dimension,
            snapshot.Metric,
            snapshot.MaxDegree,
            snapshot.SearchListSize,
            snapshot.BeamWidth,
            snapshot.DeltaThreshold,
            artifactDirectory,
            snapshot.PageSize,
            snapshot.CachePages,
            snapshot.RetainedGenerations);
        if (snapshot.Ids.Length == 0)
        {
            return index;
        }

        try
        {
            index.RestoreGeneration(snapshot.Generation, snapshot.Ids, snapshot.Artifact);
            return index;
        }
        catch (InvalidDataException)
        {
            index.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pageCache?.Dispose();
        _generationStream?.Dispose();
        if (_ownsDirectory && Directory.Exists(_artifactDirectory))
        {
            Directory.Delete(_artifactDirectory, recursive: true);
        }
    }

    private IEnumerable<int> Traverse(ReadOnlySpan<float> query)
    {
        HashSet<int> visited = [];
        PriorityQueue<int, double> pending = new();
        PriorityQueue<RankedResult, double> best = new();
        float entryDistance = DistanceFunctions.Calculate(query, ReadVector(0), _metric);
        pending.Enqueue(0, entryDistance);
        int expanded = 0;
        while (pending.Count > 0 && expanded < _searchListSize)
        {
            int width = Math.Min(_beamWidth, pending.Count);
            for (int beam = 0; beam < width && expanded < _searchListSize; beam++)
            {
                int position = pending.Dequeue();
                if (!visited.Add(position))
                {
                    continue;
                }

                expanded++;
                float[] vector = ReadVector(position);
                float distance = DistanceFunctions.Calculate(query, vector, _metric);
                best.Enqueue(new RankedResult(position.ToString(System.Globalization.CultureInfo.InvariantCulture), distance), -distance);
                if (best.Count > _searchListSize)
                {
                    best.Dequeue();
                }

                foreach (int neighbor in ReadNeighbors(position))
                {
                    if (!visited.Contains(neighbor))
                    {
                        float neighborDistance = DistanceFunctions.Calculate(query, ReadVector(neighbor), _metric);
                        pending.Enqueue(neighbor, neighborDistance);
                    }
                }
            }
        }

        return VectorIndexUtilities.Drain(best)
            .Select(static result => int.Parse(result.Id, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddResult(
        ReadOnlySpan<float> query,
        string id,
        ReadOnlySpan<float> vector,
        int limit,
        PriorityQueue<RankedResult, double> top,
        DistanceMetric metric = default)
    {
        float distance = DistanceFunctions.Calculate(query, vector, metric);
        top.Enqueue(new RankedResult(id, distance), -distance);
        if (top.Count > limit)
        {
            top.Dequeue();
        }
    }

    private void AddResult(
        ReadOnlySpan<float> query,
        string id,
        ReadOnlySpan<float> vector,
        int limit,
        PriorityQueue<RankedResult, double> top) =>
        AddResult(query, id, vector, limit, top, _metric);

    private void CommitDelta()
    {
        if (_delta.Count == 0 && _deleted.Count == 0)
        {
            return;
        }

        List<(string Id, float[] Vector)> merged = new(Count);
        for (int position = 0; position < _ids.Length; position++)
        {
            string id = _ids[position];
            if (!_deleted.Contains(id) && !_delta.ContainsKey(id))
            {
                merged.Add((id, ReadVector(position)));
            }
        }

        merged.AddRange(_delta.Select(static pair => (pair.Key, pair.Value)));
        merged.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        CommitGeneration(merged);
    }

    private void CommitGeneration(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        long nextGeneration = checked(_generation + 1);
        List<int>[] graph = BuildVamanaGraph(vectors);
        string dataPath = DataPath(nextGeneration);
        string idsPath = IdsPath(nextGeneration);
        string temporaryData = dataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string temporaryIds = idsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteData(temporaryData, vectors, graph);
            WriteIds(temporaryIds, vectors.Select(static item => item.Id));
            File.Move(temporaryData, dataPath, overwrite: true);
            File.Move(temporaryIds, idsPath, overwrite: true);
            WriteManifest(nextGeneration, _generation == 0 ? null : _generation, dataPath);
        }
        finally
        {
            if (File.Exists(temporaryData))
            {
                File.Delete(temporaryData);
            }

            if (File.Exists(temporaryIds))
            {
                File.Delete(temporaryIds);
            }
        }

        OpenGeneration(
            nextGeneration,
            _generation == 0 ? null : _generation,
            vectors.Select(static item => item.Id).ToArray());
        _delta.Clear();
        _deleted.Clear();
        DeleteObsoleteGenerations();
    }

    private List<int>[] BuildVamanaGraph(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        List<int>[] graph = Enumerable.Range(0, vectors.Count).Select(_ => new List<int>(_maxDegree)).ToArray();
        for (int node = 0; node < vectors.Count; node++)
        {
            IEnumerable<int> pool = CandidatePool(node, vectors.Count);
            (int Index, float Distance)[] ordered = pool
                .Where(candidate => candidate != node)
                .Select(candidate => (
                    Index: candidate,
                    Distance: DistanceFunctions.Calculate(vectors[node].Vector, vectors[candidate].Vector, _metric)))
                .OrderBy(static item => item.Distance)
                .ThenBy(static item => item.Index)
                .ToArray();
            foreach ((int candidate, float distance) in ordered)
            {
                bool occluded = graph[node].Any(selected =>
                    1.2F * DistanceFunctions.Calculate(vectors[candidate].Vector, vectors[selected].Vector, _metric) <= distance);
                if (!occluded || graph[node].Count < Math.Min(2, _maxDegree))
                {
                    graph[node].Add(candidate);
                    if (graph[node].Count == _maxDegree)
                    {
                        break;
                    }
                }
            }

            if (node > 0 && !graph[node].Contains(node - 1))
            {
                if (graph[node].Count == _maxDegree)
                {
                    graph[node][^1] = node - 1;
                }
                else
                {
                    graph[node].Add(node - 1);
                }
            }
        }

        for (int node = 1; node < graph.Length; node++)
        {
            if (!graph[node - 1].Contains(node))
            {
                if (graph[node - 1].Count == _maxDegree)
                {
                    graph[node - 1][^1] = node;
                }
                else
                {
                    graph[node - 1].Add(node);
                }
            }
        }

        return graph;
    }

    private static IEnumerable<int> CandidatePool(int node, int count)
    {
        if (count <= CandidatePoolLimit)
        {
            return Enumerable.Range(0, count);
        }

        int stride = Math.Max(1, count / CandidatePoolLimit);
        int offset = node % stride;
        return Enumerable.Range(0, CandidatePoolLimit)
            .Select(index => (offset + index * stride) % count)
            .Append(Math.Max(0, node - 1))
            .Append(Math.Min(count - 1, node + 1))
            .Distinct();
    }

    private void WriteData(
        string path,
        IReadOnlyList<(string Id, float[] Vector)> vectors,
        List<int>[] graph)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _dimension);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], vectors.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _maxDegree);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], RecordSize);
        stream.Write(header);
        byte[] record = new byte[RecordSize];
        for (int index = 0; index < vectors.Count; index++)
        {
            Span<byte> span = record;
            for (int coordinate = 0; coordinate < _dimension; coordinate++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[(coordinate * sizeof(float))..], vectors[index].Vector[coordinate]);
            }

            int neighborsOffset = _dimension * sizeof(float);
            BinaryPrimitives.WriteInt32LittleEndian(span[neighborsOffset..], graph[index].Count);
            for (int neighbor = 0; neighbor < _maxDegree; neighbor++)
            {
                int value = neighbor < graph[index].Count ? graph[index][neighbor] : -1;
                BinaryPrimitives.WriteInt32LittleEndian(span[(neighborsOffset + sizeof(int) + neighbor * sizeof(int))..], value);
            }

            stream.Write(record);
            Array.Clear(record);
        }

        stream.Flush(flushToDisk: true);
    }

    private void RestoreGeneration(long generation, string[] ids, byte[] artifact)
    {
        string path = DataPath(generation);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, artifact);
        File.Move(temporary, path, overwrite: true);
        WriteIds(IdsPath(generation), ids);
        ValidateData(path, ids.Length);
        WriteManifest(generation, null, path);
        OpenGeneration(generation, null, ids);
    }

    private void OpenGeneration(long generation, long? previous, string[] ids)
    {
        _pageCache?.Dispose();
        _generationStream?.Dispose();
        _generationStream = new FileStream(
            DataPath(generation),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            PageSize,
            FileOptions.RandomAccess);
        _pageCache = new DiskPageCache(_generationStream.SafeFileHandle, PageSize, CachePages);
        _generation = generation;
        _previousGeneration = previous;
        _ids = ids;
        _positions.Clear();
        for (int index = 0; index < ids.Length; index++)
        {
            _positions.Add(ids[index], index);
        }
    }

    private void ValidateData(string path, int expectedCount)
    {
        using FileStream stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.Read(header) != HeaderSize || !header[..Magic.Length].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != _dimension ||
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != expectedCount ||
            BinaryPrimitives.ReadInt32LittleEndian(header[16..]) != _maxDegree ||
            BinaryPrimitives.ReadInt32LittleEndian(header[20..]) != RecordSize ||
            stream.Length != HeaderSize + (long)expectedCount * RecordSize)
        {
            throw new InvalidDataException("The DiskANN generation artifact is invalid.");
        }
    }

    private float[] ReadVector(int position)
    {
        byte[] bytes = new byte[_dimension * sizeof(float)];
        _pageCache!.Read(RecordOffset(position), bytes);
        float[] vector = new float[_dimension];
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            vector[coordinate] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(coordinate * sizeof(float)));
        }

        return vector;
    }

    private int[] ReadNeighbors(int position)
    {
        byte[] bytes = new byte[sizeof(int) + _maxDegree * sizeof(int)];
        _pageCache!.Read(RecordOffset(position) + _dimension * sizeof(float), bytes);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || count > _maxDegree)
        {
            throw new InvalidDataException("The DiskANN neighbor record is invalid.");
        }

        int[] neighbors = new int[count];
        for (int index = 0; index < count; index++)
        {
            neighbors[index] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(sizeof(int) + index * sizeof(int)));
            if (neighbors[index] < 0 || neighbors[index] >= _ids.Length)
            {
                throw new InvalidDataException("The DiskANN neighbor position is invalid.");
            }
        }

        return neighbors;
    }

    private void WriteManifest(long active, long? previous, string dataPath)
    {
        string checksum = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dataPath)));
        string contents = FormattableString.Invariant($"1\n{active}\n{previous?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}\n{checksum}\n");
        string path = Path.Combine(_artifactDirectory, "manifest");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, contents, Encoding.ASCII);
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteIds(string path, IEnumerable<string> ids)
    {
        string[] encoded = ids.Select(id => Convert.ToBase64String(Encoding.UTF8.GetBytes(id))).ToArray();
        File.WriteAllLines(path, encoded, Encoding.ASCII);
    }

    private static string[] ReadIds(string path) => File.ReadAllLines(path, Encoding.ASCII)
        .Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value)))
        .ToArray();

    private void DeleteObsoleteGenerations()
    {
        HashSet<long> retained = [_generation];
        if (_previousGeneration.HasValue)
        {
            retained.Add(_previousGeneration.Value);
        }

        foreach (long generation in Directory.EnumerateFiles(_artifactDirectory, "generation-*.dat")
                     .Select(ParseGeneration)
                     .Where(static value => value >= 0)
                     .OrderDescending()
                     .Take(_retainedGenerations))
        {
            retained.Add(generation);
        }

        foreach (string file in Directory.EnumerateFiles(_artifactDirectory, "generation-*.*"))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (long.TryParse(stem.AsSpan("generation-".Length), out long generation) && !retained.Contains(generation))
            {
                File.Delete(file);
            }
        }
    }

    private static long ParseGeneration(string path)
    {
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(path);
        return long.TryParse(stem["generation-".Length..], out long generation) ? generation : -1;
    }

    private int RecordSize => checked(_dimension * sizeof(float) + sizeof(int) + _maxDegree * sizeof(int));

    private long RecordOffset(int position) => checked(HeaderSize + (long)position * RecordSize);

    private string DataPath(long generation) => Path.Combine(_artifactDirectory, $"generation-{generation}.dat");

    private string IdsPath(long generation) => Path.Combine(_artifactDirectory, $"generation-{generation}.ids");
}

internal sealed class DiskPageCache : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _pageSize;
    private readonly int _capacity;
    private readonly Dictionary<long, LinkedListNode<Page>> _pages = [];
    private readonly LinkedList<Page> _lru = [];

    public DiskPageCache(SafeFileHandle handle, int pageSize, int capacity)
    {
        _handle = handle;
        _pageSize = pageSize;
        _capacity = capacity;
    }

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public void Read(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            long absolute = offset + copied;
            long pageNumber = absolute / _pageSize;
            int pageOffset = (int)(absolute % _pageSize);
            Page page = GetPage(pageNumber);
            int count = Math.Min(destination.Length - copied, page.ValidLength - pageOffset);
            if (count <= 0)
            {
                throw new EndOfStreamException("The DiskANN generation ended unexpectedly.");
            }

            page.Bytes.AsSpan(pageOffset, count).CopyTo(destination[copied..]);
            copied += count;
        }
    }

    public void Dispose()
    {
        _pages.Clear();
        _lru.Clear();
    }

    private Page GetPage(long number)
    {
        if (_pages.TryGetValue(number, out LinkedListNode<Page>? existing))
        {
            Hits++;
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            return existing.Value;
        }

        Misses++;
        byte[] bytes = new byte[_pageSize];
        int length = RandomAccess.Read(_handle, bytes, number * _pageSize);
        Page page = new(number, bytes, length);
        LinkedListNode<Page> node = _lru.AddFirst(page);
        _pages.Add(number, node);
        if (_pages.Count > _capacity)
        {
            LinkedListNode<Page> last = _lru.Last!;
            _pages.Remove(last.Value.Number);
            _lru.RemoveLast();
        }

        return page;
    }

    private sealed record Page(long Number, byte[] Bytes, int ValidLength);
}

[MemoryPackable]
internal sealed partial class DiskAnnSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public int MaxDegree { get; set; }

    public int SearchListSize { get; set; }

    public int BeamWidth { get; set; }

    public int DeltaThreshold { get; set; }

    public int PageSize { get; set; }

    public int CachePages { get; set; }

    public int RetainedGenerations { get; set; } = 2;

    public long Generation { get; set; }

    public string[] Ids { get; set; } = [];

    public byte[] Artifact { get; set; } = [];
}
