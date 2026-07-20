using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class CollectionSearchIndex : IDisposable
{
    private IVectorIndex _vector;
    private readonly Bm25Index _text;
    private readonly MetadataIndex _metadata;
    private readonly bool _metadataIndexed;
    private readonly Dictionary<string, IReadOnlyDictionary<string, MetadataValue>> _unindexedMetadata = new(StringComparer.Ordinal);

    public CollectionSearchIndex(CollectionDefinition definition)
        : this(definition, VectorIndexKind.Flat, [], null)
    {
    }

    public CollectionSearchIndex(
        CollectionDefinition definition,
        VectorIndexKind vectorKind,
        IEnumerable<DocumentRecord> documents,
        byte[]? persistedVectorIndex,
        double bm25K1 = 1.2,
        double bm25B = 0.75,
        int maximumTermsPerDocument = 100_000,
        string? diskAnnArtifactDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (vectorKind is VectorIndexKind.Auto)
        {
            throw new ArgumentOutOfRangeException(nameof(vectorKind), vectorKind, "Auto must be resolved to a concrete vector-index kind.");
        }

        DocumentRecord[] records = documents.ToArray();
        VectorKind = vectorKind;
        _metadataIndexed = definition.MetadataIndexed;
        string signature = HnswIndex.ComputeDocumentSignature(records);
        if (TryRestore(
                persistedVectorIndex,
                definition,
                vectorKind,
                signature,
                records.Length,
                definition.MetadataIndexed,
                bm25K1,
                bm25B,
                maximumTermsPerDocument,
                diskAnnArtifactDirectory,
                out IVectorIndex? restoredVector,
                out Bm25Index? restoredText,
                out MetadataIndex? restoredMetadata))
        {
            _vector = restoredVector;
            _text = restoredText;
            _metadata = restoredMetadata;
            WasRestored = true;
            WasVectorIndexRestored = true;
            if (!_metadataIndexed)
            {
                PopulateUnindexedMetadata(records);
            }

            return;
        }

        HnswIndex? legacyHnsw = vectorKind == VectorIndexKind.Hnsw && persistedVectorIndex is not null
            ? HnswIndex.Deserialize(persistedVectorIndex, definition, signature)
            : null;
        _vector = legacyHnsw ?? CreateVectorIndex(definition, vectorKind, diskAnnArtifactDirectory);
        _text = new Bm25Index(bm25K1, bm25B, maximumTermsPerDocument);
        _metadata = new MetadataIndex();
        WasVectorIndexRestored = legacyHnsw is not null;

        if (!WasVectorIndexRestored && _vector is IBulkVectorIndex bulk)
        {
            bulk.Build(records.Select(static document => (document.Id, document.Vector)).ToArray());
        }

        foreach (DocumentRecord document in records)
        {
            if (!WasVectorIndexRestored && _vector is not IBulkVectorIndex)
            {
                _vector.Upsert(document.Id, document.Vector);
            }

            _text.Upsert(document.Id, document.Text);
            if (_metadataIndexed)
            {
                _metadata.Upsert(document.Id, document.Metadata);
            }
            else
            {
                _unindexedMetadata[document.Id] = CopyMetadata(document.Metadata);
            }
        }
    }

    public int Count => _vector.Count;

    public VectorIndexKind VectorKind { get; }

    public bool WasRestored { get; }

    public bool WasVectorIndexRestored { get; }

    public void ValidateDocument(DocumentRecord document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _text.ValidateText(document.Text);
    }

    public void Upsert(DocumentRecord document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _vector.Upsert(document.Id, document.Vector);
        _text.Upsert(document.Id, document.Text);
        if (_metadataIndexed)
        {
            _metadata.Upsert(document.Id, document.Metadata);
        }
        else
        {
            _unindexedMetadata[document.Id] = CopyMetadata(document.Metadata);
        }
    }

    public bool Remove(string id)
    {
        bool removed = _vector.Remove(id);
        _text.Remove(id);
        if (_metadataIndexed)
        {
            _metadata.Remove(id);
        }
        else
        {
            _unindexedMetadata.Remove(id);
        }
        return removed;
    }

    public byte[] Serialize(IEnumerable<DocumentRecord> documents)
    {
        _ = _vector switch
        {
            IvfFlatIndex ivfFlat => ivfFlat.RebuildIfNeeded(),
            IvfPqIndex ivfPq => ivfPq.RebuildIfNeeded(),
            _ => false,
        };
        string signature = HnswIndex.ComputeDocumentSignature(documents);
        byte[] vector = _vector switch
        {
            FlatVectorIndex flat => flat.Serialize(),
            HnswIndex hnsw => hnsw.Serialize(signature),
            ScalarQuantizedVectorIndex quantized => quantized.Serialize(),
            IvfFlatIndex ivfFlat => ivfFlat.Serialize(),
            IvfPqIndex ivfPq => ivfPq.Serialize(),
            DiskAnnIndex diskAnn => diskAnn.Serialize(),
            _ => throw new InvalidOperationException($"Unsupported vector index '{_vector.GetType().Name}'."),
        };
        SearchIndexSnapshot snapshot = new()
        {
            DocumentSignature = signature,
            VectorKind = VectorKind,
            MetadataIndexed = _metadataIndexed,
            Vector = vector,
            Text = _text.Serialize(),
            Metadata = _metadata.Serialize(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    public IReadOnlyList<HybridRankedResult> Search(
        SearchRequest request,
        int candidateMultiplier,
        IReadOnlySet<string>? routingCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlySet<string>? candidates = IntersectCandidates(
            request.Filter is null ? null : EvaluateMetadata(request.Filter),
            routingCandidates);
        int candidateLimit = Math.Max(request.Limit, checked(request.Limit * candidateMultiplier));

        return request.Mode switch
        {
            SearchMode.Vector => _vector.Search(request.Vector!, request.Limit, candidates)
                .Select(static (result, index) => new HybridRankedResult(result.Id, -result.Score, index + 1, null))
                .ToArray(),
            SearchMode.Text => _text.Search(request.Text!, request.Limit, candidates)
                .Select(static (result, index) => new HybridRankedResult(result.Id, result.Score, null, index + 1))
                .ToArray(),
            SearchMode.Hybrid => RankFusion.WeightedReciprocalRank(
                _vector.Search(request.Vector!, candidateLimit, candidates),
                _text.Search(request.Text!, candidateLimit, candidates),
                request.VectorWeight,
                request.TextWeight,
                request.Limit),
            SearchMode.Metadata => (candidates ?? AllMetadataIds()).Order(StringComparer.Ordinal)
                .Take(request.Limit)
                .Select(static id => new HybridRankedResult(id, 1, null, null))
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown search mode."),
        };
    }

    private static IReadOnlySet<string>? IntersectCandidates(
        IReadOnlySet<string>? metadataCandidates,
        IReadOnlySet<string>? routingCandidates)
    {
        if (metadataCandidates is null)
        {
            return routingCandidates;
        }

        if (routingCandidates is null)
        {
            return metadataCandidates;
        }

        IReadOnlySet<string> smaller = metadataCandidates.Count <= routingCandidates.Count
            ? metadataCandidates
            : routingCandidates;
        IReadOnlySet<string> larger = ReferenceEquals(smaller, metadataCandidates)
            ? routingCandidates
            : metadataCandidates;
        return smaller.Where(larger.Contains).ToHashSet(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (_vector is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static IVectorIndex CreateVectorIndex(
        CollectionDefinition definition,
        VectorIndexKind kind,
        string? diskAnnArtifactDirectory) => kind switch
        {
            VectorIndexKind.Flat when definition.VectorIndex.Quantization == VectorQuantizationKind.Float32 =>
                new FlatVectorIndex(definition.Dimension, definition.Metric),
            VectorIndexKind.Flat => new ScalarQuantizedVectorIndex(
                definition.Dimension,
                definition.Metric,
                definition.VectorIndex.Quantization,
                definition.VectorIndex.RerankCandidateMultiplier),
            VectorIndexKind.Hnsw => new HnswIndex(
                definition.Dimension,
                definition.Metric,
                definition.VectorIndex.HnswM,
                definition.VectorIndex.HnswEfConstruction,
                definition.VectorIndex.HnswEfSearch),
            VectorIndexKind.IvfFlat => new IvfFlatIndex(
                definition.Dimension,
                definition.Metric,
                definition.VectorIndex.IvfListCount,
                definition.VectorIndex.IvfProbeCount,
                definition.VectorIndex.IvfTrainingIterations),
            VectorIndexKind.IvfPq => new IvfPqIndex(
                definition.Dimension,
                definition.Metric,
                definition.VectorIndex.IvfListCount,
                definition.VectorIndex.IvfProbeCount,
                definition.VectorIndex.IvfTrainingIterations,
                definition.VectorIndex.PqSubvectorCount,
                definition.VectorIndex.PqCentroidCount,
                definition.VectorIndex.PqTrainingIterations,
                definition.VectorIndex.RerankCandidateMultiplier),
            VectorIndexKind.DiskAnn => new DiskAnnIndex(
                definition.Dimension,
                definition.Metric,
                definition.VectorIndex.DiskAnnMaxDegree,
                definition.VectorIndex.DiskAnnSearchListSize,
                definition.VectorIndex.DiskAnnBeamWidth,
                definition.VectorIndex.DiskAnnDeltaThreshold,
                diskAnnArtifactDirectory,
                definition.VectorIndex.DiskAnnPageSize,
                definition.VectorIndex.DiskAnnCachePages,
                definition.VectorIndex.DiskAnnRetainedGenerations),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown vector-index kind."),
        };

    private static bool TryRestore(
        byte[]? persisted,
        CollectionDefinition definition,
        VectorIndexKind vectorKind,
        string documentSignature,
        int documentCount,
        bool metadataIndexed,
        double bm25K1,
        double bm25B,
        int maximumTermsPerDocument,
        string? diskAnnArtifactDirectory,
        out IVectorIndex vector,
        out Bm25Index text,
        out MetadataIndex metadata)
    {
        vector = null!;
        text = null!;
        metadata = null!;
        if (persisted is null)
        {
            return false;
        }

        SearchIndexSnapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<SearchIndexSnapshot>(persisted);
        }
        catch (MemoryPackSerializationException)
        {
            return false;
        }

        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.VectorKind != vectorKind ||
            !string.Equals(snapshot.DocumentSignature, documentSignature, StringComparison.Ordinal) ||
            snapshot.MetadataIndexed != metadataIndexed || snapshot.Vector is null || snapshot.Text is null ||
            snapshot.Metadata is null)
        {
            return false;
        }

        IVectorIndex? restoredVector = vectorKind switch
        {
            VectorIndexKind.Flat when definition.VectorIndex.Quantization == VectorQuantizationKind.Float32 =>
                FlatVectorIndex.Deserialize(snapshot.Vector, definition),
            VectorIndexKind.Flat => ScalarQuantizedVectorIndex.Deserialize(snapshot.Vector, definition),
            VectorIndexKind.Hnsw => HnswIndex.Deserialize(snapshot.Vector, definition, documentSignature),
            VectorIndexKind.IvfFlat => IvfFlatIndex.Deserialize(snapshot.Vector, definition),
            VectorIndexKind.IvfPq => IvfPqIndex.Deserialize(snapshot.Vector, definition),
            VectorIndexKind.DiskAnn => DiskAnnIndex.Deserialize(snapshot.Vector, definition, diskAnnArtifactDirectory),
            _ => null,
        };
        Bm25Index? restoredText = Bm25Index.Deserialize(
            snapshot.Text,
            bm25K1,
            bm25B,
            maximumTermsPerDocument);
        MetadataIndex? restoredMetadata = MetadataIndex.Deserialize(snapshot.Metadata);
        int expectedMetadataCount = metadataIndexed ? documentCount : 0;
        if (restoredVector?.Count != documentCount || restoredText?.Count != documentCount ||
            restoredMetadata?.Count != expectedMetadataCount)
        {
            return false;
        }

        vector = restoredVector;
        text = restoredText;
        metadata = restoredMetadata;
        return true;
    }

    private IReadOnlySet<string> EvaluateMetadata(MetadataFilter filter)
    {
        if (_metadataIndexed)
        {
            return _metadata.Evaluate(filter);
        }

        return _unindexedMetadata
            .Where(pair => MetadataFilterEvaluator.Matches(pair.Value, filter))
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private IReadOnlySet<string> AllMetadataIds() => _metadataIndexed
        ? _metadata.Evaluate(filter: null)
        : _unindexedMetadata.Keys.ToHashSet(StringComparer.Ordinal);

    private void PopulateUnindexedMetadata(IEnumerable<DocumentRecord> records)
    {
        foreach (DocumentRecord document in records)
        {
            _unindexedMetadata[document.Id] = CopyMetadata(document.Metadata);
        }
    }

    private static Dictionary<string, MetadataValue> CopyMetadata(
        IReadOnlyDictionary<string, MetadataValue> metadata) =>
        metadata.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
}

[MemoryPackable]
internal sealed partial class SearchIndexSnapshot
{
    public int FormatVersion { get; set; } = 1;

    public string DocumentSignature { get; set; } = string.Empty;

    public VectorIndexKind VectorKind { get; set; }

    public bool MetadataIndexed { get; set; } = true;

    public byte[] Vector { get; set; } = [];

    public byte[] Text { get; set; } = [];

    public byte[] Metadata { get; set; } = [];
}
