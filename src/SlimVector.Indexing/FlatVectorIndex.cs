using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class FlatVectorIndex : IVectorIndex
{
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
    private readonly List<string?> _ids = [];
    private float[] _vectors = [];
    private int _count;

    public FlatVectorIndex(int dimension, DistanceMetric metric)
    {
        DomainValidation.ValidateDimension(dimension);
        _dimension = dimension;
        _metric = metric;
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

        if (_positions.TryGetValue(id, out int existing))
        {
            vector.CopyTo(_vectors.AsSpan(existing * _dimension, _dimension));
            return;
        }

        int position = FindFreePosition();
        EnsureCapacity(position + 1);
        _ids[position] = id;
        _positions.Add(id, position);
        vector.CopyTo(_vectors.AsSpan(position * _dimension, _dimension));
        _count++;
    }

    public bool Remove(string id)
    {
        if (!_positions.Remove(id, out int position))
        {
            return false;
        }

        _ids[position] = null;
        _vectors.AsSpan(position * _dimension, _dimension).Clear();
        _count--;
        return true;
    }

    public IReadOnlyList<RankedResult> Search(ReadOnlySpan<float> query, int limit, IReadOnlySet<string>? candidates = null)
    {
        if (query.Length != _dimension)
        {
            throw new DomainException(
                ErrorCodes.DimensionMismatch,
                $"Expected a vector with dimension {_dimension}, but received {query.Length} values.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        PriorityQueue<RankedResult, double> top = new();
        for (int position = 0; position < _ids.Count; position++)
        {
            string? id = _ids[position];
            if (id is null || candidates is not null && !candidates.Contains(id))
            {
                continue;
            }

            ReadOnlySpan<float> vector = _vectors.AsSpan(position * _dimension, _dimension);
            double distance = DistanceFunctions.Calculate(query, vector, _metric);
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

    internal byte[] Serialize()
    {
        FlatVectorSnapshot snapshot = new()
        {
            Dimension = _dimension,
            Metric = _metric,
            Entries = _ids
                .Select((id, position) => id is null
                    ? null
                    : new FlatVectorEntry
                    {
                        Id = id,
                        Vector = _vectors.AsSpan(position * _dimension, _dimension).ToArray(),
                    })
                .OfType<FlatVectorEntry>()
                .ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    internal static FlatVectorIndex? Deserialize(ReadOnlySpan<byte> data, CollectionDefinition definition)
    {
        FlatVectorSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<FlatVectorSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric || snapshot.Entries is null ||
            snapshot.Entries.Any(entry => entry is null || entry.Vector is null || entry.Vector.Length != snapshot.Dimension) ||
            snapshot.Entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Entries.Length)
        {
            return null;
        }

        FlatVectorIndex index = new(snapshot.Dimension, snapshot.Metric);
        foreach (FlatVectorEntry entry in snapshot.Entries)
        {
            index.Upsert(entry.Id, entry.Vector);
        }

        return index;
    }

    private int FindFreePosition()
    {
        for (int index = 0; index < _ids.Count; index++)
        {
            if (_ids[index] is null)
            {
                return index;
            }
        }

        _ids.Add(null);
        return _ids.Count - 1;
    }

    private void EnsureCapacity(int requiredPositions)
    {
        int requiredLength = checked(requiredPositions * _dimension);
        if (_vectors.Length >= requiredLength)
        {
            return;
        }

        int currentPositions = _vectors.Length / _dimension;
        int newPositions = Math.Max(requiredPositions, Math.Max(4, currentPositions * 2));
        Array.Resize(ref _vectors, checked(newPositions * _dimension));
    }
}

[MemoryPackable]
internal sealed partial class FlatVectorSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public FlatVectorEntry[] Entries { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class FlatVectorEntry
{
    public string Id { get; set; } = string.Empty;

    public float[] Vector { get; set; } = [];
}
