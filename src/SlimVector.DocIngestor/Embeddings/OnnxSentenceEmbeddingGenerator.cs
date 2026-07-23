using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using Tokenizers.HuggingFace.Tokenizer;

namespace SlimVector.DocIngestor.Embeddings;

public sealed class OnnxSentenceEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    private readonly HuggingFaceEmbeddingOptions _options;
    private readonly HuggingFaceModelCache _cache;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private bool _disposed;

    public OnnxSentenceEmbeddingGenerator(HttpClient httpClient, HuggingFaceEmbeddingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _cache = new HuggingFaceModelCache(httpClient, options);
    }

    public string ModelId => _options.ModelId;

    public int Dimension => _options.Dimension;

    public ValueTask<EmbeddingModelStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EmbeddingModelStatus
        {
            ModelId = _options.ModelId,
            Revision = _options.Revision,
            Variant = _cache.Variant,
            Directory = Path.GetFullPath(_options.ModelDirectory),
            Dimension = _options.Dimension,
            MaximumSequenceLength = _options.MaximumSequenceLength,
            IsReady = _cache.IsReady,
        });
    }

    public ValueTask EnsureReadyAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) => _cache.EnsureReadyAsync(progress, cancellationToken);

    public async ValueTask<IReadOnlyList<float[]>> GenerateAsync(
        IReadOnlyList<string> texts,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        if (texts.Any(static text => string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException("Embedding inputs cannot be null or whitespace.", nameof(texts));
        }

        await EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRuntimeLoaded();
            List<float[]> result = new(texts.Count);
            for (int offset = 0; offset < texts.Count; offset += _options.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(_options.BatchSize, texts.Count - offset);
                RunBatch(texts, offset, count, result);
                progress?.Report(result.Count * 100d / texts.Count);
            }

            return result;
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();
        _tokenizer?.Dispose();
        _cache.Dispose();
        _inferenceGate.Dispose();
    }

    private void EnsureRuntimeLoaded()
    {
        if (_session is not null && _tokenizer is not null)
        {
            return;
        }

        SessionOptions sessionOptions = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        _session = new InferenceSession(_cache.ModelPath, sessionOptions);
        _tokenizer = Tokenizer.FromFile(_cache.TokenizerPath);
    }

    private void RunBatch(IReadOnlyList<string> texts, int offset, int count, List<float[]> destination)
    {
        Tokenizer tokenizer = _tokenizer!;
        List<EncodedInput> encoded = [];
        int[] windowCounts = new int[count];
        for (int document = 0; document < count; document++)
        {
            Encoding encoding = tokenizer.Encode(
                texts[offset + document],
                addSpecialTokens: true,
                includeTypeIds: true,
                includeAttentionMask: true).First();
            uint[] ids = encoding.Ids.ToArray();
            uint[] typeIds = encoding.TypeIds.ToArray();
            IReadOnlyList<EncodedTokenWindow> windows = CreateTokenWindows(
                ids,
                typeIds,
                _options.MaximumSequenceLength);
            windowCounts[document] = windows.Count;
            foreach (EncodedTokenWindow window in windows)
            {
                encoded.Add(new EncodedInput(window.Ids, window.TypeIds, window.ContentTokenCount));
            }
        }

        List<float[]> windowVectors = new(encoded.Count);
        for (int windowOffset = 0; windowOffset < encoded.Count; windowOffset += _options.BatchSize)
        {
            int windowCount = Math.Min(_options.BatchSize, encoded.Count - windowOffset);
            RunEncodedBatch(encoded, windowOffset, windowCount, windowVectors);
        }

        int vectorIndex = 0;
        for (int document = 0; document < count; document++)
        {
            float[] vector = new float[_options.Dimension];
            int totalWeight = 0;
            for (int window = 0; window < windowCounts[document]; window++)
            {
                EncodedInput item = encoded[vectorIndex];
                float[] windowVector = windowVectors[vectorIndex];
                totalWeight += item.ContentTokenCount;
                for (int dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] += windowVector[dimension] * item.ContentTokenCount;
                }

                vectorIndex++;
            }

            if (totalWeight > 0)
            {
                for (int dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] /= totalWeight;
                }
            }

            Normalize(vector);
            destination.Add(vector);
        }
    }

    internal static IReadOnlyList<EncodedTokenWindow> CreateTokenWindows(
        uint[] ids,
        uint[] typeIds,
        int maximumSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(typeIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSequenceLength, 3);

        int contentTokenCount = Math.Max(1, ids.Length - 2);
        if (ids.Length <= maximumSequenceLength || ids.Length < 3)
        {
            return [new EncodedTokenWindow(ids, typeIds, contentTokenCount)];
        }

        int windowCapacity = maximumSequenceLength - 2;
        int contentLength = ids.Length - 2;
        List<EncodedTokenWindow> windows = new((contentLength + windowCapacity - 1) / windowCapacity);
        for (int contentOffset = 0; contentOffset < contentLength; contentOffset += windowCapacity)
        {
            int count = Math.Min(windowCapacity, contentLength - contentOffset);
            uint[] windowIds = new uint[count + 2];
            uint[] windowTypeIds = new uint[count + 2];
            windowIds[0] = ids[0];
            windowIds[^1] = ids[^1];
            windowTypeIds[0] = typeIds.Length > 0 ? typeIds[0] : 0;
            windowTypeIds[^1] = typeIds.Length > 0 ? typeIds[Math.Min(ids.Length - 1, typeIds.Length - 1)] : 0;
            Array.Copy(ids, contentOffset + 1, windowIds, 1, count);
            for (int token = 0; token < count; token++)
            {
                int sourceIndex = contentOffset + token + 1;
                windowTypeIds[token + 1] = sourceIndex < typeIds.Length ? typeIds[sourceIndex] : 0;
            }

            windows.Add(new EncodedTokenWindow(windowIds, windowTypeIds, count));
        }

        return windows;
    }

    private void RunEncodedBatch(
        IReadOnlyList<EncodedInput> encoded,
        int offset,
        int count,
        List<float[]> destination)
    {
        InferenceSession session = _session!;
        int sequenceLength = 1;
        for (int batch = 0; batch < count; batch++)
        {
            sequenceLength = Math.Max(sequenceLength, encoded[offset + batch].Ids.Length);
        }

        long[] inputIds = new long[count * sequenceLength];
        long[] tokenTypeIds = new long[count * sequenceLength];
        long[] attentionMask = new long[count * sequenceLength];
        for (int batch = 0; batch < count; batch++)
        {
            EncodedInput item = encoded[offset + batch];
            for (int token = 0; token < item.Ids.Length; token++)
            {
                int index = batch * sequenceLength + token;
                inputIds[index] = item.Ids[token];
                tokenTypeIds[index] = token < item.TypeIds.Length ? item.TypeIds[token] : 0;
                attentionMask[index] = 1;
            }
        }

        DenseTensor<long> idsTensor = new(inputIds, [count, sequenceLength]);
        DenseTensor<long> typeTensor = new(tokenTypeIds, [count, sequenceLength]);
        DenseTensor<long> maskTensor = new(attentionMask, [count, sequenceLength]);
        List<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor("input_ids", idsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor),
        ];
        if (session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", typeTensor));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(inputs);
        Tensor<float>? output = null;
        foreach (DisposableNamedOnnxValue item in outputs)
        {
            output = item.AsTensor<float>();
            break;
        }

        if (output is null)
        {
            throw new DocumentIngestionException("embedding_model_output_missing", "The ONNX model returned no output tensor.");
        }

        int[] dimensions = output.Dimensions.ToArray();
        if (dimensions.Length == 3)
        {
            if (dimensions[0] != count || dimensions[2] != _options.Dimension)
            {
                throw UnexpectedOutput(dimensions);
            }

            for (int batch = 0; batch < count; batch++)
            {
                float[] vector = new float[_options.Dimension];
                int activeTokens = encoded[offset + batch].Ids.Length;
                for (int token = 0; token < activeTokens; token++)
                {
                    for (int dimension = 0; dimension < vector.Length; dimension++)
                    {
                        vector[dimension] += output[batch, token, dimension];
                    }
                }

                for (int dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] /= activeTokens;
                }

                Normalize(vector);
                destination.Add(vector);
            }

            return;
        }

        if (dimensions.Length == 2 && dimensions[0] == count && dimensions[1] == _options.Dimension)
        {
            for (int batch = 0; batch < count; batch++)
            {
                float[] vector = new float[_options.Dimension];
                for (int dimension = 0; dimension < vector.Length; dimension++)
                {
                    vector[dimension] = output[batch, dimension];
                }

                Normalize(vector);
                destination.Add(vector);
            }

            return;
        }

        throw UnexpectedOutput(dimensions);
    }

    private static void Normalize(Span<float> vector)
    {
        double magnitudeSquared = 0;
        foreach (float value in vector)
        {
            magnitudeSquared += value * value;
        }

        if (magnitudeSquared <= double.Epsilon)
        {
            return;
        }

        float divisor = (float)Math.Sqrt(magnitudeSquared);
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] /= divisor;
        }
    }

    private static DocumentIngestionException UnexpectedOutput(IReadOnlyList<int> dimensions) => new(
        "embedding_model_output_invalid",
        $"The ONNX model returned an unexpected tensor shape [{string.Join(',', dimensions)}].");

    internal readonly record struct EncodedTokenWindow(uint[] Ids, uint[] TypeIds, int ContentTokenCount);

    private sealed record EncodedInput(uint[] Ids, uint[] TypeIds, int ContentTokenCount);
}
