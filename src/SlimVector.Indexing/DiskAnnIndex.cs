using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.Win32.SafeHandles;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class DiskAnnIndex : IVectorIndex, IBulkVectorIndex, IDisposable
{
    private const int HeaderSize = 32;
    private const int ConstructionSampleLimit = 128;
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
    private readonly ConcurrentBag<DiskAnnSearchContext> _searchContexts = [];
    private FileStream? _generationStream;
    private DiskPageCache? _pageCache;
    private string[] _ids = [];
    private long _generation;
    private long? _previousGeneration;
    private int _entryPoint;
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
        DiskAnnSearchContext context = RentSearchContext(limit);
        try
        {
            if (_ids.Length > 0)
            {
                if (candidates is not null && candidates.Count <= Math.Max(_searchListSize, limit * 8))
                {
                    foreach (string id in candidates)
                    {
                        if (_positions.TryGetValue(id, out int position) && !_deleted.Contains(id))
                        {
                            ReadVector(position, context.Vector, context.RecordBytes);
                            AddResult(query, id, context.Vector, limit, context.Results);
                        }
                    }
                }
                else
                {
                    Traverse(query, limit, candidates, context);
                }
            }

            foreach ((string id, float[] vector) in _delta)
            {
                if (candidates is null || candidates.Contains(id))
                {
                    AddResult(query, id, vector, limit, context.Results);
                }
            }

            return VectorIndexUtilities.Drain(context.Results);
        }
        finally
        {
            ReturnSearchContext(context);
        }
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

    private void Traverse(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates,
        DiskAnnSearchContext context)
    {
        ReadVector(_entryPoint, context.Vector, context.RecordBytes);
        float entryDistance = DistanceFunctions.Calculate(query, context.Vector, _metric);
        context.MarkScheduled(_entryPoint);
        context.Pending.Enqueue(_entryPoint, entryDistance);
        int expanded = 0;
        while (context.Pending.Count > 0 && expanded < _searchListSize)
        {
            int width = Math.Min(_beamWidth, context.Pending.Count);
            for (int beam = 0; beam < width && expanded < _searchListSize; beam++)
            {
                int position = context.Pending.Dequeue();
                expanded++;
                int neighborCount = ReadRecord(position, context.Vector, context.Neighbors, context.RecordBytes);
                string id = _ids[position];
                if (!_deleted.Contains(id) && (candidates is null || candidates.Contains(id)))
                {
                    AddResult(query, id, context.Vector, limit, context.Results);
                }

                for (int neighborIndex = 0; neighborIndex < neighborCount; neighborIndex++)
                {
                    int neighbor = context.Neighbors[neighborIndex];
                    if (context.MarkScheduled(neighbor))
                    {
                        ReadVector(neighbor, context.NeighborVector, context.RecordBytes);
                        float neighborDistance = DistanceFunctions.Calculate(query, context.NeighborVector, _metric);
                        context.Pending.Enqueue(neighbor, neighborDistance);
                    }
                }
            }
        }
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
        (List<int>[] graph, int entryPoint) = BuildVamanaGraph(vectors);
        string dataPath = DataPath(nextGeneration);
        string idsPath = IdsPath(nextGeneration);
        string temporaryData = dataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string temporaryIds = idsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteData(temporaryData, vectors, graph, entryPoint);
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

    private (List<int>[] Graph, int EntryPoint) BuildVamanaGraph(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        List<int>[] graph = Enumerable.Range(0, vectors.Count).Select(_ => new List<int>(_maxDegree)).ToArray();
        if (vectors.Count == 0)
        {
            return (graph, 0);
        }

        int entryPoint = FindApproximateMedoid(vectors);
        for (int node = 0; node < vectors.Count; node++)
        {
            if (node > 0)
            {
                graph[node].Add(node - 1);
                graph[node - 1].Add(node);
            }
        }

        int constructionListSize = Math.Max(_maxDegree * 2, Math.Min(_searchListSize, 256));
        int[] order = Enumerable.Range(0, vectors.Count)
            .OrderBy(static node => unchecked((uint)node * 2_654_435_761U))
            .ToArray();
        for (int pass = 0; pass < 2; pass++)
        {
            IEnumerable<int> traversalOrder = pass == 0 ? order : order.Reverse();
            foreach (int node in traversalOrder)
            {
                HashSet<int> candidates = GreedySearchInMemory(
                    vectors,
                    graph,
                    entryPoint,
                    vectors[node].Vector,
                    constructionListSize);
                candidates.Remove(node);
                candidates.UnionWith(graph[node]);
                graph[node] = RobustPrune(node, candidates, vectors);
                foreach (int neighbor in graph[node].ToArray())
                {
                    if (graph[neighbor].Contains(node))
                    {
                        continue;
                    }

                    HashSet<int> reverseCandidates = [.. graph[neighbor], node];
                    graph[neighbor] = RobustPrune(neighbor, reverseCandidates, vectors);
                }
            }
        }

        return (graph, entryPoint);
    }

    private HashSet<int> GreedySearchInMemory(
        IReadOnlyList<(string Id, float[] Vector)> vectors,
        List<int>[] graph,
        int entryPoint,
        ReadOnlySpan<float> query,
        int searchListSize)
    {
        HashSet<int> visited = [];
        PriorityQueue<int, double> pending = new();
        pending.Enqueue(entryPoint, DistanceFunctions.Calculate(query, vectors[entryPoint].Vector, _metric));
        while (pending.Count > 0 && visited.Count < searchListSize)
        {
            int node = pending.Dequeue();
            if (!visited.Add(node))
            {
                continue;
            }

            foreach (int neighbor in graph[node])
            {
                if (!visited.Contains(neighbor))
                {
                    pending.Enqueue(neighbor, DistanceFunctions.Calculate(query, vectors[neighbor].Vector, _metric));
                }
            }
        }

        return visited;
    }

    private List<int> RobustPrune(
        int node,
        IEnumerable<int> candidates,
        IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        (int Index, float Distance)[] ordered = candidates
            .Where(candidate => candidate != node)
            .Distinct()
            .Select(candidate => (
                Index: candidate,
                Distance: DistanceFunctions.Calculate(vectors[node].Vector, vectors[candidate].Vector, _metric)))
            .OrderBy(static candidate => candidate.Distance)
            .ThenBy(static candidate => candidate.Index)
            .ToArray();
        List<int> selected = new(_maxDegree);
        foreach ((int candidate, float distance) in ordered)
        {
            bool occluded = selected.Any(existing =>
                1.2F * DistanceFunctions.Calculate(vectors[candidate].Vector, vectors[existing].Vector, _metric) <= distance);
            if (!occluded || selected.Count < Math.Min(2, _maxDegree))
            {
                selected.Add(candidate);
                if (selected.Count == _maxDegree)
                {
                    break;
                }
            }
        }

        return selected;
    }

    private int FindApproximateMedoid(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        int sampleCount = Math.Min(ConstructionSampleLimit, vectors.Count);
        int stride = Math.Max(1, vectors.Count / sampleCount);
        int[] sample = Enumerable.Range(0, sampleCount)
            .Select(index => Math.Min(vectors.Count - 1, index * stride))
            .Distinct()
            .ToArray();
        return sample
            .Select(candidate => (
                Candidate: candidate,
                Total: sample.Sum(other =>
                    (double)DistanceFunctions.Calculate(vectors[candidate].Vector, vectors[other].Vector, _metric))))
            .OrderBy(static item => item.Total)
            .ThenBy(static item => item.Candidate)
            .First().Candidate;
    }

    private void WriteData(
        string path,
        IReadOnlyList<(string Id, float[] Vector)> vectors,
        List<int>[] graph,
        int entryPoint)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _dimension);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], vectors.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], _maxDegree);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], RecordSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], entryPoint);
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
        Span<byte> header = stackalloc byte[HeaderSize];
        _pageCache.Read(0, header);
        _entryPoint = ids.Length == 0 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        if (ids.Length > 0 && (_entryPoint < 0 || _entryPoint >= ids.Length))
        {
            throw new InvalidDataException("The DiskANN entry point is outside the generation.");
        }
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
            expectedCount > 0 && (BinaryPrimitives.ReadInt32LittleEndian(header[24..]) < 0 ||
                BinaryPrimitives.ReadInt32LittleEndian(header[24..]) >= expectedCount) ||
            stream.Length != HeaderSize + (long)expectedCount * RecordSize)
        {
            throw new InvalidDataException("The DiskANN generation artifact is invalid.");
        }
    }

    private float[] ReadVector(int position)
    {
        float[] vector = new float[_dimension];
        byte[] bytes = new byte[_dimension * sizeof(float)];
        ReadVector(position, vector, bytes);
        return vector;
    }

    private void ReadVector(int position, Span<float> destination, Span<byte> bytes)
    {
        Span<byte> vectorBytes = bytes[..checked(_dimension * sizeof(float))];
        _pageCache!.Read(RecordOffset(position), vectorBytes);
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            destination[coordinate] = BinaryPrimitives.ReadSingleLittleEndian(vectorBytes[(coordinate * sizeof(float))..]);
        }
    }

    private int ReadRecord(int position, Span<float> vector, Span<int> neighbors, Span<byte> bytes)
    {
        Span<byte> record = bytes[..RecordSize];
        _pageCache!.Read(RecordOffset(position), record);
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            vector[coordinate] = BinaryPrimitives.ReadSingleLittleEndian(record[(coordinate * sizeof(float))..]);
        }

        int neighborsOffset = _dimension * sizeof(float);
        int count = BinaryPrimitives.ReadInt32LittleEndian(record[neighborsOffset..]);
        if (count < 0 || count > _maxDegree)
        {
            throw new InvalidDataException("The DiskANN neighbor record is invalid.");
        }

        for (int index = 0; index < count; index++)
        {
            neighbors[index] = BinaryPrimitives.ReadInt32LittleEndian(
                record[(neighborsOffset + sizeof(int) + index * sizeof(int))..]);
            if (neighbors[index] < 0 || neighbors[index] >= _ids.Length)
            {
                throw new InvalidDataException("The DiskANN neighbor position is invalid.");
            }
        }

        return count;
    }

    private DiskAnnSearchContext RentSearchContext(int limit)
    {
        if (!_searchContexts.TryTake(out DiskAnnSearchContext? context))
        {
            context = new DiskAnnSearchContext();
        }

        context.Reset(_ids.Length, _dimension, _maxDegree, RecordSize, Math.Max(limit, _searchListSize));
        return context;
    }

    private void ReturnSearchContext(DiskAnnSearchContext context)
    {
        context.Pending.Clear();
        context.Results.Clear();
        _searchContexts.Add(context);
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

internal sealed class DiskAnnSearchContext
{
    private int[] _scheduled = [];
    private int _epoch;

    public PriorityQueue<int, float> Pending { get; } = new();

    public PriorityQueue<RankedResult, double> Results { get; } = new();

    public float[] Vector { get; private set; } = [];

    public float[] NeighborVector { get; private set; } = [];

    public int[] Neighbors { get; private set; } = [];

    public byte[] RecordBytes { get; private set; } = [];

    public void Reset(int vectorCount, int dimension, int maxDegree, int recordSize, int queueCapacity)
    {
        if (_scheduled.Length < vectorCount)
        {
            _scheduled = new int[vectorCount];
            _epoch = 0;
        }

        if (Vector.Length < dimension)
        {
            Vector = new float[dimension];
            NeighborVector = new float[dimension];
        }

        if (Neighbors.Length < maxDegree)
        {
            Neighbors = new int[maxDegree];
        }

        if (RecordBytes.Length < recordSize)
        {
            RecordBytes = new byte[recordSize];
        }

        _epoch++;
        if (_epoch == int.MaxValue)
        {
            Array.Clear(_scheduled);
            _epoch = 1;
        }

        Pending.Clear();
        Results.Clear();
        Pending.EnsureCapacity(queueCapacity);
        Results.EnsureCapacity(queueCapacity);
    }

    public bool MarkScheduled(int position)
    {
        if (_scheduled[position] == _epoch)
        {
            return false;
        }

        _scheduled[position] = _epoch;
        return true;
    }
}

internal sealed class DiskPageCache : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly int _pageSize;
    private readonly object _sync = new();
    private readonly Dictionary<long, int> _pageIndexes;
    private readonly PageSlot[] _slots;
    private int _count;
    private int _nextVictim;
    private long _hits;
    private long _misses;

    public DiskPageCache(SafeFileHandle handle, int pageSize, int capacity)
    {
        _handle = handle;
        _pageSize = pageSize;
        _pageIndexes = new Dictionary<long, int>(capacity);
        _slots = Enumerable.Range(0, capacity)
            .Select(_ => new PageSlot(pageSize))
            .ToArray();
    }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public void Read(long offset, Span<byte> destination)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            long absolute = offset + copied;
            long pageNumber = absolute / _pageSize;
            int pageOffset = (int)(absolute % _pageSize);
            lock (_sync)
            {
                PageSlot page = GetPage(pageNumber);
                int count = Math.Min(destination.Length - copied, page.ValidLength - pageOffset);
                if (count <= 0)
                {
                    throw new EndOfStreamException("The DiskANN generation ended unexpectedly.");
                }

                page.Bytes.AsSpan(pageOffset, count).CopyTo(destination[copied..]);
                copied += count;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _pageIndexes.Clear();
            _count = 0;
        }
    }

    private PageSlot GetPage(long number)
    {
        if (_pageIndexes.TryGetValue(number, out int existingIndex))
        {
            _hits++;
            PageSlot existing = _slots[existingIndex];
            existing.Referenced = true;
            return existing;
        }

        _misses++;
        int slotIndex;
        if (_count < _slots.Length)
        {
            slotIndex = _count++;
        }
        else
        {
            while (_slots[_nextVictim].Referenced)
            {
                _slots[_nextVictim].Referenced = false;
                _nextVictim = (_nextVictim + 1) % _slots.Length;
            }

            slotIndex = _nextVictim;
            _nextVictim = (_nextVictim + 1) % _slots.Length;
            _pageIndexes.Remove(_slots[slotIndex].Number);
        }

        PageSlot slot = _slots[slotIndex];
        slot.Number = number;
        slot.ValidLength = RandomAccess.Read(_handle, slot.Bytes, number * _pageSize);
        slot.Referenced = true;
        _pageIndexes.Add(number, slotIndex);
        return slot;
    }

    private sealed class PageSlot
    {
        public PageSlot(int pageSize) => Bytes = new byte[pageSize];

        public long Number { get; set; } = -1;

        public byte[] Bytes { get; }

        public int ValidLength { get; set; }

        public bool Referenced { get; set; }
    }
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
