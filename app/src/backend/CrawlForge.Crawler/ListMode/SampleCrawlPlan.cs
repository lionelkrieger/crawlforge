using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Resolved plan of a sample-crawl run (FS-CRAWL-001 §5.1: "a depth/page-bounded
/// sampling run for a quick preview, using the same pipeline and limits
/// resolution"). The plan carries the effective limits produced by the
/// STANDARD <see cref="ILimitsResolver"/> priority order (audit → project →
/// application → built-in) with the sample bounds expressed as the per-audit
/// override layer through <see cref="SampleCrawlLimits.ToAuditOverrides"/> —
/// no sample-specific resolution path exists, and no other layer is consulted
/// or altered (criterion #34; precedence rule 5).
/// </summary>
/// <param name="Settings">The user-configured sample bounds.</param>
/// <param name="Limits">Effective limits after standard resolution.</param>
/// <param name="AuditOverrides">The per-audit override layer carrying the sample bounds.</param>
public sealed record SampleCrawlPlan(
    SampleCrawlSettings Settings,
    ResolvedCrawlLimits Limits,
    CrawlLimitOverrides AuditOverrides);

/// <summary>
/// Builds the sample-crawl plan from user bounds and the configured limits
/// layers. Purely local configuration work — the component never fetches and
/// never resolves limits through any path other than the locked
/// <see cref="ILimitsResolver"/>. The engine runs the sample through the same
/// crawl pipeline with <see cref="SampleCrawlPlan.Limits"/> and admits URLs
/// through <see cref="CrawlAdmissionGate"/> configured with the same bounds.
/// </summary>
public static class SampleCrawlPlanner
{
    /// <summary>
    /// Resolves the effective limits of a sample run. The sample bounds become
    /// the per-audit override layer; project and application defaults pass
    /// through unchanged, so a stricter project/application ceiling still wins
    /// wherever the resolver's priority order dictates.
    /// </summary>
    /// <param name="settings">User-configured sample bounds (validated).</param>
    /// <param name="limitsResolver">The locked standard limits resolver.</param>
    /// <param name="projectDefaults">Project-level defaults, if any.</param>
    /// <param name="applicationDefaults">Application-level defaults, if any.</param>
    /// <returns>The resolved sample plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The sample bounds are invalid.</exception>
    public static SampleCrawlPlan Plan(
        SampleCrawlSettings settings,
        ILimitsResolver limitsResolver,
        CrawlLimitOverrides? projectDefaults = null,
        CrawlLimitOverrides? applicationDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limitsResolver);

        var auditOverrides = SampleCrawlLimits.ToAuditOverrides(settings);
        var limits = limitsResolver.Resolve(auditOverrides, projectDefaults, applicationDefaults);
        return new SampleCrawlPlan(settings, limits, auditOverrides);
    }
}
