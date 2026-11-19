using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Explicit per-URL-pattern capture override (FS-CRAWL-001 §5.6.1 manual
/// override; amendment WP-10-A1, D-021). The user may set the capture for any
/// URL pattern explicitly (rendered or non-rendered); explicit settings take
/// precedence over adaptive switching and apply on top of every capture mode.
/// </summary>
/// <param name="Pattern">URL path pattern the override applies to (for example
/// "/blog/*"; the matching semantics are the engine's URL-pattern grouping).</param>
/// <param name="Capture">Rendered or non-rendered capture for matching URLs.</param>
public sealed record CapturePatternOverride(
    string Pattern,
    PatternCaptureMode Capture);

/// <summary>
/// Capture-mode settings document (FS-CRAWL-001 §5.6.1/§5.6.2; amendment
/// WP-10-A1, D-021). Carried by the additive nullable
/// <c>CrawlConfigurationDto.CaptureSettings</c> member — null keeps the engine
/// defaults below. Every knob is locally configurable; nothing here is
/// license/account-driven (exclusion #8) and no threshold is a hard cap unless
/// the spec names one (the render wait hard cap is the only hard bound).
/// </summary>
/// <param name="AdaptiveSampleSizePerGroup">Adaptive mode: number of URLs sampled
/// per URL-path-pattern group for the render comparison. Spec default range is
/// 3–5 (§5.6.1); the application default is 4 (the range midpoint).</param>
/// <param name="MaterialChangeWordCountThreshold">Adaptive mode: visible
/// word-count change fraction above which a sample counts as materially changed
/// (§5.6.1: "above 20%", threshold configurable). Default 0.20. Any title,
/// canonical, meta-robots or hreflang difference, or a different link set, is
/// material regardless of this threshold.</param>
/// <param name="DualStorageWarningThresholdBytes">Dual mode: stored-capture byte
/// total above which the user is warned (§5.6.1: "Above a configurable storage
/// threshold the user is warned; there is no hard cap"). Application default
/// 1 GiB; configurable, never enforced as a cap.</param>
/// <param name="RenderWaitQuietMs">Render wait rule: DOM-and-network quiet period
/// in milliseconds (§5.6.2, CR-009). Default 500; configurable.</param>
/// <param name="RenderWaitHardCapMs">Render wait rule: hard cap in milliseconds
/// (§5.6.2, CR-009). Default 20000; configurable. Fixed delays are not used.</param>
/// <param name="RenderingPoolSize">Concurrent Chromium contexts. Null applies the
/// engine default: half the logical processors, minimum 2 (§5.6.2; see
/// <see cref="DefaultRenderingPoolSize"/>).</param>
/// <param name="PatternOverrides">Explicit per-URL-pattern capture overrides;
/// null/empty means none. Overrides win over adaptive switching and apply on top
/// of every mode (§5.6.1).</param>
public sealed record CaptureSettings(
    int AdaptiveSampleSizePerGroup = 4,
    double MaterialChangeWordCountThreshold = 0.20,
    long DualStorageWarningThresholdBytes = 1_073_741_824,
    int RenderWaitQuietMs = 500,
    int RenderWaitHardCapMs = 20_000,
    int? RenderingPoolSize = null,
    IReadOnlyList<CapturePatternOverride>? PatternOverrides = null)
{
    /// <summary>Default settings (every knob at its application default).</summary>
    public static CaptureSettings Default { get; } = new();

    /// <summary>
    /// Engine default rendering pool size: half the logical processors, minimum 2
    /// (FS-CRAWL-001 §5.6.2). Used when <see cref="RenderingPoolSize"/> is null.
    /// </summary>
    public static int DefaultRenderingPoolSize => Math.Max(2, Environment.ProcessorCount / 2);
}