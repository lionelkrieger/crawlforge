using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Provider factory (FS-CRAWL-002 §12.2): ONNX ↔ loopback-daemon switching
/// without data migration, configuration-time loopback enforcement.
/// </summary>
public class ProviderFactoryTests
{
    private static string AssetDirectory => IntelligenceSqliteStackFixture.FindAssetDirectory();

    [Fact]
    public void BundledOnnxSettingsCreateTheOfflineProvider()
    {
        var provider = EmbeddingProviderFactory.Create(
            new EmbeddingProviderSettings(EmbeddingProviderKind.BundledOnnx, null, null),
            AssetDirectory);
        try
        {
            Assert.IsType<BundledOnnxEmbeddingProvider>(provider);
            Assert.Equal("onnx:all-MiniLM-L6-v2:384", provider.ModelTag);
            Assert.Equal(384, provider.Dimensions);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void LoopbackDaemonSettingsCreateTheDaemonProvider()
    {
        var provider = EmbeddingProviderFactory.Create(
            new EmbeddingProviderSettings(EmbeddingProviderKind.LoopbackDaemon, "http://127.0.0.1:11434", "my-model"),
            AssetDirectory);
        try
        {
            Assert.IsType<LoopbackDaemonEmbeddingProvider>(provider);
            Assert.Equal("my-model", provider.ModelTag);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void NonLoopbackDaemonSettingsAreRejectedAtConfiguration()
    {
        Assert.Throws<EmbeddingConfigurationException>(() => EmbeddingProviderFactory.Create(
            new EmbeddingProviderSettings(EmbeddingProviderKind.LoopbackDaemon, "https://embeddings.example.test", null),
            AssetDirectory));
    }

    [Fact]
    public void SettingsValidationMirrorsConstructionRules()
    {
        Assert.True(EmbeddingProviderFactory.TryValidateSettings(
            new EmbeddingProviderSettings(EmbeddingProviderKind.BundledOnnx, null, null), out _));
        Assert.True(EmbeddingProviderFactory.TryValidateSettings(
            new EmbeddingProviderSettings(EmbeddingProviderKind.LoopbackDaemon, "http://localhost:11434", null), out _));
        Assert.False(EmbeddingProviderFactory.TryValidateSettings(
            new EmbeddingProviderSettings(EmbeddingProviderKind.LoopbackDaemon, "http://10.0.0.1:11434", null), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ProviderSwitchChangesOnlyTheModelTagNotTheStorageSchema()
    {
        // Both providers produce EmbeddingVector with a model tag; the same
        // url_embeddings table stores either — switching requires no migration
        // (the pipeline re-embeds on tag change, §12.3).
        using var onnx = new BundledOnnxEmbeddingProvider(AssetDirectory);
        using var daemon = new LoopbackDaemonEmbeddingProvider("http://127.0.0.1:11434", "daemon-model");
        Assert.NotEqual(onnx.ModelTag, daemon.ModelTag);

        var onnxPayload = EmbeddingPayload.FromVector(
            new CrawlForge.Contracts.Intelligence.EmbeddingVector(new float[] { 1, 0 }, onnx.ModelTag), "h");
        var daemonPayload = EmbeddingPayload.FromVector(
            new CrawlForge.Contracts.Intelligence.EmbeddingVector(new float[] { 1, 0 }, daemon.ModelTag), "h");
        Assert.NotNull(EmbeddingPayload.TryParse(onnxPayload.ToJson()));
        Assert.NotNull(EmbeddingPayload.TryParse(daemonPayload.ToJson()));
    }
}
