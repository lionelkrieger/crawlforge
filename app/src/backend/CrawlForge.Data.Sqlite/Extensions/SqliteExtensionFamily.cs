namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// The four required database capability families (FS-CRAWL-001 §2.4): the configured
/// provider must supply them natively or through loadable extensions. The embedded
/// provider bundles all four locally and probes them at startup.
/// </summary>
public enum SqliteExtensionFamily
{
    /// <summary>Regular-expression predicates in queries.</summary>
    Regex = 0,

    /// <summary>In-database delimited-text import/export.</summary>
    DelimitedText = 1,

    /// <summary>Vector-similarity search over stored embeddings.</summary>
    VectorSimilarity = 2,

    /// <summary>Extended mathematical and statistical functions.</summary>
    MathStatistics = 3,
}
