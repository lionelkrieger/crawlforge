namespace CrawlForge.Crawler.Capture;

/// <summary>
/// Builder-local stable rule keys emitted by the capture-mode coordinator
/// (WP-10-02). The locked <c>Contracts.Constants.FindingRuleKeys</c> carries the
/// render.*-delta family, <c>content.javascript-dependent-template</c> and
/// <c>render.service-worker-registered</c>; the key below is an additive stable
/// string in the open rule-key taxonomy pending promotion ( Contracts is
/// re-locked for wave 10A — the promotion is requested in the WP-10-02 wiring
/// handoff). Keys are never repurposed.
/// </summary>
public static class CaptureRuleKeys
{
    /// <summary>
    /// Warning: dual-mode stored captures exceeded the configurable storage
    /// threshold (FS-CRAWL-001 §5.6.1: "Above a configurable storage threshold
    /// the user is warned; there is no hard cap"; exclusion #8). Evidence carries
    /// the stored byte total and the threshold. NEVER enforced as a cap.
    /// </summary>
    public const string DualStorageWarning = "render.dual-storage-warning";
}
