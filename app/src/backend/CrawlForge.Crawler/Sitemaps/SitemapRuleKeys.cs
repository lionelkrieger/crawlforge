namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Stable rule keys emitted by sitemap-vs-crawl correlation (CAP-B-052/053/054;
/// phase-09 WP-09-01 contract_tests payload list). Additive stable strings in
/// the open rule-key taxonomy (schema catalog: findings.rule key; precedent:
/// Audit/Indexability/IndexabilityRuleKeys.cs) — kept out of the locked
/// Contracts constants. Keys are never repurposed; new keys arrive additively.
/// Category per spec §7.4 taxonomy: "Indexability &amp; status" covers sitemap
/// coverage. Severities are rule-definition choices (§7.5: locally configurable
/// thresholds; the spec does not fix per-rule severities) — defaults approved
/// by the phase-09 lead for WP-09-01.
/// </summary>
public static class SitemapRuleKeys
{
    /// <summary>A sitemap-listed URL was not crawled (missing from the crawl). Default severity: Warning.</summary>
    public const string MissingUrl = "sitemap.missing-url";

    /// <summary>A sitemap-listed URL is not search-indexable (CR-024 semantics). Default severity: Warning.</summary>
    public const string NonIndexable = "sitemap.non-indexable";

    /// <summary>A sitemap-listed URL redirects instead of serving content directly. Default severity: Warning.</summary>
    public const string Redirecting = "sitemap.redirecting";
}
