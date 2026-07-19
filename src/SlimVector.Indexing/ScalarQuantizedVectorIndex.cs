using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class ScalarQuantizedVectorIndex : IVectorIndex, IBulkVectorIndex
{
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly VectorQuantizationKind _kind;
    private readonly int _rerankCandidateMultiplier;
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _int8Codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort[]> _float16Codes = new(StringComparer.Ordinal);
    private float[] _minimums;
    private float[] _scales;

    public ScalarQuantizedVectorIndex(
        int dimension,
        DistanceMetric metric,
        VectorQuantizationKind kind,
        int rerankCandidateMultiplier = 4)
    {
        DomainValidation.ValidateDimension(dimension);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(rerankCandidateMultiplier, 1);
        _dimension = dimension;
        _metric = metric;
        _kind = kind;
        _rerankCandidateMultiplier = rerankCandidateMultiplier;
        _minimums = new float[dimension];
        _scales = new float[dimension];
    }

    public int Count => _vectors.Count;

    public VectorQuantizationKind Quantization => _kind;

    public long EncodedBytes => _kind switch
    {
        VectorQuantizationKind.Float32 => (long)Count * _dimension * sizeof(float),
        VectorQuantizationKind.Float16 => (long)Count * _dimension * sizeof(ushort),
        VectorQuantizationKind.Int8 => (long)Count * _dimension + (long)_dimension * sizeof(float) * 2,
        _ => 0,
    };

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

        ReencodeAll();
    }

    public void Upsert(string id, ReadOnlySpan<float> vector)
    {
        DomainValidation.ValidateDocumentId(id);
        VectorIndexUtilities.ValidateVector(vector, _dimension);
        _vectors[id] = vector.ToArray();
        if (_kind == VectorQuantizationKind.Int8)
        {
            ReencodeInt8();
        }
        else if (_kind == VectorQuantizationKind.Float16)
        {
            _float16Codes[id] = EncodeFloat16(vector);
        }
    }

    public bool Remove(string id)
    {
        bool removed = _vectors.Remove(id);
        _int8Codes.Remove(id);
        _float16Codes.Remove(id);
        if (removed && _kind == VectorQuantizationKind.Int8)
        {
            ReencodeInt8();
        }

        return removed;
    }

    public IReadOnlyList<RankedResult> Search(
        ReadOnlySpan<float> query,
        int limit,
        IReadOnlySet<string>? candidates = null)
    {
        VectorIndexUtilities.ValidateVector(query, _dimension);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        int approximateLimit = Math.Min(
            _vectors.Count,
            checked(limit * _rerankCandidateMultiplier));
        PriorityQueue<RankedResult, double> approximate = new();

        foreach ((string id, float[] vector) in _vectors)
        {
            if (candidates is not null && !candidates.Contains(id))
            {
                continue;
            }

            float distance = _kind switch
            {
                VectorQuantizationKind.Float32 => DistanceFunctions.Calculate(query, vector, _metric),
                VectorQuantizationKind.Float16 => DistanceToFloat16(query, _float16Codes[id]),
                VectorQuantizationKind.Int8 => DistanceToInt8(query, _int8Codes[id]),
                _ => throw new InvalidOperationException("Unknown quantization kind."),
            };
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
        ScalarQuantizedSnapshot snapshot = new()
        {
            Dimension = _dimension,
            Metric = _metric,
            Kind = _kind,
            RerankCandidateMultiplier = _rerankCandidateMultiplier,
            Entries = _vectors.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ScalarQuantizedEntry
                {
                    Id = pair.Key,
                    Original = pair.Value,
                    Int8 = _int8Codes.GetValueOrDefault(pair.Key) ?? [],
                    Float16 = _float16Codes.GetValueOrDefault(pair.Key) ?? [],
                })
                .ToArray(),
            Minimums = _minimums,
            Scales = _scales,
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public static ScalarQuantizedVectorIndex? Deserialize(
        ReadOnlySpan<byte> data,
        CollectionDefinition definition)
    {
        ScalarQuantizedSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<ScalarQuantizedSnapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Dimension != definition.Dimension ||
            snapshot.Metric != definition.Metric || snapshot.Kind != definition.VectorIndex.Quantization ||
            snapshot.RerankCandidateMultiplier != definition.VectorIndex.RerankCandidateMultiplier ||
            snapshot.Entries is null || snapshot.Minimums?.Length != snapshot.Dimension ||
            snapshot.Scales?.Length != snapshot.Dimension || snapshot.Entries.Any(entry =>
                entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Original?.Length != snapshot.Dimension) ||
            snapshot.Entries.Select(static entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Entries.Length)
        {
            return null;
        }

        ScalarQuantizedVectorIndex index = new(
            snapshot.Dimension,
            snapshot.Metric,
            snapshot.Kind,
            snapshot.RerankCandidateMultiplier);
        index._minimums = snapshot.Minimums;
        index._scales = snapshot.Scales;
        foreach (ScalarQuantizedEntry entry in snapshot.Entries)
        {
            index._vectors.Add(entry.Id, entry.Original);
            if (snapshot.Kind == VectorQuantizationKind.Int8)
            {
                if (entry.Int8?.Length != snapshot.Dimension)
                {
                    return null;
                }

                index._int8Codes.Add(entry.Id, entry.Int8);
            }
            else if (snapshot.Kind == VectorQuantizationKind.Float16)
            {
                if (entry.Float16?.Length != snapshot.Dimension)
                {
                    return null;
                }

                index._float16Codes.Add(entry.Id, entry.Float16);
            }
        }

        return index;
    }

    private void ReencodeAll()
    {
        _int8Codes.Clear();
        _float16Codes.Clear();
        if (_kind == VectorQuantizationKind.Int8)
        {
            ReencodeInt8();
        }
        else if (_kind == VectorQuantizationKind.Float16)
        {
            foreach ((string id, float[] vector) in _vectors)
            {
                _float16Codes.Add(id, EncodeFloat16(vector));
            }
        }
    }

    private void ReencodeInt8()
    {
        _int8Codes.Clear();
        if (_vectors.Count == 0)
        {
            Array.Clear(_minimums);
            Array.Clear(_scales);
            return;
        }

        Array.Fill(_minimums, float.PositiveInfinity);
        float[] maximums = new float[_dimension];
        Array.Fill(maximums, float.NegativeInfinity);
        foreach (float[] vector in _vectors.Values)
        {
            for (int coordinate = 0; coordinate < _dimension; coordinate++)
            {
                _minimums[coordinate] = MathF.Min(_minimums[coordinate], vector[coordinate]);
                maximums[coordinate] = MathF.Max(maximums[coordinate], vector[coordinate]);
            }
        }

        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            float range = maximums[coordinate] - _minimums[coordinate];
            _scales[coordinate] = range <= float.Epsilon ? 1F : range / byte.MaxValue;
        }

        foreach ((string id, float[] vector) in _vectors)
        {
            byte[] code = new byte[_dimension];
            for (int coordinate = 0; coordinate < _dimension; coordinate++)
            {
                code[coordinate] = (byte)Math.Clamp(
                    (int)MathF.Round((vector[coordinate] - _minimums[coordinate]) / _scales[coordinate]),
                    byte.MinValue,
                    byte.MaxValue);
            }

            _int8Codes.Add(id, code);
        }
    }

    private float DistanceToInt8(ReadOnlySpan<float> query, ReadOnlySpan<byte> code)
    {
        float[] decoded = new float[_dimension];
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            decoded[coordinate] = _minimums[coordinate] + code[coordinate] * _scales[coordinate];
        }

        return DistanceFunctions.Calculate(query, decoded, _metric);
    }

    private float DistanceToFloat16(ReadOnlySpan<float> query, ReadOnlySpan<ushort> code)
    {
        float[] decoded = new float[_dimension];
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            decoded[coordinate] = (float)BitConverter.UInt16BitsToHalf(code[coordinate]);
        }

        return DistanceFunctions.Calculate(query, decoded, _metric);
    }

    private ushort[] EncodeFloat16(ReadOnlySpan<float> vector)
    {
        ushort[] code = new ushort[_dimension];
        for (int coordinate = 0; coordinate < _dimension; coordinate++)
        {
            code[coordinate] = BitConverter.HalfToUInt16Bits((Half)vector[coordinate]);
        }

        return code;
    }
}

[MemoryPackable]
internal sealed partial class ScalarQuantizedSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public int Dimension { get; set; }

    public DistanceMetric Metric { get; set; }

    public VectorQuantizationKind Kind { get; set; }

    public int RerankCandidateMultiplier { get; set; }

    public float[] Minimums { get; set; } = [];

    public float[] Scales { get; set; } = [];

    public ScalarQuantizedEntry[] Entries { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class ScalarQuantizedEntry
{
    public string Id { get; set; } = string.Empty;

    public float[] Original { get; set; } = [];

    public byte[] Int8 { get; set; } = [];

    public ushort[] Float16 { get; set; } = [];
}
