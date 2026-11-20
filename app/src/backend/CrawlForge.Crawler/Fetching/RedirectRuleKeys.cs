using CrawlForge.Contracts.Constants;

namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Stable rule keys emitted by redirect analysis (CAP-A-010/011, CAP-B-049/050/051).
/// The four CR-029 inappropriate-redirect case keys are LOCKED
/// <see cref="FindingRuleKeys"/> constants (WP-10-A1) — aliased here, never
/// re-spelled. Loop and chain keys remain additive builder-local strings in
/// the open rule-key taxonomy (they were not promoted to the locked catalog;
/// see the phase-10 handoff). Category per spec §7.4 taxonomy: "Indexability
/// &amp; status" covers redirect chains, loops and inappropriate redirects.
/// Severities are rule-definition choices (§7.5) — defaults approved by the
/// phase-09 lead for WP-09-01, kept by CR-029.
/// </summary>
public static class RedirectRuleKeys
{
    /// <summary>A redirect chain revisits a URL (loop). Default severity: Error.</summary>
    public const string Loop = "redirects.loop";

    /// <summary>
    /// LEGACY aggregate key (phase 09). CR-029 split the inappropriate-redirect
    /// rule into the four case keys below; the analyzer no longer emits this
    /// key. The constant is retained only so existing rule-catalog seeds keep
    /// compiling until the coordinator replaces the seed (phase-10 handoff).
    /// </summary>
    public const string Inappropriate = "redirects.inappropriate";

    /// <summary>
    /// A multi-hop redirect chain (2+ hops) was recorded hop by hop
    /// (CAP-B-049 reporting surface alongside the URL-detail chain).
    /// Default severity: Warning for 2+ hops; single-hop redirects are normal
    /// and emit no finding. Chain-cap-exceeded violations reuse this key with
    /// Error severity and a <c>violation</c> evidence field.
    /// </summary>
    public const string Chain = "redirects.chain";

    /// <summary>CR-029 case 1: a hop redirects to a different host. Locked key.</summary>
    public const string InappropriateCrossHost = FindingRuleKeys.RedirectsInappropriateCrossHost;

    /// <summary>CR-029 case 2: a hop uses a temporary redirect status. Locked key.</summary>
    public const string InappropriateTemporaryStatus = FindingRuleKeys.RedirectsInappropriateTemporaryStatus;

    /// <summary>CR-029 case 3: the final target is non-indexable per CR-024. Locked key.</summary>
    public const string InappropriateNonIndexableTarget = FindingRuleKeys.RedirectsInappropriateNonIndexableTarget;

    /// <summary>CR-029 case 4: an HTTPS URL redirects to HTTP. Locked key.</summary>
    public const string InappropriateHttpsDowngrade = FindingRuleKeys.RedirectsInappropriateHttpsDowngrade;

    /// <summary>
    /// Maps one fired check to its locked case rule key (CR-029: each case
    /// emits its own finding).
    /// </summary>
    /// <param name="check">A single fired check flag.</param>
    /// <returns>The locked case rule key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The flag is not a single known case.</exception>
    public static string ForCase(RedirectInappropriatenessChecks check) => check switch
    {
        RedirectInappropriatenessChecks.CrossHost => InappropriateCrossHost,
        RedirectInappropriatenessChecks.TemporaryStatus => InappropriateTemporaryStatus,
        RedirectInappropriatenessChecks.NonIndexableTarget => InappropriateNonIndexableTarget,
        RedirectInappropriatenessChecks.HttpsDowngrade => InappropriateHttpsDowngrade,
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "Not a single CR-029 case flag."),
    };

    /// <summary>
    /// The stable case name used in finding evidence (matches the locked key
    /// suffix and the configuration toggle names).
    /// </summary>
    /// <param name="check">A single fired check flag.</param>
    /// <returns>The hyphenated case name.</returns>
    public static string CaseName(RedirectInappropriatenessChecks check) => check switch
    {
        RedirectInappropriatenessChecks.CrossHost => "cross-host",
        RedirectInappropriatenessChecks.TemporaryStatus => "temporary-status",
        RedirectInappropriatenessChecks.NonIndexableTarget => "non-indexable-target",
        RedirectInappropriatenessChecks.HttpsDowngrade => "https-downgrade",
        _ => check.ToString(),
    };
}
