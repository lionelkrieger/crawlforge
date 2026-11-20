using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Capture;

/// <summary>
/// Capture-mode execution contract (FS-CRAWL-001 §5.6.1, CR-005; WP-10-02).
/// One coordinator instance governs ONE audit: it is created from the frozen
/// configuration snapshot by <see cref="ICaptureModeCoordinatorFactory"/> and
/// holds the adaptive per-group state for the run. The engine calls
/// <see cref="CaptureAsync"/> once per successfully fetched in-scope HTML URL,
/// after raw link discovery (AuditExecutionEngine.CrawlPhaseAsync ~:1368).
/// <para>
/// Mode semantics (§5.6.1): non-JS fetches only (default); JS renders every
/// in-scope page; dual stores BOTH captures and compares every URL; adaptive
/// samples a configurable number of URLs per URL-path-pattern group (default 4,
/// spec range 3–5) and switches a group to rendered capture when a sample
/// materially changes. Manual per-pattern overrides WIN over adaptive switching
/// and apply on top of every mode.
/// </para>
/// <para>
/// The coordinator never fails the crawl: a render failure is isolated and the
/// URL keeps its raw capture (§5.6.2 failure isolation). Rendered traffic is
/// captured directly by the rendering control layer (CR-006): the waterfall
/// artifact is persisted from <see cref="RenderedPage.NetworkWaterfall"/>, never
/// from the loopback proxy.
/// </para>
/// </summary>
public interface ICaptureModeCoordinator
{
    /// <summary>The capture mode governing this audit (null configuration → NonJs).</summary>
    CaptureMode Mode { get; }

    /// <summary>
    /// Pure capture decision for one URL: mode + manual overrides + adaptive
    /// group state. Does not render and does not mutate adaptive state; the
    /// state advance happens inside <see cref="CaptureAsync"/>.
    /// </summary>
    /// <param name="url">Absolute URL about to be captured.</param>
    /// <returns>The decision with its reason and pattern-group key.</returns>
    CaptureDecision Decide(string url);

