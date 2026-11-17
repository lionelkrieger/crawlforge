using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Outcome of applying the render-resolution policy to a challenge verdict
/// (FS-CRAWL-002 §2): rendering may resolve a challenge, confirm it, or leave
/// a suspected hit unclassified. Resolution is an observation about the
/// rendered response — never a solving or bypass action (exclusion E3).
/// </summary>
public enum WafRenderOutcome
{
    /// <summary>No challenge and no suspected hit on the original response.</summary>
    NoChallenge = 0,

    /// <summary>The original challenge no longer appears after rendering.</summary>
    ResolvedByRender = 1,

    /// <summary>The challenge persists after rendering (or rendering was not attempted).</summary>
    StillBlocked = 2,

    /// <summary>Only a sub-threshold or single-facet hit was observed.</summary>
    SuspectedOnly = 3,
}

/// <summary>
/// Render-resolution verdict with the notice rule key to report, when any.
/// </summary>
/// <param name="Outcome">Resolution outcome.</param>
/// <param name="NoticeRuleKey">Notice finding rule key, or null when no notice applies.</param>
public sealed record WafRenderResolution(WafRenderOutcome Outcome, string? NoticeRuleKey);

/// <summary>
/// The render-resolution policy (FS-CRAWL-002 §2): a challenge that disappears
/// in the rendered response is annotated <c>challenge-resolved-by-render</c>;
/// a sub-threshold or single-facet hit is annotated <c>suspected-challenge</c>.
/// Pure decision logic — detection and reporting only (exclusion E3).
/// </summary>
public static class WafRenderResolutionPolicy
{
    /// <summary>Resolves an original verdict against the rendered-response verdict.</summary>
    /// <param name="original">Verdict on the originally received response.</param>
    /// <param name="rendered">Verdict on the rendered response, or null when rendering was not attempted.</param>
    /// <returns>The resolution outcome and notice rule key.</returns>
    public static WafRenderResolution Resolve(WafEvaluationResult original, WafEvaluationResult? rendered)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!original.IsChallenge)
        {
            return original.MatchedSignatureKeys.Count > 0
                ? new WafRenderResolution(WafRenderOutcome.SuspectedOnly, FindingRuleKeys.SuspectedChallenge)
                : new WafRenderResolution(WafRenderOutcome.NoChallenge, null);
        }

        if (rendered is { IsChallenge: false })
        {
            return new WafRenderResolution(WafRenderOutcome.ResolvedByRender, FindingRuleKeys.ChallengeResolvedByRender);
        }

        return new WafRenderResolution(WafRenderOutcome.StillBlocked, null);
    }
}

/// <summary>
/// Content-finding exclusion for WAF-blocked URLs (FS-CRAWL-002 §2): a URL
/// classified blocked with the waf-challenge sub-classification never received
/// its real content, so thin-content and duplicate-title rules must not report
/// findings for it. Content rules consume the persisted classification facts
/// (url_records.status_class and block_reason) through this predicate.
/// </summary>
public static class WafContentFindingExclusion
{
    /// <summary>
    /// Whether content-quality findings (thin content, duplicate title, and
    /// siblings) must be skipped for a URL with the given classification.
    /// </summary>
    /// <param name="statusClass">Persisted crawl status class of the URL.</param>
    /// <param name="blockReason">Persisted block reason, when any.</param>
    /// <returns>True when the URL is WAF-blocked and content findings are excluded.</returns>
    public static bool IsExcluded(CrawlStatusClass statusClass, string? blockReason) =>
        statusClass == CrawlStatusClass.Blocked
        && string.Equals(blockReason, WafDetectionRecorder.WafChallengeBlockReason, StringComparison.Ordinal);
}
