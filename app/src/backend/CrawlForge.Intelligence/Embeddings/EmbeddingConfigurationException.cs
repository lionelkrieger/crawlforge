namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Embedding provider configuration failure (FS-CRAWL-002 §12.2): raised at
/// configuration time for invalid provider settings — most importantly any
/// non-loopback daemon endpoint (exclusion E7). Configuration failures never
/// fail an audit; the semantic passes degrade to unavailable with a local
/// diagnostic.
/// </summary>
public sealed class EmbeddingConfigurationException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    /// <param name="message">Configuration error detail.</param>
    public EmbeddingConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    /// <param name="message">Configuration error detail.</param>
    /// <param name="innerException">Underlying cause.</param>
    public EmbeddingConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
