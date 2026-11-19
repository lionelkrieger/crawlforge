using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Document kind of a parsed XML sitemap (FS-CRAWL-001 §5.5: URL entries with
/// declared metadata; sitemap indexes are expanded recursively).
/// </summary>
public enum SitemapDocumentKind
{
    /// <summary>A <c>&lt;urlset&gt;</c> document listing page URLs.</summary>
    UrlSet = 0,

    /// <summary>A <c>&lt;sitemapindex&gt;</c> document listing child sitemaps.</summary>
    SitemapIndex = 1,
}

/// <summary>
/// One <c>&lt;url&gt;</c> entry of a sitemap urlset with its declared metadata
/// (FS-CRAWL-001 §5.5). Metadata values are kept as declared strings except
/// priority, which is parsed to a bounded double when valid.
/// </summary>
/// <param name="Url">Declared location (absolute URL as written in <c>&lt;loc&gt;</c>).</param>
/// <param name="LastModified">Declared <c>&lt;lastmod&gt;</c>, when present.</param>
/// <param name="ChangeFrequency">Declared <c>&lt;changefreq&gt;</c>, when present.</param>
/// <param name="Priority">Declared <c>&lt;priority&gt;</c> in [0,1], when valid.</param>
public sealed record SitemapUrlEntry(
    string Url,
    string? LastModified = null,
    string? ChangeFrequency = null,
    double? Priority = null);

/// <summary>
/// A parsed sitemap document: either a urlset of page entries or an index of
/// child sitemap locations. Parsing is tolerant — malformed input yields
/// diagnostics instead of exceptions (see <see cref="SitemapXmlParser"/>).
/// </summary>
/// <param name="Kind">Document kind.</param>
/// <param name="Urls">Page entries (urlset documents only).</param>
/// <param name="ChildSitemapUrls">Child sitemap locations (index documents only).</param>
public sealed record SitemapDocument(
    SitemapDocumentKind Kind,
    IReadOnlyList<SitemapUrlEntry> Urls,
    IReadOnlyList<string> ChildSitemapUrls);

/// <summary>
/// Result of tolerant sitemap XML parsing: a document when the body parsed,
/// plus bounded diagnostics describing skipped or truncated content.
/// </summary>
/// <param name="Document">The parsed document, or null when the body is not a sitemap.</param>
/// <param name="Diagnostics">Human-readable diagnostics (malformed XML, skipped entries, truncation).</param>
public sealed record SitemapParseResult(
    SitemapDocument? Document,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// How a sitemap URL was discovered (FS-CRAWL-001 §5.1 sitemap seeds, §5.5
/// sitemap expansion; robots.txt Sitemap directives + default probe).
/// </summary>
public enum SitemapSourceKind
{
    /// <summary>Declared in a robots.txt <c>Sitemap:</c> directive.</summary>
    RobotsDirective = 0,

    /// <summary>Found by probing the origin's <c>/sitemap.xml</c> default location.</summary>
    DefaultProbe = 1,

    /// <summary>User-supplied sitemap seed (§5.1 "Sitemap seeds" input).</summary>
    ConfiguredSeed = 2,

    /// <summary>Referenced by a parent sitemap index (recursive expansion).</summary>
    IndexRecursion = 3,
}

/// <summary>
/// One discovered sitemap location with its discovery source.
/// </summary>
/// <param name="Url">Absolute sitemap URL.</param>
/// <param name="Source">How the location was discovered.</param>
public sealed record DiscoveredSitemap(string Url, SitemapSourceKind Source);

/// <summary>
/// Result of sitemap discovery for one crawl origin: candidate sitemap
/// locations in deterministic order (robots directives, default probe,
/// configured seeds) plus diagnostics. Discovery never throws on unreachable
/// or hostile robots/sitemap endpoints — failures become diagnostics.
/// </summary>
/// <param name="Sitemaps">Discovered sitemap locations, deduplicated by canonical URL.</param>
/// <param name="Diagnostics">Discovery diagnostics (unreachable robots, probe misses, rejected URLs).</param>
/// <param name="Robots">The robots document used (fetched or supplied), when available.</param>
public sealed record SitemapDiscoveryResult(
    IReadOnlyList<DiscoveredSitemap> Sitemaps,
    IReadOnlyList<string> Diagnostics,
    RobotsDocument? Robots);

/// <summary>
/// Bounds for sitemap ingestion (scale invariant: bounded memory and fetch
/// count even against hostile sitemap trees). All values are local
/// configuration — nothing is resolved remotely (exclusion rows 5/8).
/// </summary>
/// <param name="MaxIndexDepth">Maximum sitemap-index recursion depth (at least 0).</param>
/// <param name="MaxSitemaps">Maximum sitemap documents fetched per ingestion (at least 1).</param>
/// <param name="MaxUrlsPerSitemap">Maximum <c>&lt;url&gt;</c> entries retained per document (at least 1).</param>
/// <param name="MaxTotalUrls">Maximum distinct page URLs retained per ingestion (at least 1).</param>
/// <param name="FetchTimeoutSeconds">Per-fetch timeout for sitemap documents (at least 1).</param>
public sealed record SitemapIngestionOptions(
    int MaxIndexDepth = 5,
    int MaxSitemaps = 100,
    int MaxUrlsPerSitemap = 50_000,
    int MaxTotalUrls = 500_000,
    int FetchTimeoutSeconds = 30)
{
    /// <summary>Validates the bounds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is invalid.</exception>
    public SitemapIngestionOptions Validate()
    {
        if (MaxIndexDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIndexDepth), MaxIndexDepth, "The index depth cannot be negative.");
        }

        if (MaxSitemaps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSitemaps), MaxSitemaps, "At least one sitemap must be fetchable.");
        }

        if (MaxUrlsPerSitemap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUrlsPerSitemap), MaxUrlsPerSitemap, "The per-document URL bound must be positive.");
        }

        if (MaxTotalUrls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTotalUrls), MaxTotalUrls, "The total URL bound must be positive.");
        }

        if (FetchTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(FetchTimeoutSeconds), FetchTimeoutSeconds, "The fetch timeout must be at least 1 second.");
        }

        return this;
    }
}

