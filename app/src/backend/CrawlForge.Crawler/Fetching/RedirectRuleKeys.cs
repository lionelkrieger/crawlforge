namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Stable rule keys emitted by redirect analysis (CAP-A-010/011, CAP-B-049/050/051;
/// phase-09 WP-09-01 contract_tests payload list). Additive stable strings in
/// the open rule-key taxonomy (schema catalog: findings.rule key; precedent:
/// Audit/Indexability/IndexabilityRuleKeys.cs) — kept out of the locked
/// Contracts constants. Category per spec §7.4 taxonomy: "Indexability &amp;
/// status" covers redirect chains and loops. Severities are rule-definition
/// choices (§7.5: locally configurable thresholds; the spec does not fix
/// per-rule severities) — defaults approved by the phase-09 lead for WP-09-01.
/// </summary>
public static class RedirectRuleKeys
{
    /// <summary>A redirect chain revisits a URL (loop). Default severity: Error.</summary>
    public const string Loop = "redirects.loop";

    /// <summary>
    /// A redirect matches an "inappropriate" check. Default severity: Warning.
    /// SPEC GAP (loop §8, recorded by the lead): the architecture names the
    /// capability (CAP-B-051) but defines no checks. The check set is an
    /// explicit, configurable rule definition pending owner confirmation —
    /// see <see cref="RedirectInappropriatenessChecks"/>. NOT spec-derived.
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
}
