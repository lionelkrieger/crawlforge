namespace CrawlForge.Crawler.Frontier;

/// <summary>
/// Stable rule keys emitted by link-graph crawl findings (CAP-B-046/047/048,
/// CAP-E-122; phase-09 WP-09-01 deliverables). Additive stable strings in the
/// open rule-key taxonomy (schema catalog: findings.rule key; precedent:
/// Audit/Indexability/IndexabilityRuleKeys.cs) — kept out of the locked
/// Contracts constants. Categories per spec §7.4 taxonomy (lead-verified):
/// orphan pages belong to "Indexability &amp; status"; broken internal and
/// external links belong to "Linking". Severities are rule-definition choices
/// (§7.5: locally configurable thresholds; the spec does not fix per-rule
/// severities) — defaults approved by the phase-09 lead for WP-09-01.
/// </summary>
public static class LinkRuleKeys
{
    /// <summary>
    /// A crawled URL has zero internal in-links from another URL (self-loops
    /// excluded) — an orphan page. Default severity: Warning.
    /// Category: Indexability (spec §7.4; the Contracts enum doc-comment
    /// mentioning orphan under Linking is a notation slip recorded by the lead).
    /// </summary>
    public const string Orphan = "links.orphan";

    /// <summary>An internal link target returned a 4xx/5xx status or failed to fetch. Default severity: Error.</summary>
    public const string BrokenInternal = "links.broken-internal";

    /// <summary>An external link target failed (DNS/connect/TLS/timeout) or returned 4xx/5xx. Default severity: Warning.</summary>
    public const string BrokenExternal = "links.broken-external";
}
