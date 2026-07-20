using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class IvfFlatIndex : IVectorIndex, IBulkVectorIndex
{
    public const int MinimumTrainingPointsPerCentroid = 39;
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly int _listCount;
    private readonly int _probeCount;
    private readonly int _trainingIterations;
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _assignments = new(StringComparer.Ordinal);
    private float[][] _centroids = [];
    private HashSet<string>[] _postings = [];
    private int _trainedVectorCount;

    public IvfFlatIndex(
        int dimension,
        DistanceMetric metric,
        int listCount,
        int probeCount,
        int trainingIterations)
    {
        DomainValidation.ValidateDimension(dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(listCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(probeCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probeCount, listCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(trainingIterations, 1);
        _dimension = dimension;
        _metric = metric;
        _listCount = listCount;
        _probeCount = probeCount;
        _trainingIterations = trainingIterations;
    }

    public int Count => _vectors.Count;

    public int TrainedListCount => _centroids.Length;

    public int MinimumTrainingVectorCount => checked(_listCount * MinimumTrainingPointsPerCentroid);

    public IvfTrainingState TrainingState { get; private set; } = IvfTrainingState.Untrained;

    public void Build(IReadOnlyList<(string Id, float[] Vector)> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        _vectors.Clear();
        foreach ((string id, float[] vector) in vectors)
        {
            DomainValidation.ValidateDocumentId(id);
            VectorIndexUtilities.ValidateVector(vector, _dimension);
            if (!_vectors.TryAdd(id, (float[])vector.Clone()))
            {
                throw new ArgumentException($"Duplicate vector id '{id}'.", nameof(vectors));
            }
        }

        TrainIfReady();
    }

    public void Upsert(string id, ReadOnlySpan<float> vector)
    {
        DomainValidation.ValidateDocumentId(id);
        VectorIndexUtilities.ValidateVector(vector, _dimension);
        if (_assignments.Remove(id, out int oldList) && oldList < _postings.Length)
        {
            _postings[oldList].Remove(id);
        }

        _vectors[id] = vector.ToArray();
        if (_centroids.Length == 0)
        {
            TrainIfReady();
            return;
        }

        Assign(id, PrepareVector(vector));
        if (_vectors.Count >= checked(Math.Max(1, _trainedVectorCount) * 2))
        {
            TrainingState = IvfTrainingState.NeedsRetrain;
        }
    }

    public bool Remove(string id)
    {
        if (!_vectors.Remove(id))
        {
            return false;
        }

        if (_assignments.Remove(id, out int list) && list < _postings.Length)
        {
            _postings[list].Remove(id);
        }

        return true;
    }

    public IReadOnlyList<RankedResult> Search(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates = null)
    {
        VectorIndexUtilities.ValidateVector(query, _dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (_centroids.Length == 0)
        {
            return SearchExact(query, limit, candidates);
        }

        IEnumerable<string> candidateIds;
        if (candidates is not null && candidates.Count <= Math.Max(limit * 8, _centroids.Length))
        {
            candidateIds = candidates;
        }
        else
        {
            int probes = Math.Min(_probeCount, _centroids.Length);
            float[] queryVector = PrepareVector(query);
            int[] lists = Enumerable.Range(0, _centroids.Length)
                .Select(index => (Index: index, Distance: VectorIndexUtilities.SquaredEuclidean(queryVector, _centroids[index])))
                .OrderBy(static item => item.Distance)
                .ThenBy(static item => item.Index)
                .Take(probes)
                .Select(static item => item.Index)
                .ToArray();
            candidateIds = lists.SelectMany(index => _postings[index]);
        }

        PriorityQueue<RankedResult, double> top = new();
        foreach (string id in candidateIds.Distinct(StringComparer.Ordinal))
        {
            if (!_vectors.TryGetValue(id, out float[]? vector) || candidates is not null && !candidates.Contains(id))
            {
                continue;
            }

            float distance = DistanceFunctions.Calculate(query, vector, _metric);
            top.Enqueue(new RankedResult(id, distance), -distance);
            if (top.Count > limit)
            {
                top.Dequeue();
            }
        }

        return VectorIndexUtilities.Drain(top);
    }

    public byte[] Serialize()
    {
        IvfFlatSnapshot snapshot = new()
        {
            Dimension = _dimension,
            Metric = _metric,
            ListCount = _listCount,
            ProbeCount = _probeCount,
            TrainingIterations = _trainingIterations,
            TrainingState = TrainingState,
            TrainedVectorCount = _trainedVectorCount,
            Centroids = _centroids,
            Entries = _vectors.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new IvfFlatEntry
                {
                    Id = pair.Key,
                    Vector = pair.Value,
                    List = _assignments.GetValueOrDefault(pair.Key, -1),
                })
                .ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public static IvfFlatIndex? Deserialize(ReadOnlySpan<byte> data, CollectionDefinition definition)
    {
        IvfFlatSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<IvfFlatSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        VectorIndexConfiguration configuration = definition.VectorIndex;
        if (snapshot is null || snapshot.FormatVersion is not (1 or 2) || snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric || snapshot.ListCount != configuration.IvfListCount ||
            snapshot.ProbeCount != configuration.IvfProbeCount ||
            snapshot.TrainingIterations != configuration.IvfTrainingIterations || snapshot.Centroids is null ||
            snapshot.Entries is null || snapshot.Centroids.Any(centroid => centroid?.Length != snapshot.Dimension) ||
            snapshot.Entries.Any(entry => entry is null || entry.Vector?.Length != snapshot.Dimension ||
                snapshot.Centroids.Length > 0 && (entry.List < 0 || entry.List >= snapshot.Centroids.Length)) ||
            snapshot.Entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Entries.Length)
        {
            return null;
        }

        IvfFlatIndex index = new(
            snapshot.Dimension,
            snapshot.Metric,
            snapshot.ListCount,
            snapshot.ProbeCount,
            snapshot.TrainingIterations)
        {
            _centroids = snapshot.Centroids,
            _trainedVectorCount = snapshot.FormatVersion == 1
                ? snapshot.Entries.Length
                : snapshot.TrainedVectorCount,
            _postings = Enumerable.Range(0, snapshot.Centroids.Length)
                .Select(_ => new HashSet<string>(StringComparer.Ordinal))
                .ToArray(),
        };
        foreach (IvfFlatEntry entry in snapshot.Entries)
        {
            index._vectors.Add(entry.Id, entry.Vector);
            if (snapshot.Centroids.Length > 0)
            {
                index._assignments.Add(entry.Id, entry.List);
                index._postings[entry.List].Add(entry.Id);
            }
        }

        index.TrainingState = snapshot.Centroids.Length == 0
            ? snapshot.Entries.Length == 0 ? IvfTrainingState.Untrained : IvfTrainingState.Collecting
            : snapshot.FormatVersion == 1 ? IvfTrainingState.Active : snapshot.TrainingState;

        return index;
    }

    public bool RebuildIfNeeded()
    {
        if (_vectors.Count < MinimumTrainingVectorCount || TrainingState == IvfTrainingState.Active)
        {
            return false;
        }

        Train();
        return true;
    }

    private void TrainIfReady()
    {
        TrainingState = _vectors.Count == 0 ? IvfTrainingState.Untrained : IvfTrainingState.Collecting;
        if (_vectors.Count >= MinimumTrainingVectorCount)
        {
            Train();
        }
    }

    private void Train()
    {
        _assignments.Clear();
        if (_vectors.Count == 0)
        {
            _centroids = [];
            _postings = [];
            _trainedVectorCount = 0;
            TrainingState = IvfTrainingState.Untrained;
            return;
        }

        float[][] training = _vectors.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => PrepareVector(pair.Value))
            .ToArray();
        _centroids = VectorIndexUtilities.TrainKMeans(
            training,
            _dimension,
            _listCount,
            _trainingIterations,
            seedOffset: 17);
        _postings = Enumerable.Range(0, _centroids.Length)
            .Select(_ => new HashSet<string>(StringComparer.Ordinal))
            .ToArray();
        foreach ((string id, float[] vector) in _vectors)
        {
            Assign(id, PrepareVector(vector));
        }

        _trainedVectorCount = _vectors.Count;
        TrainingState = IvfTrainingState.Active;
    }

    private void Assign(string id, ReadOnlySpan<float> vector)
    {
        int list = VectorIndexUtilities.FindNearest(vector, _centroids);
        _assignments[id] = list;
        _postings[list].Add(id);
    }

    private RankedResult[] SearchExact(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates)
    {
        PriorityQueue<RankedResult, double> top = new();
        foreach ((string id, float[] vector) in _vectors)
        {
            if (candidates is not null && !candidates.Contains(id))
            {
                continue;
            }

            float distance = DistanceFunctions.Calculate(query, vector, _metric);
            top.Enqueue(new RankedResult(id, distance), -distance);
            if (top.Count > limit)
            {
                top.Dequeue();
            }
        }

        return VectorIndexUtilities.Drain(top);
    }

    private float[] PrepareVector(ReadOnlySpan<float> vector)
    {
        float[] prepared = vector.ToArray();
        if (_metric != DistanceMetric.Cosine)
        {
            return prepared;
        }

        double squaredNorm = 0;
        foreach (float value in prepared)
        {
            squaredNorm += value * value;
        }

        if (squaredNorm <= double.Epsilon)
        {
            return prepared;
        }

        float inverseNorm = 1F / MathF.Sqrt((float)squaredNorm);
        for (int index = 0; index < prepared.Length; index++)
        {
            prepared[index] *= inverseNorm;
        }

        return prepared;
    }
}

[MemoryPackable]
internal sealed partial class IvfFlatSnapshot
{
    public int FormatVersion { get; set; } = 2;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public int ListCount { get; set; }

    public int ProbeCount { get; set; }

    public int TrainingIterations { get; set; }

    public IvfTrainingState TrainingState { get; set; }

    public int TrainedVectorCount { get; set; }

    public float[][] Centroids { get; set; } = [];

    public IvfFlatEntry[] Entries { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class IvfFlatEntry
{
    public string Id { get; set; } = string.Empty;

    public float[] Vector { get; set; } = [];

    public int List { get; set; }
}
