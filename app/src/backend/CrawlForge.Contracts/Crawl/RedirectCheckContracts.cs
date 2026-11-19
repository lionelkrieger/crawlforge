namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Inappropriate-redirect check toggles (CR-029, applied; FS-CRAWL-001 §7.4
/// Indexability &amp; status row; amendment WP-10-A1, D-021). The redirect check
/// flags four cases, each an independent toggle on the configuration editor's
/// analysis tab, all on by default. Carried by the additive nullable
/// <c>CrawlConfigurationDto.RedirectChecks</c> member — null means all four
/// checks enabled (the default when absent). Each emitted finding names the
/// case, the triggering hop and the final target, using the case rule keys
/// <c>FindingRuleKeys.RedirectsInappropriate*</c>.
/// </summary>
/// <param name="CrossHost">Case 1: a hop redirects to a different host.
/// Default true.</param>
/// <param name="TemporaryStatus">Case 2: a hop uses a temporary redirect status
/// (302/303/307). Default true.</param>
/// <param name="NonIndexableTarget">Case 3: the final target is non-indexable
/// per CR-024 (search-indexable semantics, including canonical-elsewhere).
/// Default true.</param>
/// <param name="HttpsDowngrade">Case 4: an HTTPS URL redirects to HTTP.
/// Default true.</param>
public sealed record RedirectCheckSettings(
    bool CrossHost = true,
    bool TemporaryStatus = true,
    bool NonIndexableTarget = true,
    bool HttpsDowngrade = true)
{
    /// <summary>All four checks enabled (the CR-029 default).</summary>
    public static RedirectCheckSettings AllEnabled { get; } = new();

    /// <summary>All four checks disabled.</summary>
    public static RedirectCheckSettings AllDisabled { get; } = new(false, false, false, false);
}