using System.Security.Cryptography;
using System.Text.Json;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Bundled offline ONNX provider (FS-CRAWL-002 §12.2, criterion #45): real
/// local inference over the packaged asset, checksum integrity, determinism,
/// semantic quality, and the no-download posture (missing asset → local
/// configuration error, never a fetch).
/// </summary>
public class BundledOnnxProviderTests
{
    private static string AssetDirectory => IntelligenceSqliteStackFixture.FindAssetDirectory();

    [Fact]
    public void BundledAssetsMatchTheChecksumManifest()
    {
        var directory = AssetDirectory;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));

        foreach (var section in new[] { "model", "tokenizer" })
        {
            var entry = manifest.RootElement.GetProperty(section);
            var file = entry.GetProperty("file").GetString()!;
            var expected = entry.GetProperty("sha256").GetString()!;
            var expectedBytes = entry.GetProperty("bytes").GetInt64();

            var path = Path.Combine(directory, file);
            Assert.True(File.Exists(path));
            Assert.Equal(expectedBytes, new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(expected, actual);
        }

        Assert.Equal("onnx:all-MiniLM-L6-v2:384", manifest.RootElement.GetProperty("modelTag").GetString());
        Assert.Equal(384, manifest.RootElement.GetProperty("dimensions").GetInt32());
    }

    [Fact]
    public async Task LocalInferenceProduces384DimensionalUnitVectors()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory);
        var vector = await provider.EmbedAsync("The quick brown fox jumps over the lazy dog.");

        Assert.Equal(384, vector.Values.Length);
        Assert.Equal(BundledOnnxEmbeddingProvider.DefaultModelTag, vector.ModelTag);
        Assert.Equal(384, provider.Dimensions);
        double norm = 0;
        foreach (var component in vector.Values.Span)
        {
            norm += (double)component * component;
        }

        Assert.Equal(1.0, Math.Sqrt(norm), 3);
    }

    [Fact]
    public async Task EmbeddingsAreDeterministic()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory);
        var first = await provider.EmbedAsync("Deterministic local inference.");
        var second = await provider.EmbedAsync("Deterministic local inference.");
        Assert.Equal(first.Values.ToArray(), second.Values.ToArray());
    }

    [Fact]
    public async Task SimilarSentencesScoreAboveDissimilarOnes()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory);
        var baking = await provider.EmbedAsync("How to bake sourdough bread at home.");
        var bakingSimilar = await provider.EmbedAsync("A guide to baking bread in your own kitchen.");
        var unrelated = await provider.EmbedAsync("Quarterly financial results and shareholder dividends.");

        var similarScore = VectorMath.CosineSimilarity(baking.Values.Span, bakingSimilar.Values.Span);
        var unrelatedScore = VectorMath.CosineSimilarity(baking.Values.Span, unrelated.Values.Span);

        Assert.True(similarScore > unrelatedScore,
            $"Expected similar ({similarScore:F3}) > unrelated ({unrelatedScore:F3}).");
        Assert.True(similarScore > 0.5, $"Similar sentences should score well above 0.5 (got {similarScore:F3}).");
    }

    [Fact]
    public async Task ConnectionTestConfirmsLocalModelAvailability()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory);
        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.ModelAvailable);
        Assert.Contains("384", result.Message, StringComparison.Ordinal);
        Assert.Contains("No network", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBundledAssetRaisesConfigurationErrorWithoutAnyDownload()
    {
        var emptyDirectory = Path.Combine(Path.GetTempPath(), "cf-empty-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDirectory);
        try
        {
            var ex = Assert.Throws<EmbeddingConfigurationException>(
                () => new BundledOnnxEmbeddingProvider(emptyDirectory));
            Assert.Contains("runtime downloads are forbidden", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectionTestReportsUnavailableWhenVocabularyIsMissing()
    {
        // Model present but vocabulary absent: the provider degrades to an
        // unavailable verdict (local diagnostic posture), never a download.
        var partialDirectory = Path.Combine(Path.GetTempPath(), "cf-partial-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(partialDirectory);
        try
        {
            File.Copy(Path.Combine(AssetDirectory, "model.onnx"), Path.Combine(partialDirectory, "model.onnx"));
            using var provider = new BundledOnnxEmbeddingProvider(partialDirectory);
            var result = await provider.TestConnectionAsync();
            Assert.False(result.IsReachable);
            Assert.False(result.ModelAvailable);
        }
        finally
        {
            Directory.Delete(partialDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LongInputIsChunkedAndPooledByTheModelLimit()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory, maxTokens: 64);
        var longText = string.Join(' ', Enumerable.Repeat("semantic embedding analysis", 200));
        var vector = await provider.EmbedAsync(longText);
        Assert.Equal(384, vector.Values.Length);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        using var provider = new BundledOnnxEmbeddingProvider(AssetDirectory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.EmbedAsync("cancelled", cts.Token));
    }
}
