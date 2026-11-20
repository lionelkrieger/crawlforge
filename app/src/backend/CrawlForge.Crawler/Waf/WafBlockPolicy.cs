using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Waf;

/// <summary>
/// Politeness response to a corroborated WAF challenge (FS-CRAWL-002 §2.3.1
/// challenge classes, §2.5 AIMD coupling; phase-10 D-021). Detection and
/// reporting only — no challenge solving or evasion (exclusion E3).
/// </summary>
public enum WafBlockResponse
{
    /// <summary>No challenge: the response feeds the normal AIMD observation path.</summary>
    None = 0,

    /// <summary>
    /// Soft block: feed the challenge observation to the AIMD controller
    /// (multiplicative decrease, trigger <c>waf-soft-block</c>, cooldown D).
    /// </summary>
    AimdThrottle = 1,

    /// <summary>
    /// Hard block: stop the host for this audit (no further fetches of the
    /// host; the blocked URL is never re-fetched). Host-stop takes precedence
    /// over throttling — the engine owns the stop wiring.
    /// </summary>
    HostStop = 2,
}

/// <summary>
/// Five-class WAF block policy (FS-CRAWL-002 §2.3.1; phase-10 D-021): the
/// catalogue's five challenge classes split into soft blocks that throttle
/// through AIMD and hard blocks that stop the host.
/// <list type="bullet">
/// <item>Soft: <see cref="WafChallengeClass.ManagedChallenge"/>
/// ("managed-challenge") and <see cref="WafChallengeClass.RateLimitSoftBlock"/>
/// ("rate-limit-soft-block") — the site is reachable but rate-gated; the AIMD
/// multiplicative decrease backs off and the crawl continues.</item>
/// <item>Hard: <see cref="WafChallengeClass.Captcha"/>,
/// <see cref="WafChallengeClass.Interstitial"/> ("interstitial" — locked wire,
/// see <see cref="EnumWireNames"/> deviation note) and
/// <see cref="WafChallengeClass.BotBlock"/> ("botblock" — locked wire) — the
/// crawler cannot proceed past the checkpoint; the host is stopped.</item>
/// <item><see cref="WafChallengeClass.Unknown"/>: a corroborated challenge whose
/// class the catalogue does not establish keeps the conservative phase-09
/// behaviour (AIMD throttle) — never an implicit host stop.</item>
/// </list>
/// Pure and stateless; the engine consults this policy at the WAF record path
/// and wires the AIMD observation / host stop accordingly (coordinator wiring).
/// </summary>
public static class WafBlockPolicy
{
    /// <summary>
    /// Classifies the politeness response for one challenge class.
    /// </summary>
    /// <param name="challengeClass">The verdict's challenge class.</param>
    /// <returns>Soft-block throttle, hard-block host stop, or the conservative
    /// throttle for an unestablished class.</returns>
    public static WafBlockResponse Classify(WafChallengeClass challengeClass) => challengeClass switch
    {
        WafChallengeClass.ManagedChallenge => WafBlockResponse.AimdThrottle,
        WafChallengeClass.RateLimitSoftBlock => WafBlockResponse.AimdThrottle,
        WafChallengeClass.Captcha => WafBlockResponse.HostStop,
        WafChallengeClass.Interstitial => WafBlockResponse.HostStop,
        WafChallengeClass.BotBlock => WafBlockResponse.HostStop,
        // Unclassified corroborated challenge: conservative soft response
        // (phase-09 baseline — every challenge throttled through AIMD).
        _ => WafBlockResponse.AimdThrottle,
    };

    /// <summary>Whether the class is a soft block (AIMD throttle).</summary>
    /// <param name="challengeClass">The verdict's challenge class.</param>
    /// <returns>True for managed-challenge and rate-limit-soft-block.</returns>
    public static bool IsSoftBlock(WafChallengeClass challengeClass) =>
        Classify(challengeClass) == WafBlockResponse.AimdThrottle
        && challengeClass is WafChallengeClass.ManagedChallenge or WafChallengeClass.RateLimitSoftBlock;

    /// <summary>Whether the class is a hard block (host stop).</summary>
    /// <param name="challengeClass">The verdict's challenge class.</param>
    /// <returns>True for captcha, interstitial and botblock.</returns>
    public static bool IsHardBlock(WafChallengeClass challengeClass) =>
        Classify(challengeClass) == WafBlockResponse.HostStop;

    /// <summary>
    /// Resolves the response for a full verdict: a non-challenge never triggers
    /// the WAF path (status/latency backoff belongs to the parent AIMD flow).
    /// </summary>
    /// <param name="verdict">The WAF evaluation verdict.</param>
    /// <returns>The politeness response for the verdict.</returns>
    public static WafBlockResponse Resolve(CrawlForge.Contracts.Crawl.WafEvaluationResult verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return verdict.IsChallenge ? Classify(verdict.ChallengeClass) : WafBlockResponse.None;
    }
}
