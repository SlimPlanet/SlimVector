using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class HnswIndex : IVectorIndex
{
    private const int MaximumLevel = 32;
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly int _m;
    private readonly int _efConstruction;
    private readonly int _efSearch;
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly List<Node> _nodes = [];
    private int _entryPoint = -1;
    private int _maximumLevel = -1;
    private int _count;

    public HnswIndex(
        int dimension,
        DistanceMetric metric,
        int m = 16,
        int efConstruction = 200,
        int efSearch = 64)
    {
        DomainValidation.ValidateDimension(dimension);
        VectorIndexConfiguration configuration = new()
        {
            Kind = VectorIndexKind.Hnsw,
            HnswM = m,
            HnswEfConstruction = efConstruction,
            HnswEfSearch = efSearch,
        };
        DomainValidation.ValidateVectorIndex(configuration);
        _dimension = dimension;
        _metric = metric;
        _m = m;
        _efConstruction = efConstruction;
        _efSearch = efSearch;
    }

    public int Count => _count;

    public void Upsert(string id, ReadOnlySpan<float> vector)
    {
        DomainValidation.ValidateDocumentId(id);
        if (vector.Length != _dimension)
        {
            throw new DomainException(
                ErrorCodes.DimensionMismatch,
                $"Expected a vector with dimension {_dimension}, but received {vector.Length} values.");
        }

        if (_positions.ContainsKey(id))
        {
            Remove(id);
        }

        int level = ChooseLevel(id);
        Node node = new(id, vector.ToArray(), level);
        int position = _nodes.Count;
        _nodes.Add(node);
        _positions[id] = position;
        _count++;

        if (_entryPoint < 0)
        {
            _entryPoint = position;
            _maximumLevel = level;
            return;
        }

        int entry = _entryPoint;
        for (int currentLevel = _maximumLevel; currentLevel > level; currentLevel--)
        {
            entry = GreedyClosest(vector, entry, currentLevel);
        }

        int connectionLevel = Math.Min(level, _maximumLevel);
        for (int currentLevel = connectionLevel; currentLevel >= 0; currentLevel--)
        {
            Candidate[] nearest = SearchLayer(vector, entry, _efConstruction, currentLevel);
            int[] neighbors = nearest
                .Where(candidate => !_nodes[candidate.Position].Deleted)
                .Take(MaxConnections(currentLevel))
                .Select(static candidate => candidate.Position)
                .ToArray();
            foreach (int neighbor in neighbors)
            {
                Connect(position, neighbor, currentLevel);
            }

            if (nearest.Length > 0)
            {
                entry = nearest[0].Position;
            }
        }

        if (level > _maximumLevel)
        {
            _entryPoint = position;
            _maximumLevel = level;
        }
    }

    public bool Remove(string id)
    {
        if (!_positions.Remove(id, out int position))
        {
            return false;
        }

        _nodes[position].Deleted = true;
        _count--;
        if (position == _entryPoint)
        {
            SelectEntryPoint();
        }

        return true;
    }

    public IReadOnlyList<RankedResult> Search(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates = null)
    {
        if (query.Length != _dimension)
        {
            throw new DomainException(
                ErrorCodes.DimensionMismatch,
                $"Expected a vector with dimension {_dimension}, but received {query.Length} values.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (_entryPoint < 0 || _count == 0)
        {
            return [];
        }

        if (candidates is not null)
        {
            return SearchCandidates(query, limit, candidates);
        }

        int entry = _entryPoint;
        for (int level = _maximumLevel; level > 0; level--)
        {
            entry = GreedyClosest(query, entry, level);
        }

        int exploration = Math.Max(_efSearch, checked(limit * 4));
        Candidate[] nearest = SearchLayer(query, entry, exploration, 0);
        return nearest
            .Where(candidate =>
            {
                Node node = _nodes[candidate.Position];
                return !node.Deleted && (candidates is null || candidates.Contains(node.Id));
            })
            .Take(limit)
            .Select(candidate => new RankedResult(_nodes[candidate.Position].Id, candidate.Distance))
            .ToArray();
    }

    private RankedResult[] SearchCandidates(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string> candidates)
    {
        PriorityQueue<RankedResult, double> top = new();
        foreach (string id in candidates)
        {
            if (!_positions.TryGetValue(id, out int position) || _nodes[position].Deleted)
            {
                continue;
            }

            double distance = Distance(query, position);
            top.Enqueue(new RankedResult(id, distance), -distance);
            if (top.Count > limit)
            {
                top.Dequeue();
            }
        }

        RankedResult[] results = new RankedResult[top.Count];
        for (int index = results.Length - 1; index >= 0; index--)
        {
            results[index] = top.Dequeue();
        }

        Array.Sort(results, static (left, right) => left.Score.CompareTo(right.Score));
        return results;
    }

    public byte[] Serialize(string documentSignature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentSignature);
        HnswSnapshot snapshot = new()
        {
            FormatVersion = 1,
            Dimension = _dimension,
            Metric = _metric,
            M = _m,
            EfConstruction = _efConstruction,
            EfSearch = _efSearch,
            EntryPoint = _entryPoint,
            MaximumLevel = _maximumLevel,
            DocumentSignature = documentSignature,
            Nodes = _nodes.Select(static node => new HnswNodeSnapshot
            {
                Id = node.Id,
                Vector = node.Vector,
                Level = node.Level,
                Deleted = node.Deleted,
                Neighbors = node.Neighbors.Select(static neighbors => neighbors.ToArray()).ToArray(),
            }).ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public static HnswIndex? Deserialize(
        ReadOnlySpan<byte> data,
        CollectionDefinition definition,
        string expectedDocumentSignature)
    {
        ArgumentNullException.ThrowIfNull(definition);
        HnswSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<HnswSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        if (snapshot is null ||
            snapshot.FormatVersion != 1 ||
            snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric ||
            snapshot.M != definition.VectorIndex.HnswM ||
            snapshot.EfConstruction != definition.VectorIndex.HnswEfConstruction ||
            snapshot.EfSearch != definition.VectorIndex.HnswEfSearch ||
            !string.Equals(snapshot.DocumentSignature, expectedDocumentSignature, StringComparison.Ordinal))
        {
            return null;
        }

        HnswIndex index = new(
            snapshot.Dimension,
            snapshot.Metric,
            snapshot.M,
            snapshot.EfConstruction,
            snapshot.EfSearch);
        foreach (HnswNodeSnapshot stored in snapshot.Nodes)
        {
            if (stored.Vector.Length != snapshot.Dimension || stored.Neighbors.Length != stored.Level + 1)
            {
                return null;
            }

            Node node = new(stored.Id, stored.Vector, stored.Level) { Deleted = stored.Deleted };
            for (int level = 0; level < stored.Neighbors.Length; level++)
            {
                node.Neighbors[level].AddRange(stored.Neighbors[level]);
            }

            index._nodes.Add(node);
        }

        if (index._nodes.Any(node => node.Neighbors.Any(neighbors => neighbors.Any(position => position < 0 || position >= index._nodes.Count))))
        {
            return null;
        }

        for (int position = 0; position < index._nodes.Count; position++)
        {
            Node node = index._nodes[position];
            if (!node.Deleted)
            {
                if (!index._positions.TryAdd(node.Id, position))
                {
                    return null;
                }

                index._count++;
            }
        }

        if (snapshot.EntryPoint >= index._nodes.Count || snapshot.EntryPoint < -1)
        {
            return null;
        }

        index._entryPoint = snapshot.EntryPoint;
        index._maximumLevel = snapshot.MaximumLevel;
        return index;
    }

    public static string ComputeDocumentSignature(IEnumerable<DocumentRecord> documents)
    {
        StringBuilder signature = new();
        foreach (DocumentRecord document in documents.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            signature.Append(document.Id).Append('\0').Append(document.Version).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private int GreedyClosest(ReadOnlySpan<float> query, int entry, int level)
    {
        int current = entry;
        double currentDistance = Distance(query, current);
        bool changed;
        do
        {
            changed = false;
            foreach (int neighbor in GetNeighbors(current, level))
            {
                double distance = Distance(query, neighbor);
                if (distance < currentDistance)
                {
                    current = neighbor;
                    currentDistance = distance;
                    changed = true;
                }
            }
        }
        while (changed);

        return current;
    }

    private Candidate[] SearchLayer(ReadOnlySpan<float> query, int entry, int ef, int level)
    {
        HashSet<int> visited = [entry];
        PriorityQueue<Candidate, double> candidates = new();
        PriorityQueue<Candidate, double> nearest = new();
        double entryDistance = Distance(query, entry);
        Candidate entryCandidate = new(entry, entryDistance);
        candidates.Enqueue(entryCandidate, entryDistance);
        nearest.Enqueue(entryCandidate, -entryDistance);

        while (candidates.TryDequeue(out Candidate current, out double currentDistance))
        {
            nearest.TryPeek(out _, out double worstPriority);
            double worstDistance = -worstPriority;
            if (nearest.Count >= ef && currentDistance > worstDistance)
            {
                break;
            }

            foreach (int neighbor in GetNeighbors(current.Position, level))
            {
                if (!visited.Add(neighbor))
                {
                    continue;
                }

                double distance = Distance(query, neighbor);
                nearest.TryPeek(out _, out worstPriority);
                worstDistance = -worstPriority;
                if (nearest.Count < ef || distance < worstDistance)
                {
                    Candidate candidate = new(neighbor, distance);
                    candidates.Enqueue(candidate, distance);
                    nearest.Enqueue(candidate, -distance);
                    if (nearest.Count > ef)
                    {
                        nearest.Dequeue();
                    }
                }
            }
        }

        Candidate[] result = new Candidate[nearest.Count];
        for (int index = result.Length - 1; index >= 0; index--)
        {
            result[index] = nearest.Dequeue();
        }

        Array.Sort(result, static (left, right) => left.Distance.CompareTo(right.Distance));
        return result;
    }

    private void Connect(int left, int right, int level)
    {
        AddAndPrune(left, right, level);
        AddAndPrune(right, left, level);
    }

    private void AddAndPrune(int source, int neighbor, int level)
    {
        List<int> neighbors = _nodes[source].Neighbors[level];
        if (!neighbors.Contains(neighbor))
        {
            neighbors.Add(neighbor);
        }

        int maximum = MaxConnections(level);
        if (neighbors.Count > maximum)
        {
            float[] sourceVector = _nodes[source].Vector;
            neighbors.Sort((left, right) =>
                DistanceFunctions.Calculate(sourceVector, _nodes[left].Vector, _metric)
                    .CompareTo(DistanceFunctions.Calculate(sourceVector, _nodes[right].Vector, _metric)));
            neighbors.RemoveRange(maximum, neighbors.Count - maximum);
        }
    }

    private List<int> GetNeighbors(int position, int level) =>
        level <= _nodes[position].Level ? _nodes[position].Neighbors[level] : [];

    private double Distance(ReadOnlySpan<float> query, int position) =>
        DistanceFunctions.Calculate(query, _nodes[position].Vector, _metric);

    private int MaxConnections(int level) => level == 0 ? _m * 2 : _m;

    private int ChooseLevel(string id)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        ulong bits = BitConverter.ToUInt64(hash);
        double unit = (bits + 1.0) / (ulong.MaxValue + 2.0);
        int level = (int)Math.Floor(-Math.Log(unit) / Math.Log(_m));
        return Math.Min(level, MaximumLevel);
    }

    private void SelectEntryPoint()
    {
        _entryPoint = -1;
        _maximumLevel = -1;
        for (int position = 0; position < _nodes.Count; position++)
        {
            Node node = _nodes[position];
            if (!node.Deleted && node.Level > _maximumLevel)
            {
                _entryPoint = position;
                _maximumLevel = node.Level;
            }
        }
    }

    private readonly record struct Candidate(int Position, double Distance);

    private sealed class Node
    {
        public Node(string id, float[] vector, int level)
        {
            Id = id;
            Vector = vector;
            Level = level;
            Neighbors = Enumerable.Range(0, level + 1).Select(static _ => new List<int>()).ToArray();
        }

        public string Id { get; }

        public float[] Vector { get; }

        public int Level { get; }

        public List<int>[] Neighbors { get; }

        public bool Deleted { get; set; }
    }
}

[MemoryPackable]
internal sealed partial class HnswSnapshot
{
    public int FormatVersion { get; set; }

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public int M { get; set; }

    public int EfConstruction { get; set; }

    public int EfSearch { get; set; }

    public int EntryPoint { get; set; }

    public int MaximumLevel { get; set; }

    public string DocumentSignature { get; set; } = string.Empty;

    public HnswNodeSnapshot[] Nodes { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class HnswNodeSnapshot
{
    public string Id { get; set; } = string.Empty;

    public float[] Vector { get; set; } = [];

    public int Level { get; set; }

    public bool Deleted { get; set; }

    public int[][] Neighbors { get; set; } = [];
}
