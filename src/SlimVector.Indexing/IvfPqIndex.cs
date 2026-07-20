using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class IvfPqIndex : IVectorIndex, IBulkVectorIndex
{
    public const int MinimumTrainingPointsPerCentroid = 39;
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly int _listCount;
    private readonly int _probeCount;
    private readonly int _ivfIterations;
    private readonly int _subvectorCount;
    private readonly int _pqCentroidCount;
    private readonly int _pqIterations;
    private readonly int _rerankCandidateMultiplier;
    private readonly int _subvectorDimension;
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _assignments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _codes = new(StringComparer.Ordinal);
    private float[][] _ivfCentroids = [];
    private float[][][] _codebooks = [];
    private HashSet<string>[] _postings = [];
    private int _trainedVectorCount;

    public IvfPqIndex(
        int dimension,
        DistanceMetric metric,
        int listCount,
        int probeCount,
        int ivfIterations,
        int subvectorCount,
        int pqCentroidCount,
        int pqIterations,
        int rerankCandidateMultiplier)
    {
        DomainValidation.ValidateDimension(dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(listCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(probeCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(probeCount, listCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(ivfIterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(subvectorCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pqCentroidCount, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pqCentroidCount, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(pqIterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rerankCandidateMultiplier, 1);
        if (dimension % subvectorCount != 0)
        {
            throw new DomainException(
                ErrorCodes.InvalidIndexConfiguration,
                $"PQ subvector count {subvectorCount} must divide vector dimension {dimension}.");
        }

        _dimension = dimension;
        _metric = metric;
        _listCount = listCount;
        _probeCount = probeCount;
        _ivfIterations = ivfIterations;
        _subvectorCount = subvectorCount;
        _pqCentroidCount = pqCentroidCount;
        _pqIterations = pqIterations;
        _rerankCandidateMultiplier = rerankCandidateMultiplier;
        _subvectorDimension = dimension / subvectorCount;
    }

    public int Count => _vectors.Count;

    public int TrainedListCount => _ivfCentroids.Length;

    public int TrainedCodebookSize => _codebooks.Length == 0 ? 0 : _codebooks.Sum(static book => book.Length);

    public int MinimumTrainingVectorCount => checked(
        Math.Max(_listCount, _pqCentroidCount) * MinimumTrainingPointsPerCentroid);

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

        _codes.Remove(id);
        _vectors[id] = vector.ToArray();
        if (_ivfCentroids.Length == 0 || _codebooks.Length == 0)
        {
            TrainIfReady();
            return;
        }

        EncodeAndAssign(id, vector);
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

        _codes.Remove(id);
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
        if (_ivfCentroids.Length == 0)
        {
            return SearchExact(query, limit, candidates);
        }

        float[] queryVector = PrepareVector(query);
        int[] lists = Enumerable.Range(0, _ivfCentroids.Length)
            .Select(index => (Index: index, Distance: VectorIndexUtilities.SquaredEuclidean(queryVector, _ivfCentroids[index])))
            .OrderBy(static item => item.Distance)
            .ThenBy(static item => item.Index)
            .Take(Math.Min(_probeCount, _ivfCentroids.Length))
            .Select(static item => item.Index)
            .ToArray();
        IEnumerable<string> candidateIds = candidates is not null && candidates.Count <= Math.Max(limit * 8, _ivfCentroids.Length)
            ? candidates
            : lists.SelectMany(index => _postings[index]);
        int approximateLimit = Math.Min(
            _vectors.Count,
            checked(limit * _rerankCandidateMultiplier));
        PriorityQueue<RankedResult, double> approximate = new();
        foreach (string id in candidateIds.Distinct(StringComparer.Ordinal))
        {
            if (!_codes.TryGetValue(id, out byte[]? code) || candidates is not null && !candidates.Contains(id))
            {
                continue;
            }

            float distance = ApproximateDistance(queryVector, _assignments[id], code);
            approximate.Enqueue(new RankedResult(id, distance), -distance);
            if (approximate.Count > approximateLimit)
            {
                approximate.Dequeue();
            }
        }

        PriorityQueue<RankedResult, double> reranked = new();
        foreach (RankedResult result in VectorIndexUtilities.Drain(approximate))
        {
            float exact = DistanceFunctions.Calculate(query, _vectors[result.Id], _metric);
            reranked.Enqueue(new RankedResult(result.Id, exact), -exact);
            if (reranked.Count > limit)
            {
                reranked.Dequeue();
            }
        }

        return VectorIndexUtilities.Drain(reranked);
    }

    public byte[] Serialize()
    {
        IvfPqSnapshot snapshot = new()
        {
            Dimension = _dimension,
            Metric = _metric,
            ListCount = _listCount,
            ProbeCount = _probeCount,
            IvfIterations = _ivfIterations,
            SubvectorCount = _subvectorCount,
            PqCentroidCount = _pqCentroidCount,
            PqIterations = _pqIterations,
            RerankCandidateMultiplier = _rerankCandidateMultiplier,
            TrainingState = TrainingState,
            TrainedVectorCount = _trainedVectorCount,
            IvfCentroids = _ivfCentroids,
            Codebooks = _codebooks,
            Entries = _vectors.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new IvfPqEntry
                {
                    Id = pair.Key,
                    Original = pair.Value,
                    List = _assignments.GetValueOrDefault(pair.Key, -1),
                    Code = _codes.GetValueOrDefault(pair.Key, []),
                })
                .ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public static IvfPqIndex? Deserialize(ReadOnlySpan<byte> data, CollectionDefinition definition)
    {
        IvfPqSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<IvfPqSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        VectorIndexConfiguration configuration = definition.VectorIndex;
        if (snapshot is null || snapshot.FormatVersion is not (1 or 2) || snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric || snapshot.ListCount != configuration.IvfListCount ||
            snapshot.ProbeCount != configuration.IvfProbeCount || snapshot.IvfIterations != configuration.IvfTrainingIterations ||
            snapshot.SubvectorCount != configuration.PqSubvectorCount ||
            snapshot.PqCentroidCount != configuration.PqCentroidCount ||
            snapshot.PqIterations != configuration.PqTrainingIterations ||
            snapshot.RerankCandidateMultiplier != configuration.RerankCandidateMultiplier ||
            snapshot.IvfCentroids is null || snapshot.Codebooks is null || snapshot.Entries is null ||
            snapshot.IvfCentroids.Any(centroid => centroid?.Length != snapshot.Dimension) ||
            snapshot.Codebooks.Length > 0 && (snapshot.Codebooks.Length != snapshot.SubvectorCount || snapshot.Codebooks.Any(book =>
                book is null || book.Length == 0 || book.Any(centroid => centroid?.Length != snapshot.Dimension / snapshot.SubvectorCount))) ||
            snapshot.Entries.Any(entry => entry is null || entry.Original?.Length != snapshot.Dimension ||
                snapshot.IvfCentroids.Length > 0 && (entry.Code?.Length != snapshot.SubvectorCount ||
                    entry.List < 0 || entry.List >= snapshot.IvfCentroids.Length)) ||
            snapshot.Entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Entries.Length)
        {
            return null;
        }

        IvfPqIndex index;
        try
        {
            index = new IvfPqIndex(
                snapshot.Dimension,
                snapshot.Metric,
                snapshot.ListCount,
                snapshot.ProbeCount,
                snapshot.IvfIterations,
                snapshot.SubvectorCount,
                snapshot.PqCentroidCount,
                snapshot.PqIterations,
                snapshot.RerankCandidateMultiplier);
        }
        catch (DomainException)
        {
            return null;
        }

        index._ivfCentroids = snapshot.IvfCentroids;
        index._codebooks = snapshot.Codebooks;
        index._trainedVectorCount = snapshot.FormatVersion == 1 ? snapshot.Entries.Length : snapshot.TrainedVectorCount;
        index._postings = Enumerable.Range(0, snapshot.IvfCentroids.Length)
            .Select(_ => new HashSet<string>(StringComparer.Ordinal))
            .ToArray();
        foreach (IvfPqEntry entry in snapshot.Entries)
        {
            index._vectors.Add(entry.Id, entry.Original);
            if (snapshot.IvfCentroids.Length > 0)
            {
                index._assignments.Add(entry.Id, entry.List);
                index._codes.Add(entry.Id, entry.Code);
                index._postings[entry.List].Add(entry.Id);
            }
        }

        index.TrainingState = snapshot.IvfCentroids.Length == 0
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
        _codes.Clear();
        if (_vectors.Count == 0)
        {
            _ivfCentroids = [];
            _codebooks = [];
            _postings = [];
            _trainedVectorCount = 0;
            TrainingState = IvfTrainingState.Untrained;
            return;
        }

        KeyValuePair<string, float[]>[] ordered = _vectors.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        float[][] training = ordered.Select(pair => PrepareVector(pair.Value)).ToArray();
        _ivfCentroids = VectorIndexUtilities.TrainKMeans(
            training,
            _dimension,
            _listCount,
            _ivfIterations,
            seedOffset: 29);
        _postings = Enumerable.Range(0, _ivfCentroids.Length)
            .Select(_ => new HashSet<string>(StringComparer.Ordinal))
            .ToArray();

        float[][] residuals = new float[ordered.Length][];
        for (int index = 0; index < ordered.Length; index++)
        {
            int list = VectorIndexUtilities.FindNearest(training[index], _ivfCentroids);
            _assignments.Add(ordered[index].Key, list);
            _postings[list].Add(ordered[index].Key);
            residuals[index] = Subtract(training[index], _ivfCentroids[list]);
        }

        _codebooks = new float[_subvectorCount][][];
        for (int subvector = 0; subvector < _subvectorCount; subvector++)
        {
            int offset = subvector * _subvectorDimension;
            float[][] subvectors = residuals.Select(residual => residual.AsSpan(offset, _subvectorDimension).ToArray()).ToArray();
            _codebooks[subvector] = VectorIndexUtilities.TrainKMeans(
                subvectors,
                _subvectorDimension,
                _pqCentroidCount,
                _pqIterations,
                seedOffset: 41 + subvector);
        }

        foreach ((string id, float[] vector) in ordered)
        {
            _codes.Add(id, Encode(vector, _assignments[id]));
        }

        _trainedVectorCount = _vectors.Count;
        TrainingState = IvfTrainingState.Active;
    }

    private void EncodeAndAssign(string id, ReadOnlySpan<float> vector)
    {
        float[] prepared = PrepareVector(vector);
        int list = VectorIndexUtilities.FindNearest(prepared, _ivfCentroids);
        _assignments[id] = list;
        _postings[list].Add(id);
        _codes[id] = Encode(prepared, list);
    }

    private byte[] Encode(ReadOnlySpan<float> vector, int list)
    {
        float[] prepared = _metric == DistanceMetric.Cosine ? PrepareVector(vector) : vector.ToArray();
        byte[] code = new byte[_subvectorCount];
        for (int subvector = 0; subvector < _subvectorCount; subvector++)
        {
            int offset = subvector * _subvectorDimension;
            float[] residual = new float[_subvectorDimension];
            for (int coordinate = 0; coordinate < _subvectorDimension; coordinate++)
            {
                residual[coordinate] = prepared[offset + coordinate] - _ivfCentroids[list][offset + coordinate];
            }

            code[subvector] = checked((byte)VectorIndexUtilities.FindNearest(residual, _codebooks[subvector]));
        }

        return code;
    }

    private float ApproximateDistance(ReadOnlySpan<float> query, int list, ReadOnlySpan<byte> code)
    {
        double score = 0;
        for (int subvector = 0; subvector < _subvectorCount; subvector++)
        {
            ReadOnlySpan<float> residual = _codebooks[subvector][code[subvector]];
            int offset = subvector * _subvectorDimension;
            for (int coordinate = 0; coordinate < _subvectorDimension; coordinate++)
            {
                float reconstructed = _ivfCentroids[list][offset + coordinate] + residual[coordinate];
                score += _metric == DistanceMetric.DotProduct
                    ? -(double)query[offset + coordinate] * reconstructed
                    : Math.Pow(query[offset + coordinate] - reconstructed, 2);
            }
        }

        return _metric == DistanceMetric.Euclidean ? (float)Math.Sqrt(score) : (float)score;
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

    private static float[] Subtract(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float[] result = new float[left.Length];
        for (int index = 0; index < left.Length; index++)
        {
            result[index] = left[index] - right[index];
        }

        return result;
    }
}

[MemoryPackable]
internal sealed partial class IvfPqSnapshot
{
    public int FormatVersion { get; set; } = 2;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public int ListCount { get; set; }

    public int ProbeCount { get; set; }

    public int IvfIterations { get; set; }

    public int SubvectorCount { get; set; }

    public int PqCentroidCount { get; set; }

    public int PqIterations { get; set; }

    public int RerankCandidateMultiplier { get; set; }

    public IvfTrainingState TrainingState { get; set; }

    public int TrainedVectorCount { get; set; }

    public float[][] IvfCentroids { get; set; } = [];

    public float[][][] Codebooks { get; set; } = [];

    public IvfPqEntry[] Entries { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class IvfPqEntry
{
    public string Id { get; set; } = string.Empty;

    public float[] Original { get; set; } = [];

    public int List { get; set; }

    public byte[] Code { get; set; } = [];
}
