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
        InferenceSession session = _session!;
        EncodedInput[] encoded = new EncodedInput[count];
        int sequenceLength = 1;
        for (int batch = 0; batch < count; batch++)
        {
            Encoding encoding = tokenizer.Encode(
                texts[offset + batch],
                addSpecialTokens: true,
                includeTypeIds: true,
                includeAttentionMask: true).First();
            uint[] ids = encoding.Ids.ToArray();
            uint[] typeIds = encoding.TypeIds.ToArray();
            if (ids.Length > _options.MaximumSequenceLength)
            {
                uint last = ids[^1];
                Array.Resize(ref ids, _options.MaximumSequenceLength);
                ids[^1] = last;
                Array.Resize(ref typeIds, _options.MaximumSequenceLength);
            }

            encoded[batch] = new EncodedInput(ids, typeIds);
            sequenceLength = Math.Max(sequenceLength, ids.Length);
        }

        long[] inputIds = new long[count * sequenceLength];
        long[] tokenTypeIds = new long[count * sequenceLength];
        long[] attentionMask = new long[count * sequenceLength];
        for (int batch = 0; batch < count; batch++)
        {
            EncodedInput item = encoded[batch];
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
                int activeTokens = encoded[batch].Ids.Length;
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

    private sealed record EncodedInput(uint[] Ids, uint[] TypeIds);
}
