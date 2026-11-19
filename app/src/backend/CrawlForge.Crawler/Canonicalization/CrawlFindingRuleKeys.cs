namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Stable finding rule keys produced by the WP-09-02 crawl-engine components
/// (FS-CRAWL-001 §5.2/§5.11/§5.15, FS-CRAWL-002 §2). Additive keys only — the
/// locked <c>Contracts/Constants/FindingRuleKeys.cs</c> vocabulary is never
/// repurposed. The coordinator appends these to the locked constants file and
/// the audit rule catalogue during serial wiring (WP-09-11).
/// </summary>
public static class CrawlFindingRuleKeys
{
    /// <summary>
    /// A URL was blocked from fetching by robots.txt. Identical stable string to
    /// <c>CrawlForge.Audit.Indexability.IndexabilityRuleKeys.RobotsBlocked</c> so
    /// crawl-time emission and the offline indexability analyzer agree on one key.
    /// </summary>
    public const string RobotsBlocked = "indexability.robots-blocked";

    /// <summary>
    /// Notice: an ASP.NET cookieless session segment was removed during
    /// canonicalization (CR-009; fixture trap <c>cookieless-session</c>).
    /// </summary>
    public const string SessionSegmentRemoved = "canonicalization.session-segment-removed";

    /// <summary>
    /// Warning: a cookieless crawl hit a redirect loop that a cookie jar would
    /// have resolved (fixture trap <c>cookie-gate</c>, cookieless mode).
    /// </summary>
    public const string CookielessRedirectLoop = "crawl.cookieless-redirect-loop";

    /// <summary>
    /// Warning: material crawler-vs-browser differences (possible cloaking,
    /// dynamic rendering or bot blocking; FS-CRAWL-001 §5.15).
    /// </summary>
    public const string UaComparisonMaterialDifference = "ua-comparison.material-difference";
}
