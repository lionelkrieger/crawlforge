using CrawlForge.Contracts.Intelligence;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Bundled offline ONNX embedding provider (FS-CRAWL-002 §12.2, exclusion E7):
/// runs the packaged all-MiniLM-L6-v2 sentence-embedding model on the local
/// ONNX runtime (CPU). Loads ONLY the bundled asset directory — there is no
/// download path, no network of any kind, and no remote fallback. Vectors are
/// mask-aware mean-pooled over token states and L2-normalized; the model tag
/// and dimension are recorded with every embedding. Inference is bounded by a
/// concurrency gate and fully cancellable.
/// </summary>
public sealed class BundledOnnxEmbeddingProvider : ILocalEmbeddingProvider, IDisposable
{
    /// <summary>Model file name inside the bundled asset directory.</summary>
    public const string ModelFileName = "model.onnx";

    /// <summary>Vocabulary file name inside the bundled asset directory.</summary>
    public const string VocabFileName = "vocab.txt";

    /// <summary>Vector dimension of the bundled all-MiniLM-L6-v2 model.</summary>
    public const int ModelDimensions = 384;

    /// <summary>Model tag recorded with every stored embedding.</summary>
    public const string DefaultModelTag = "onnx:all-MiniLM-L6-v2:384";

    private readonly string _modelPath;
    private readonly string _vocabPath;
    private readonly int _maxTokens;
    private readonly SemaphoreSlim _inferenceGate;
    private readonly Lazy<InferenceSession> _session;
    private readonly Lazy<WordPieceTokenizer> _tokenizer;
    private bool _disposed;

    /// <summary>Creates the provider over the bundled asset directory.</summary>
    /// <param name="modelDirectory">Bundled asset directory containing model.onnx and vocab.txt.</param>
    /// <param name="maxTokens">Maximum tokens per inference (model limit 256).</param>
    /// <param name="maxConcurrency">Bounded concurrent inferences (minimum 1).</param>
    /// <exception cref="EmbeddingConfigurationException">The bundled assets are missing.</exception>
    public BundledOnnxEmbeddingProvider(string modelDirectory, int maxTokens = 256, int maxConcurrency = 2)
    {
        if (maxTokens is < 3 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "maxTokens must be in [3, 512].");
        }

        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "maxConcurrency must be at least 1.");
        }

        _modelPath = Path.Combine(modelDirectory, ModelFileName);
        _vocabPath = Path.Combine(modelDirectory, VocabFileName);
        if (!File.Exists(_modelPath))
        {
            throw new EmbeddingConfigurationException(
                $"Bundled ONNX model not found at '{_modelPath}'. The model ships with the product; runtime downloads are forbidden (exclusion E7).");
        }

        _maxTokens = maxTokens;
        _inferenceGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _tokenizer = new Lazy<WordPieceTokenizer>(() => new WordPieceTokenizer(_vocabPath));
        _session = new Lazy<InferenceSession>(() => new InferenceSession(_modelPath));
    }

    /// <inheritdoc />
    public string ModelTag => DefaultModelTag;

    /// <inheritdoc />
    public int Dimensions => ModelDimensions;

    /// <inheritdoc />
    public async Task<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            var tokens = _tokenizer.Value.Encode(text, _maxTokens);
            var sequenceLength = tokens.InputIds.Length;

            var inputIds = new DenseTensor<long>(new[] { 1, sequenceLength });
            var attentionMask = new DenseTensor<long>(new[] { 1, sequenceLength });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, sequenceLength });
            for (var i = 0; i < sequenceLength; i++)
            {
                inputIds[0, i] = tokens.InputIds[i];
                attentionMask[0, i] = tokens.AttentionMask[i];
                tokenTypeIds[0, i] = tokens.TokenTypeIds[i];
            }

            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _session.Value.InputMetadata.Keys)
            {
                if (name.Contains("input_ids", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, inputIds));
                }
                else if (name.Contains("attention_mask", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, attentionMask));
                }
                else if (name.Contains("token_type", StringComparison.OrdinalIgnoreCase))
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, tokenTypeIds));
                }
            }

            using var results = _session.Value.Run(inputs);
            var vector = ExtractPooledVector(results, tokens.AttentionMask);
            return new EmbeddingVector(VectorMath.Normalize(vector), ModelTag);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EmbeddingProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = await EmbedAsync("connection test", cancellationToken);
            return new EmbeddingProviderTestResult(
                IsReachable: true,
                ModelAvailable: true,
                Message: $"Bundled ONNX model loaded locally; probe produced {probe.Values.Length} dimensions ({ModelTag}). No network used.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmbeddingProviderTestResult(false, false, $"Bundled ONNX model unavailable: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session.IsValueCreated)
        {
            _session.Value.Dispose();
        }

        _inferenceGate.Dispose();
    }

    private static float[] ExtractPooledVector(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attentionMask)
    {
        foreach (var result in results)
        {
            if (result.AsTensor<float>() is not DenseTensor<float> tensor)
            {
                continue;
            }

            if (tensor.Dimensions.Length == 3)
            {
                // Token states [1, sequence, hidden]: mask-aware mean pooling.
                var sequence = tensor.Dimensions[1];
                var hidden = tensor.Dimensions[2];
                var pooled = new float[hidden];
                long count = 0;
                for (var t = 0; t < sequence; t++)
                {
                    if (t < attentionMask.Length && attentionMask[t] == 0)
                    {
                        continue;
                    }

                    count++;
                    for (var h = 0; h < hidden; h++)
                    {
                        pooled[h] += tensor[0, t, h];
                    }
                }

                if (count > 0)
                {
                    var scale = 1f / count;
                    for (var h = 0; h < hidden; h++)
                    {
                        pooled[h] *= scale;
                    }
                }

                return pooled;
            }

            if (tensor.Dimensions.Length == 2)
            {
                // Already-pooled output [1, hidden].
                var hidden = tensor.Dimensions[1];
                var pooled = new float[hidden];
                for (var h = 0; h < hidden; h++)
                {
                    pooled[h] = tensor[0, h];
                }

                return pooled;
            }
        }

        throw new EmbeddingConfigurationException("The bundled ONNX model produced no usable float vector output.");
    }
}
