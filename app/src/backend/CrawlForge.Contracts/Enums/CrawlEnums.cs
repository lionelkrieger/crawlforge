namespace CrawlForge.Contracts.Enums;

/// <summary>
/// Lifecycle status of a crawl-and-audit run (schema catalog: audits.status).
/// </summary>
public enum AuditStatus
{
    /// <summary>Created but not yet started.</summary>
    Queued = 0,

    /// <summary>Actively crawling and/or auditing.</summary>
    Running = 1,

    /// <summary>Suspended; resumable from the latest checkpoint.</summary>
    Paused = 2,

    /// <summary>Finished successfully.</summary>
    Completed = 3,

    /// <summary>Stopped by the user; partial results retained.</summary>
    Cancelled = 4,

    /// <summary>Terminated by an unrecoverable error.</summary>
    Failed = 5,
}

/// <summary>
/// Coarse processing phase of an audit run, reported in audit detail
/// (REST: GET /api/audits/{id}). Additive phases may be appended.
/// </summary>
public enum AuditPhase
{
    /// <summary>No work has started yet.</summary>
    NotStarted = 0,

    /// <summary>Fetching URLs and building the link graph.</summary>
    Crawling = 1,

    /// <summary>Running the audit analyzers over collected pages.</summary>
    Analyzing = 2,

    /// <summary>Computing category and overall scores.</summary>
    Scoring = 3,

    /// <summary>Persisting aggregates and finalizing artifacts.</summary>
    Finalizing = 4,

    /// <summary>All phases finished.</summary>
    Finished = 5,
}

/// <summary>
/// Classification of a URL's crawl outcome (schema catalog: url_records.status_class).
/// The <see cref="Blocked"/> class carries the additive WAF sub-classification
/// (waf-challenge) through the additive provider/challenge fields on the URL record.
/// </summary>
public enum CrawlStatusClass
{
    /// <summary>Discovered but not fetched yet.</summary>
    NotCrawled = 0,

    /// <summary>Final response was a 2xx success.</summary>
    Success = 1,

    /// <summary>Final response was a 3xx redirect that was not followed to a terminal endpoint.</summary>
    Redirect = 2,

    /// <summary>Final response was a 4xx client error.</summary>
    ClientError = 3,

    /// <summary>Final response was a 5xx server error.</summary>
    ServerError = 4,

    /// <summary>Response was blocked (including WAF challenge pages served with 200 OK).</summary>
    Blocked = 5,

    /// <summary>Fetch failed at transport level (timeout, DNS, connection reset).</summary>
    FetchError = 6,
}

/// <summary>
/// How a URL entered the crawl (schema catalog: url_records.discovery_source).
/// The <see cref="List"/> value is the additive list-mode discovery source
/// (FS-CRAWL-002 §10).
/// </summary>
public enum DiscoverySource
{
    /// <summary>Configured seed URL.</summary>
    Seed = 0,

    /// <summary>Discovered through a link on a crawled page.</summary>
    Link = 1,

    /// <summary>Discovered through an XML sitemap.</summary>
    Sitemap = 2,

    /// <summary>Discovered as a redirect target.</summary>
    Redirect = 3,

    /// <summary>Additive: ingested from a user-supplied URL list (list mode).</summary>
    List = 4,

    /// <summary>Additive (WP-10-A1, D-021): discovered from the rendered DOM of a
    /// JS-rendered capture (FS-CRAWL-001 §5.6.1; phase-10 JS-only links are
    /// recorded as discovered-by-render). Wire value: "render".</summary>
    Render = 5,
}

/// <summary>
/// Robots exclusion state of a URL (schema catalog: url_records.robots state).
/// </summary>
public enum RobotsState
{
    /// <summary>No robots evaluation has been performed.</summary>
    Unknown = 0,

    /// <summary>Robots rules permit crawling this URL.</summary>
    Allowed = 1,

    /// <summary>Robots rules disallow crawling this URL.</summary>
    Disallowed = 2,
}

/// <summary>
/// Indexability flags carried by a URL record (schema catalog:
/// url_records.indexability flags). Composable; additive flags may be appended.
/// Indexable means search-indexable (CR-024, D-020): any restriction flag
/// (including <see cref="CanonicalElsewhere"/>) makes the URL non-indexable.
/// </summary>
[Flags]
public enum IndexabilityFlags
{
    /// <summary>No indexability restriction observed.</summary>
    None = 0,

    /// <summary>A noindex directive was observed (HTML meta or HTTP header).</summary>
    Noindex = 1,

    /// <summary>A nofollow directive was observed (HTML meta or HTTP header).</summary>
    Nofollow = 2,

    /// <summary>Robots exclusion blocks crawling of this URL.</summary>
    RobotsBlocked = 4,