    /// <summary>
    /// Captures one URL according to the mode. When the decision says render,
    /// renders through the locked <see cref="IRenderingPool"/>, parses the
    /// post-render DOM, persists the RenderedDom / NetworkWaterfall / Screenshot
    /// artifacts, stamps the URL record's <see cref="UrlRecordDto.CaptureSource"/>,
    /// extracts rendered-only links and runs any supplied capture snippets.
    /// When no render is due, returns a raw outcome without touching the pool.
    /// </summary>
    /// <param name="request">Per-URL capture request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capture outcome (never throws for render failures).</returns>
    Task<RenderedCaptureOutcome> CaptureAsync(RenderedCaptureRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains accumulated audit-level findings (adaptive template switches and
    /// the dual-storage warning). The engine records these at crawl end through
    /// its suppression-aware crawl-finding recorder. Calling again yields empty.
    /// </summary>
    /// <returns>Accumulated audit-level finding drafts.</returns>
    IReadOnlyList<FindingDraft> DrainAuditFindings();
}

/// <summary>
/// Creates per-audit capture coordinators from the frozen configuration
/// snapshot. Registered as a singleton; the engine calls <see cref="Create"/>
/// once at crawl start so adaptive group state lives for exactly one run.
/// </summary>
public interface ICaptureModeCoordinatorFactory
{
    /// <summary>Creates a coordinator for one audit's frozen configuration.</summary>
    /// <param name="configuration">Frozen configuration snapshot (CaptureMode/CaptureSettings read here).</param>
    /// <returns>A fresh coordinator with empty adaptive state.</returns>
    ICaptureModeCoordinator Create(CrawlConfigurationDto configuration);
}

/// <summary>
/// One per-URL capture request. The engine has already fetched the raw body,
/// stored it as the Body artifact and parsed it; the coordinator adds the
/// rendered capture when the mode calls for it.
/// </summary>
/// <param name="AuditId">Owning audit.</param>
/// <param name="UrlId">URL record identifier (CaptureSource is stamped onto this row).</param>
/// <param name="Url">Absolute canonical URL being captured.</param>
/// <param name="Path">URL path (pattern grouping and override matching).</param>
/// <param name="Depth">Link depth from the seeds (rendered-only link discovery).</param>
/// <param name="RawDocument">Parsed raw response document.</param>
/// <param name="RawBodyBytes">Stored raw body size (dual-storage byte accounting).</param>
/// <param name="Configuration">Frozen configuration snapshot.</param>
/// <param name="Device">Device emulation profile for the render.</param>
/// <param name="InfiniteScroll">Whether to drive infinite-scroll expansion.</param>
/// <param name="CaptureConsole">Whether to capture console messages.</param>
/// <param name="Stealth">Whether the user-enabled stealth profile applies.</param>
/// <param name="Snippets">Optional custom JS snippets to run during capture (§7.17).</param>
public sealed record RenderedCaptureRequest(
    long AuditId,
    long UrlId,
    string Url,
    string Path,
    int Depth,
    DomDocument RawDocument,
    long RawBodyBytes,
    CrawlConfigurationDto Configuration,
    DeviceProfile Device,
    bool InfiniteScroll = false,
    bool CaptureConsole = true,
    bool Stealth = false,
    IReadOnlyList<CaptureSnippetDefinition>? Snippets = null);

/// <summary>
/// The capture decision for one URL (pure; no side effects).
/// </summary>
/// <param name="ShouldRender">Whether the rendered capture is due.</param>
/// <param name="Reason">Stable reason token (mode:non-js, mode:js, mode:dual,
/// override:rendered, override:non-rendered, adaptive:sample, adaptive:switched,
/// adaptive:raw).</param>
/// <param name="GroupKey">URL-path-pattern group key (adaptive grouping).</param>
/// <param name="OverrideApplied">Whether a manual per-pattern override decided.</param>
public sealed record CaptureDecision(
    bool ShouldRender,
    string Reason,
    string GroupKey,
    bool OverrideApplied);

/// <summary>
/// The outcome of capturing one URL.
/// </summary>
/// <param name="CaptureSource">Which capture produced the analysed document
/// (raw / rendered / both), stamped onto the URL record.</param>
/// <param name="Rendered">Whether a rendered capture was produced.</param>
/// <param name="RenderedDocument">Parsed post-render DOM, when rendered.</param>
/// <param name="RenderedHtml">Post-render HTML snapshot, when rendered.</param>
/// <param name="RenderedPage">The pool result (waterfall, console, screenshot), when rendered.</param>
/// <param name="RenderedOnlyLinks">Links present only in the rendered DOM,
/// resolved absolute; the engine enqueues them as discovered-by-render.</param>
/// <param name="Findings">Per-URL findings produced at capture time.</param>
/// <param name="MaterialChange">Material-change facts when an adaptive sample
/// was compared (null when no comparison ran).</param>
/// <param name="SnippetResults">Capture-time snippet results by source, when snippets ran.</param>
/// <param name="ServiceWorkerRegistered">Whether a service-worker registration
/// was observed in the captured documents (renders bypass it; §7.19).</param>
/// <param name="RenderFailureReason">Isolated render failure detail, when the
/// render failed and the URL kept its raw capture.</param>
/// <param name="Decision">The decision that produced this outcome.</param>
public sealed record RenderedCaptureOutcome(
    CaptureSource CaptureSource,
    bool Rendered,
    DomDocument? RenderedDocument,
    string? RenderedHtml,
    RenderedPage? RenderedPage,
    IReadOnlyList<RenderedLinkCandidate> RenderedOnlyLinks,
    IReadOnlyList<FindingDraft> Findings,
    MaterialChangeFact? MaterialChange,
    IReadOnlyList<CaptureSnippetOutcome> SnippetResults,
    bool ServiceWorkerRegistered,
    string? RenderFailureReason,
    CaptureDecision Decision);

/// <summary>
/// One link discovered only in the rendered DOM (FS-CRAWL-001 §5.6.1 JS-only
/// links). The engine resolves scope/canonicalization and enqueues it with
/// <see cref="DiscoverySource.Render"/>.
/// </summary>
/// <param name="AbsoluteUrl">Href resolved against the page URL.</param>
/// <param name="AnchorText">Visible anchor text, when present.</param>
/// <param name="IsNofollow">Whether the anchor carries rel="nofollow".</param>
public sealed record RenderedLinkCandidate(string AbsoluteUrl, string? AnchorText, bool IsNofollow);

/// <summary>
/// Material-change facts of one adaptive sample comparison (§5.6.1): any
/// title/canonical/meta-robots/hreflang difference, a different link set, or a
/// visible word-count change above the configured threshold is material.
/// </summary>
/// <param name="Url">Sample URL compared.</param>
/// <param name="GroupKey">Pattern group the sample belongs to.</param>
/// <param name="IsMaterial">Whether the sample materially changed.</param>
/// <param name="TitleChanged">Title differs.</param>
/// <param name="CanonicalChanged">Canonical differs.</param>
/// <param name="MetaRobotsChanged">Meta-robots directives differ.</param>
/// <param name="HreflangChanged">hreflang alternates differ.</param>
/// <param name="LinkSetChanged">The set of link hrefs differs.</param>
/// <param name="RawWordCount">Visible word count of the raw document.</param>
/// <param name="RenderedWordCount">Visible word count of the rendered document.</param>
/// <param name="WordCountChangeFraction">Absolute word-count change relative to
/// the raw count (null when the raw count is zero; a zero→nonzero change is
/// always material).</param>
/// <param name="WordCountThreshold">Configured material-change threshold applied.</param>
public sealed record MaterialChangeFact(
    string Url,
    string GroupKey,
    bool IsMaterial,
    bool TitleChanged,
    bool CanonicalChanged,
    bool MetaRobotsChanged,
    bool HreflangChanged,
    bool LinkSetChanged,
    int RawWordCount,
    int RenderedWordCount,
    double? WordCountChangeFraction,
    double WordCountThreshold);

/// <summary>
/// One custom JS snippet bound to an extraction source (FS-CRAWL-001 §5.6.1:
/// custom extraction/search/snippets may target raw, rendered, or both; §7.17).
/// </summary>
/// <param name="Snippet">The locked snippet definition.</param>
/// <param name="Source">Which document the snippet evaluates against.</param>
public sealed record CaptureSnippetDefinition(
    JsSnippetDefinition Snippet,
    CaptureSource Source = CaptureSource.Rendered);

/// <summary>
/// Snippet results for one extraction source.
/// </summary>
/// <param name="Source">The document source the results came from.</param>
/// <param name="Results">Bounded typed snippet results.</param>
/// <param name="TimedOutKeys">Snippet keys aborted by the outer execution bound.</param>
public sealed record CaptureSnippetOutcome(
    CaptureSource Source,
    IReadOnlyList<SnippetResultDraft> Results,
    IReadOnlyList<string> TimedOutKeys);

/// <summary>
/// Runs custom JS snippets during capture with a timeout inside an isolated
/// wrapper (FS-CRAWL-001 §7.17). The locked <see cref="IJsSnippetRunner"/>
/// (Jint interpreter: no CLR interop, no network, scoped cf.* host API,
/// per-snippet timeout + statement/memory bounds) provides the isolation; this
/// executor adds the raw/rendered/both source selection and an outer execution
/// bound so a hung runner can never stall a capture worker. A snippet failure
/// on one page never fails the audit or other pages.
/// </summary>
public interface ICaptureSnippetExecutor
{
    /// <summary>Runs the supplied snippets against the selected sources.</summary>
    /// <param name="context">Page context carrying the raw and rendered documents.</param>
    /// <param name="snippets">Snippets with their extraction sources.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results grouped by source; never throws for snippet failures.</returns>
    Task<IReadOnlyList<CaptureSnippetOutcome>> RunAsync(
        PageAuditContext context,
        IReadOnlyList<CaptureSnippetDefinition> snippets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Capture-component defaults local to WP-10-02 (the locked
/// <see cref="CaptureSettings"/> carries the configurable knobs).
/// </summary>
public static class CaptureDefaults
{
    /// <summary>
    /// Default desktop device profile used when the engine supplies none
    /// (1920×1080 desktop Chrome identity; the rendering layer applies the
    /// configured emulation per §5.6.2).
    /// </summary>
    public static DeviceProfile DesktopDevice { get; } = new(
        "desktop",
        1920,
        1080,
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        IsMobile: false);

    /// <summary>Spec sample-size range for adaptive mode (§5.6.1: 3–5 URLs).</summary>
    public const int MinAdaptiveSampleSize = 3;

    /// <summary>Spec sample-size range upper bound (§5.6.1).</summary>
    public const int MaxAdaptiveSampleSize = 5;

    /// <summary>
    /// Outer per-source snippet execution bound (safety net above the runner's
    /// own per-snippet timeout; the Jint runner aborts individual snippets at
    /// its configured timeout well inside this bound).
    /// </summary>
    public static TimeSpan SnippetOuterTimeout { get; } = TimeSpan.FromSeconds(10);
}