/// <summary>
/// One page URL seeded from a sitemap, with the sitemap document that first
/// declared it and the entry's declared metadata. The canonical URL is the
/// crawl key (§5.2); the raw declared URL is retained for evidence.
/// </summary>
/// <param name="CanonicalUrl">Canonicalized crawl key.</param>
/// <param name="Host">Host of the canonical URL (per-host sub-queue routing).</param>
/// <param name="DeclaredUrl">URL as declared in the sitemap <c>&lt;loc&gt;</c>.</param>
/// <param name="SourceSitemapUrl">Sitemap document that first declared the URL.</param>
/// <param name="LastModified">Declared <c>&lt;lastmod&gt;</c>, when present.</param>
/// <param name="ChangeFrequency">Declared <c>&lt;changefreq&gt;</c>, when present.</param>
/// <param name="Priority">Declared <c>&lt;priority&gt;</c>, when valid.</param>
public sealed record SitemapSeededUrl(
    string CanonicalUrl,
    string Host,
    string DeclaredUrl,
    string SourceSitemapUrl,
    string? LastModified = null,
    string? ChangeFrequency = null,
    double? Priority = null);

/// <summary>
/// Result of sitemap ingestion: deduplicated page URLs with discovery source
/// sitemap, the sitemap documents traversed, and bounded diagnostics. The
/// engine enqueues <see cref="CreateFrontierEntries"/> through the normal
/// frontier path (§5.3: sitemap entries are scheduled like any other
/// discovery) and feeds <see cref="Urls"/> to sitemap-vs-crawl correlation.
/// </summary>
/// <param name="Urls">Distinct page URLs in first-declaration order.</param>
/// <param name="Sitemaps">Sitemap documents traversed (including index documents).</param>
/// <param name="Diagnostics">Ingestion diagnostics (fetch failures, malformed documents, bounds reached).</param>
public sealed record SitemapIngestionResult(
    IReadOnlyList<SitemapSeededUrl> Urls,
    IReadOnlyList<DiscoveredSitemap> Sitemaps,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>
    /// Creates frontier entries for the seeded URLs: depth 0, seed-level
    /// priority, discovery source <see cref="DiscoverySource.Sitemap"/>.
    /// Deduplication against the audit seen-set remains the engine's concern.
    /// </summary>
    /// <returns>Frontier entries in first-declaration order.</returns>
    public IReadOnlyList<FrontierEntry> CreateFrontierEntries() =>
        Urls.Select(u => new FrontierEntry(u.CanonicalUrl, u.Host, Depth: 0, Priority: 1.0, DiscoverySource.Sitemap)).ToList();
}

/// <summary>
/// Per-URL crawl facts supplied to sitemap-vs-crawl correlation. The caller
/// (engine wiring) projects persisted <c>url_records</c> rows plus redirect
/// chains into this shape; the analyzer applies CR-024 search-indexability
/// semantics locally so the correlation rule is testable in isolation.
/// </summary>
/// <param name="CanonicalUrl">Canonical URL key (matches the sitemap declaration after canonicalization).</param>
/// <param name="UrlId">url_records identifier, when the URL was crawled.</param>
/// <param name="WasCrawled">Whether a url_records row exists for the URL.</param>
/// <param name="StatusClass">Persisted crawl status class.</param>
/// <param name="FinalStatusCode">Final HTTP status code, when fetched.</param>
/// <param name="RobotsState">Persisted robots evaluation state.</param>
/// <param name="IndexabilityFlags">Persisted indexability flags (noindex covers meta and X-Robots-Tag).</param>
/// <param name="CanonicalTargetUrl">Declared canonical target, when present (null = absent).</param>
/// <param name="Chain">Recorded redirect chain of the URL (empty when it did not redirect).</param>
public sealed record SitemapUrlCrawlFact(
    string CanonicalUrl,
    long? UrlId,
    bool WasCrawled,
    CrawlStatusClass StatusClass,
    int? FinalStatusCode,
    RobotsState RobotsState,
    IndexabilityFlags IndexabilityFlags,
    string? CanonicalTargetUrl,
    IReadOnlyList<RedirectHop> Chain);
