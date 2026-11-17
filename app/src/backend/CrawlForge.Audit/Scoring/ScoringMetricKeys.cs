namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Stable metric keys owned by the scoring area (FS-CRAWL-001 §8.1:
/// aggregates keyed by audit and metric with a dimension payload). All keys
/// carry the <c>scoring.</c> prefix so the suppression-aware rewrite never
/// touches metric rows owned by other areas (for example <c>waf-blocks</c>).
/// Keys are stable strings; additive keys may be appended, existing keys are
/// never repurposed.
/// </summary>
public static class ScoringMetricKeys
{
    /// <summary>Total non-suppressed finding count (dimensions: none).</summary>
    public const string FindingsTotal = "scoring.findings.total";

    /// <summary>Non-suppressed finding count per severity (dimension: severity).</summary>
    public const string FindingsBySeverity = "scoring.findings.by-severity";

    /// <summary>Non-suppressed finding count per category (dimension: category).</summary>
    public const string FindingsByCategory = "scoring.findings.by-category";

    /// <summary>Suppressed finding count retained for inspection (dimensions: none).</summary>
    public const string SuppressedTotal = "scoring.suppressed.total";

    /// <summary>Total crawled URL count (dimensions: none).</summary>
    public const string UrlsTotal = "scoring.urls.total";

    /// <summary>Indexable URL count used as scoring coverage (dimensions: none).</summary>
    public const string UrlsIndexable = "scoring.urls.indexable";

    /// <summary>Dimension payload key for the severity breakdown.</summary>
    public const string SeverityDimension = "severity";

    /// <summary>Dimension payload key for the category breakdown.</summary>
    public const string CategoryDimension = "category";

    /// <summary>All metric keys owned by the scoring rewrite.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        FindingsTotal,
        FindingsBySeverity,
        FindingsByCategory,
        SuppressedTotal,
        UrlsTotal,
        UrlsIndexable,
    ];
}
