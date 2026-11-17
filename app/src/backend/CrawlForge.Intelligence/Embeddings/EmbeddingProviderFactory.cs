using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Composition-time provider factory (FS-CRAWL-002 §12.2): selects the bundled
/// offline ONNX model or the user-configured loopback-only daemon from
/// application settings. Non-loopback daemon endpoints are REJECTED here at
/// configuration time (exclusion E7). Switching providers requires NO data
/// migration: vectors live in the same url_embeddings table tagged by model;
/// a model-tag change triggers a full re-embed of the audit (§12.3 prohibits
/// mixing model tags within one audit), handled by the semantic pipeline.
/// </summary>
public static class EmbeddingProviderFactory
{
    /// <summary>
    /// Creates the configured provider.
    /// </summary>
    /// <param name="settings">Provider selection from application settings.</param>
    /// <param name="bundledModelDirectory">Bundled ONNX asset directory (app\assets\onnx-model).</param>
    /// <param name="daemonHttpClient">Optional injected daemon client (tests).</param>
    /// <returns>The local embedding provider.</returns>
    /// <exception cref="EmbeddingConfigurationException">Configuration is invalid (e.g. non-loopback endpoint).</exception>
    public static ILocalEmbeddingProvider Create(
        EmbeddingProviderSettings settings,
        string bundledModelDirectory,
        HttpClient? daemonHttpClient = null)
    {
        return settings.Kind switch
        {
            EmbeddingProviderKind.BundledOnnx => new BundledOnnxEmbeddingProvider(bundledModelDirectory),
            EmbeddingProviderKind.LoopbackDaemon => new LoopbackDaemonEmbeddingProvider(
                settings.LoopbackEndpoint,
                settings.ModelTag,
                daemonHttpClient),
            _ => throw new EmbeddingConfigurationException($"Unknown embedding provider kind '{settings.Kind}'."),
        };
    }

    /// <summary>
    /// Validates provider settings without constructing the provider
    /// (configuration-surface validation; REST POST /api/settings/embedding/test
    /// enforces the same rule before any connection attempt).
    /// </summary>
    /// <param name="settings">Provider selection to validate.</param>
    /// <param name="error">Validation error when invalid.</param>
    /// <returns>True when the settings are acceptable.</returns>
    public static bool TryValidateSettings(EmbeddingProviderSettings settings, out string? error)
    {
        if (settings.Kind == EmbeddingProviderKind.LoopbackDaemon)
        {
            return LoopbackEndpointValidator.TryValidate(settings.LoopbackEndpoint, out error);
        }

        error = null;
        return true;
    }
}
