using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Audit.Performance;

/// <summary>
/// One lab measurement request against the crawl target (offline: the probe
/// navigates only to the supplied URL; no remote resolution exists).
/// </summary>
/// <param name="Url">Crawl-target URL to measure.</param>
/// <param name="Device">Device emulation profile.</param>
/// <param name="TimeoutSeconds">Optional per-measurement timeout.</param>
/// <param name="CaptureCoverage">Whether to capture JS/CSS code coverage.</param>
public sealed record LabVitalsRequest(
    string Url,
    DeviceProfile Device,
    int? TimeoutSeconds = null,
    bool CaptureCoverage = true);

/// <summary>
/// One script/stylesheet coverage entry (used vs total bytes during render).
/// </summary>
/// <param name="Url">Resource URL; inline code carries the page URL.</param>
/// <param name="Kind">Resource kind: "js" or "css".</param>
/// <param name="UsedBytes">Bytes executed/applied during render.</param>
/// <param name="TotalBytes">Total fetched bytes of the resource.</param>
public sealed record CoverageEntry(string Url, string Kind, long UsedBytes, long TotalBytes);

/// <summary>
/// Code-coverage sample (FS-CRAWL-001 §7.11): the fraction of fetched
/// stylesheet and script bytes actually used during render.
/// </summary>
/// <param name="JsUsedBytes">Executed script bytes.</param>
/// <param name="JsTotalBytes">Total fetched script bytes.</param>
/// <param name="CssUsedBytes">Applied stylesheet bytes.</param>
/// <param name="CssTotalBytes">Total fetched stylesheet bytes.</param>
/// <param name="Entries">Per-resource coverage entries.</param>
public sealed record CodeCoverageSample(
    long JsUsedBytes,
    long JsTotalBytes,
    long CssUsedBytes,
    long CssTotalBytes,
    IReadOnlyList<CoverageEntry> Entries);

/// <summary>
/// Page composition metrics (FS-CRAWL-001 §7.11): DOM size/shape, image
/// counts and sizes, and the smallest rendered font size.
/// </summary>
/// <param name="DomNodes">Total DOM element nodes.</param>
/// <param name="MaxTreeDepth">Maximum element tree depth.</param>
/// <param name="MaxTreeWidth">Maximum children of any one element.</param>
/// <param name="ImageCount">Image element count.</param>
/// <param name="ImageBytes">Transferred image bytes (0 when unknown).</param>
/// <param name="SmallestFontSizePx">Smallest rendered font size; null when unmeasured.</param>
public sealed record PageCompositionSample(
    int DomNodes,
    int MaxTreeDepth,
    int MaxTreeWidth,
    int ImageCount,
    long ImageBytes,
    double? SmallestFontSizePx);

/// <summary>
/// One lab measurement sample. Null vitals indicate the metric could not be
/// measured. The INP value is a lab proxy: the worst observed Event Timing
/// duration when interactions occurred, otherwise the worst single-task
/// blocking estimate (longest long task minus the 50 ms free budget).
/// </summary>
/// <param name="LcpMilliseconds">Largest Contentful Paint.</param>
/// <param name="Cls">Cumulative Layout Shift.</param>
/// <param name="InpProxyMilliseconds">Interaction to Next Paint lab proxy.</param>
/// <param name="TbtMilliseconds">Total Blocking Time.</param>
/// <param name="FcpMilliseconds">First Contentful Paint.</param>
/// <param name="Coverage">Code-coverage sample, when captured.</param>
/// <param name="Composition">Composition sample, when captured.</param>
public sealed record LabVitalsSample(
    double? LcpMilliseconds,
    double? Cls,
    double? InpProxyMilliseconds,
    double? TbtMilliseconds,
    double? FcpMilliseconds,
    CodeCoverageSample? Coverage,
    PageCompositionSample? Composition);

/// <summary>
/// Bounded lab measurement probe (WP-03-06, orchestrator-approved design):
/// the locked <see cref="IRenderingPool"/> exposes no script-evaluation or
/// metrics surface, so lab LCP/CLS/INP-proxy and CDP coverage are measured
/// through this dedicated instrumented render. Implementations reuse the
/// WP-02-06 zero-vendor-egress launch posture, resolve the browser LOCALLY
/// only, and never download anything at runtime.
/// </summary>
public interface ILabVitalsProbe
{
    /// <summary>Measures one page through an instrumented local render.</summary>
    /// <param name="request">Measurement request (crawl target only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lab sample.</returns>
    Task<LabVitalsSample> CollectAsync(LabVitalsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Lab probe failure classification (local diagnostics only).</summary>
public enum LabProbeFailureKind
{
    /// <summary>No local browser could be resolved or launched.</summary>
    BrowserUnavailable = 0,

    /// <summary>Navigation or measurement failed.</summary>
    Navigation = 1,

    /// <summary>The measurement exceeded its timeout budget.</summary>
    Timeout = 2,
}

/// <summary>
/// An isolated lab probe failure: one page's failure never takes down the
/// probe or an in-flight audit. Diagnostics are local only.
/// </summary>
public sealed class LabProbeException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Local diagnostic description.</param>
    public LabProbeException(LabProbeFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Creates the failure with an inner cause.</summary>
    /// <param name="kind">Failure classification.</param>
    /// <param name="message">Local diagnostic description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public LabProbeException(LabProbeFailureKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure classification.</summary>
    public LabProbeFailureKind Kind { get; }
}
