namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Rule keys for the Analytics &amp; Tagging category (FS-CRAWL-002 §14.3).
/// The five spec rule keys are locked contract constants
/// (<see cref="Contracts.Constants.FindingRuleKeys"/>); the rendering
/// diagnostic is a local additive key (WP-04-04).
/// </summary>
public static class MarTechRuleKeys
{
    /// <summary>Indexable page missing the site's primary container (Warning).</summary>
    public const string MissingContainer = Contracts.Constants.FindingRuleKeys.AnalyticsMissingContainer;

    /// <summary>Duplicated container injection or duplicate pageview hits (Warning).</summary>
    public const string DuplicateContainer = Contracts.Constants.FindingRuleKeys.AnalyticsDuplicateContainer;

    /// <summary>Malformed dataLayer payloads / schema violations (Warning).</summary>
    public const string DataLayerMalformed = Contracts.Constants.FindingRuleKeys.AnalyticsDataLayerMalformed;

    /// <summary>Tags firing before or without default consent declarations (Warning).</summary>
    public const string ConsentModeMissing = Contracts.Constants.FindingRuleKeys.AnalyticsConsentModeMissing;

    /// <summary>Container present but zero network hits while capture enabled (Notice).</summary>
    public const string NoHitsObserved = Contracts.Constants.FindingRuleKeys.AnalyticsNoHitsObserved;

    /// <summary>
    /// Local additive diagnostic: a rendering-dependent pass (consent
    /// timeline / dataLayer inspection) was not evaluated because rendering
    /// mode is disabled (FS-CRAWL-002 §14.2 explicit degradation notice).
    /// </summary>
    public const string RequiresRendering = "analytics.requires-rendering";
}

/// <summary>
/// Metric keys for MarTech metric_summaries contributions. The locked
/// contract keys (<see cref="Contracts.Constants.MetricKeys"/>) carry the
/// spec §14.4 dashboard metrics; the additive local keys carry the WP-04-04
/// dispatch metrics for phase-05 aggregation.
/// </summary>
public static class MarTechMetricKeys
{
    /// <summary>Locked: analytics container coverage (dimension: vendor).</summary>
    public const string AnalyticsCoverage = Contracts.Constants.MetricKeys.AnalyticsCoverage;

    /// <summary>Locked: consent compliance (1 compliant / 0 violating).</summary>
    public const string ConsentCompliance = Contracts.Constants.MetricKeys.ConsentCompliance;

    /// <summary>Additive local: detected MarTech stack size per page.</summary>
    public const string MarTechStack = "martech-stack";

    /// <summary>Additive local: tag-integrity issue count per page.</summary>
    public const string TagIntegrityIssues = "tag-integrity-issues";

    /// <summary>Additive local: consent-violation count per page.</summary>
    public const string ConsentViolations = "consent-violations";
}
