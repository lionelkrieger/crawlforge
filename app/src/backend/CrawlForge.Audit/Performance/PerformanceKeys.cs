namespace CrawlForge.Audit.Performance;

/// <summary>
/// Stable performance metric keys (FS-CRAWL-001 §7.11) contributed to
/// metric_summaries. Additive-only: existing keys are never repurposed.
/// Documented here for phase-05 aggregation (histograms, worst pages,
/// budget pass-rate).
/// </summary>
public static class PerformanceMetricKeys
{
    /// <summary>Largest Contentful Paint, milliseconds.</summary>
    public const string Lcp = "performance.lcp";

    /// <summary>Cumulative Layout Shift, unitless score.</summary>
    public const string Cls = "performance.cls";

    /// <summary>Interaction to Next Paint lab proxy, milliseconds.</summary>
    public const string InpProxy = "performance.inp-proxy";

    /// <summary>Time to First Byte, milliseconds (from the fetch).</summary>
    public const string Ttfb = "performance.ttfb";

    /// <summary>First Contentful Paint, milliseconds.</summary>
    public const string Fcp = "performance.fcp";

    /// <summary>Total Blocking Time, milliseconds.</summary>
    public const string Tbt = "performance.tbt";

    /// <summary>Used script + stylesheet bytes during render.</summary>
    public const string CoverageUsedBytes = "performance.code-coverage-used-bytes";

    /// <summary>Total fetched script + stylesheet bytes.</summary>
    public const string CoverageTotalBytes = "performance.code-coverage-total-bytes";

    /// <summary>Unused code percentage (0–100).</summary>
    public const string CoverageUnusedPercent = "performance.code-coverage-unused-percent";

    /// <summary>Total DOM element nodes.</summary>
    public const string DomNodes = "performance.dom-nodes";

    /// <summary>Maximum DOM tree depth.</summary>
    public const string DomMaxDepth = "performance.dom-max-depth";

    /// <summary>Maximum DOM tree width (children of one node).</summary>
    public const string DomMaxWidth = "performance.dom-max-width";

    /// <summary>Image element count.</summary>
    public const string ImageCount = "performance.image-count";

    /// <summary>Transferred image bytes (resource timing).</summary>
    public const string ImageBytes = "performance.image-bytes";

    /// <summary>Smallest rendered font size, CSS pixels.</summary>
    public const string MinFontSizePx = "performance.min-font-size-px";

    /// <summary>Total transferred page bytes (document body).</summary>
    public const string TotalBytes = "performance.total-bytes";

    /// <summary>Proxy-captured request count for the page.</summary>
    public const string RequestCount = "performance.request-count";
}

/// <summary>
/// Stable performance finding rule keys (FS-CRAWL-001 §7.11). Additive-only.
/// </summary>
public static class PerformanceRuleKeys
{
    /// <summary>LCP band finding (good/needs-improvement/poor).</summary>
    public const string Lcp = "performance.lcp";

    /// <summary>CLS band finding (good/needs-improvement/poor).</summary>
    public const string Cls = "performance.cls";

    /// <summary>INP-proxy band finding (good/needs-improvement/poor).</summary>
    public const string Inp = "performance.inp";

    /// <summary>A user-defined performance budget was exceeded.</summary>
    public const string BudgetExceeded = "performance.budget-exceeded";

    /// <summary>A budget metric could not be evaluated for the page.</summary>
    public const string BudgetNotEvaluated = "performance.budget-not-evaluated";

    /// <summary>A large unused script/stylesheet payload was measured.</summary>
    public const string UnusedCode = "performance.unused-code";

    /// <summary>Render-dependent measurement was skipped (rendering off).</summary>
    public const string RequiresRendering = "performance.requires-rendering";

    /// <summary>The lab probe could not measure the page (local failure).</summary>
    public const string ProbeUnavailable = "performance.probe-unavailable";
}
