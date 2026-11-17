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
