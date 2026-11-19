namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Detection policy for the pre-audit WAF fingerprinting pass (FS-CRAWL-002 §2).
/// Detection and reporting only — the policy never authorizes challenge solving,
/// replay, token forging, or evasion (exclusion E3); verdicts derive solely from
/// the local catalogue and the observed response (exclusion E4).
/// </summary>
public sealed record WafDetectionPolicy
{
    /// <summary>Default confidence threshold for a corroborated challenge verdict.</summary>
    public const double DefaultConfidenceThreshold = 0.8;

    /// <summary>Default minimum number of independent corroborating facets.</summary>
    public const int DefaultMinimumCorroboratingFacets = 2;

    /// <summary>Default body-snippet bound for the pre-parse stage (64 KiB).</summary>
    public const int DefaultBodySnippetLimit = 64 * 1024;

    /// <summary>Default node bound for the Stage B pre-parse tag scan (§2.2).</summary>
    public const int DefaultStageBNodeLimit = 2000;

    /// <summary>Default site-wide block-rate warning threshold (1%).</summary>
    public const double DefaultBlockRateWarningThreshold = 0.01;

    /// <summary>Default site-wide block-rate error threshold (5%).</summary>
    public const double DefaultBlockRateErrorThreshold = 0.05;

    /// <summary>Default minimum URL sample for the site-wide block-rate diagnostic.</summary>
    public const int DefaultMinimumUrlsForBlockRate = 20;

    /// <summary>The documented default policy.</summary>
    public static WafDetectionPolicy Default { get; } = new();

    /// <summary>Whether the fingerprinting pass is enabled (application_settings WAF toggle).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Minimum aggregate confidence for a challenge verdict. A corroborated
    /// candidate below this threshold is reported as suspected, never blocked.
    /// </summary>
    public double ConfidenceThreshold { get; init; } = DefaultConfidenceThreshold;

    /// <summary>
    /// Minimum number of independent facets (header, cookie, body) that must
    /// corroborate before a response may be classified as a challenge. A
    /// single-facet hit never classifies as blocked, regardless of confidence.
    /// </summary>
    public int MinimumCorroboratingFacets { get; init; } = DefaultMinimumCorroboratingFacets;

    /// <summary>
    /// Maximum number of leading body characters inspected by the bounded
    /// pre-parse stage. The pass never parses the full document.
    /// </summary>
    public int BodySnippetLimit { get; init; } = DefaultBodySnippetLimit;

    /// <summary>
    /// Maximum number of start tags (and script elements) inspected by the
    /// Stage B pre-parse for dom-marker/script facets (§2.2 bound).
    /// </summary>
    public int StageBNodeLimit { get; init; } = DefaultStageBNodeLimit;

    /// <summary>Site-wide block rate at or above which a Warning finding is reported.</summary>
    public double BlockRateWarningThreshold { get; init; } = DefaultBlockRateWarningThreshold;

    /// <summary>Site-wide block rate at or above which an Error finding is reported.</summary>
    public double BlockRateErrorThreshold { get; init; } = DefaultBlockRateErrorThreshold;

    /// <summary>Minimum crawled URL count before the block-rate diagnostic reports.</summary>
    public int MinimumUrlsForBlockRate { get; init; } = DefaultMinimumUrlsForBlockRate;
}
