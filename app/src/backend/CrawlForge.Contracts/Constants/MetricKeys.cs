namespace CrawlForge.Contracts.Constants;

/// <summary>
/// Metric keys introduced by the extension specification
/// (FS-CRAWL-002 Appendix A, metric_summaries row). Metric keys are stable
/// strings; additive keys may be appended, existing keys are never repurposed.
/// </summary>
public static class MetricKeys
{
    /// <summary>Count of WAF-blocked URLs.</summary>
    public const string WafBlocks = "waf-blocks";

    /// <summary>Rate of WAF-blocked URLs.</summary>
    public const string WafBlockRate = "waf-block-rate";

    /// <summary>Per-host AIMD throttle summary.</summary>
    public const string HostThrottle = "host-throttle";

    /// <summary>Word count within the resolved content scope.</summary>
    public const string ScopedWordCount = "scoped-word-count";

    /// <summary>Top site-wide n-grams.</summary>
    public const string NgramTop = "ngram-top";

    /// <summary>Count of keyword-cannibalization clusters.</summary>
    public const string CannibalizationClusters = "cannibalization-clusters";

    /// <summary>Count of topical gaps.</summary>
    public const string TopicalGaps = "topical-gaps";

    /// <summary>Redirect-matrix validation summary.</summary>
    public const string RedirectMatrix = "redirect-matrix";

    /// <summary>Count of true orphans from log correlation.</summary>
    public const string LogTrueOrphans = "log-true-orphans";

    /// <summary>Crawl-budget waste from log correlation.</summary>
    public const string LogBudgetWaste = "log-budget-waste";

    /// <summary>Count of uncrawled indexable URLs from log correlation.</summary>
    public const string LogUncrawledIndexable = "log-uncrawled-indexable";

    /// <summary>Bot hit counts from log imports.</summary>
    public const string LogBotHits = "log-bot-hits";

    /// <summary>Count of semantic cannibalization clusters.</summary>
    public const string SemanticClusters = "semantic-clusters";

    /// <summary>Count of semantic link opportunities.</summary>
    public const string SemanticOpportunities = "semantic-opportunities";

    /// <summary>GEO readiness score.</summary>
    public const string GeoReadiness = "geo-readiness";

    /// <summary>Governance-matrix coverage.</summary>
    public const string GovernanceCoverage = "governance-coverage";

    /// <summary>Analytics container coverage.</summary>
    public const string AnalyticsCoverage = "analytics-coverage";

    /// <summary>Consent Mode compliance.</summary>
    public const string ConsentCompliance = "consent-compliance";

    /// <summary>Weighted link equity.</summary>
    public const string LinkEquityWeighted = "link-equity-weighted";

    /// <summary>SERP pixel truncation counts.</summary>
    public const string SerpTruncation = "serp-truncation";
}