    /// <summary>The final status class is not indexable (non-2xx).</summary>
    NonIndexableStatus = 8,

    /// <summary>Additive (WP-10-A1, D-021; CR-024): the page's canonical directive
    /// points to a different URL, so search engines consolidate indexing away
    /// from this page ("canonical elsewhere"). The URL stays crawlable but is
    /// not search-indexable. Existing flag values are unchanged.</summary>
    CanonicalElsewhere = 16,
}

/// <summary>
/// Rendering strategy for a crawl configuration (schema catalog:
/// crawl_configurations.rendering mode).
/// </summary>
public enum RenderMode
{
    /// <summary>Plain HTTP fetching only; no headless rendering.</summary>
    None = 0,

    /// <summary>Render on demand when the analyzer pipeline requires it.</summary>
    Auto = 1,

    /// <summary>Render every page through the headless pool.</summary>
    Always = 2,
}

/// <summary>
/// Capture mode of a crawl configuration (FS-CRAWL-001 §5.6.1, added by CR-005;
/// amendment WP-10-A1, D-021). A configuration selects exactly one mode; new
/// configurations default to non-JS. The additive nullable
/// <c>CrawlConfigurationDto.CaptureMode</c> member carries this vocabulary —
/// null keeps the baseline non-JS behaviour. Wire values follow the lowercase
/// convention with the hyphenated exception NonJs ↔ "non-js" (same convention
/// as CatchUp ↔ "catch-up"; see <see cref="EnumWireNames"/>).
/// </summary>
public enum CaptureMode
{
    /// <summary>Every in-scope URL is fetched over plain HTTP and parsed without
    /// executing scripts. Default for new configurations. Wire value: "non-js".</summary>
    NonJs = 0,

    /// <summary>Every in-scope URL is rendered in the headless browser; the
    /// post-render document is the analysed document. Wire value: "js".</summary>
    Js = 1,

    /// <summary>Every in-scope URL is captured both ways; both documents are
    /// stored and the response-vs-rendered comparison (§7.18) is computed for
    /// every URL. Wire value: "dual".</summary>
    Dual = 2,

    /// <summary>Every in-scope URL is fetched over plain HTTP; a sample per
    /// URL-path-pattern group is also rendered and compared, and groups whose
    /// sample materially changes switch to rendered capture for the rest of the
    /// audit. Wire value: "adaptive".</summary>
    Adaptive = 3,
}

/// <summary>
/// Which capture produced a URL record's analysed document (FS-CRAWL-001
/// §5.6.1: "Every URL record states which capture produced it (raw, rendered,
/// or both)"; amendment WP-10-A1, D-021). The additive nullable
/// <c>UrlRecordDto.CaptureSource</c> member carries this vocabulary — null means
/// the baseline raw (non-JS) capture. Wire values: "raw", "rendered", "both".
/// </summary>
public enum CaptureSource
{
    /// <summary>The raw HTTP response document (non-JS capture).</summary>
    Raw = 0,

    /// <summary>The post-render document (JS capture).</summary>
    Rendered = 1,

    /// <summary>Both captures exist for the URL (dual capture, or an adaptive
    /// sample that was fetched and rendered).</summary>
    Both = 2,
}

/// <summary>
/// Explicit per-URL-pattern capture override (FS-CRAWL-001 §5.6.1 manual
/// override: "rendered or non-rendered"; amendment WP-10-A1, D-021). Explicit
/// settings take precedence over adaptive switching and apply on top of every
/// capture mode. Wire values follow the lowercase convention with the
/// hyphenated exception NonRendered ↔ "non-rendered" (see <see cref="EnumWireNames"/>).
/// </summary>
public enum PatternCaptureMode
{
    /// <summary>URLs matching the pattern are captured without rendering.
    /// Wire value: "non-rendered".</summary>
    NonRendered = 0,

    /// <summary>URLs matching the pattern are captured through the headless
    /// browser. Wire value: "rendered".</summary>
    Rendered = 1,
}

/// <summary>
/// Directed-link relation flags (schema catalog: link_edges.relation flags).
/// Composable; additive flags may be appended.
/// </summary>
[Flags]
public enum LinkRelationFlags
{
    /// <summary>No relation flags observed.</summary>
    None = 0,

    /// <summary>rel="nofollow".</summary>
    Nofollow = 1,

    /// <summary>rel="noopener".</summary>
    Noopener = 2,

    /// <summary>rel="noreferrer".</summary>
    Noreferrer = 4,

    /// <summary>rel="ugc" (user-generated content).</summary>
    Ugc = 8,

    /// <summary>rel="sponsored".</summary>
    Sponsored = 16,

    /// <summary>rel="alternate" (including hreflang alternates).</summary>
    Alternate = 32,

    /// <summary>rel="canonical".</summary>
    Canonical = 64,
}
