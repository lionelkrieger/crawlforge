namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Crawl scope definition (FS-CRAWL-001 §5.1): host-only, include-subdomains,
/// or whole-domain, with optional path-prefix rules. The default reproduces
/// the baseline engine behavior (same-host scope).
/// </summary>
public enum CrawlScopeMode
{
    /// <summary>Only the exact seed hosts are in scope (baseline).</summary>
    HostOnly = 0,

    /// <summary>Seed hosts and their subdomains are in scope.</summary>
    IncludeSubdomains = 1,

    /// <summary>
    /// The whole registered domain is in scope. Local approximation: the last
    /// two host labels are compared (no public-suffix list dependency; the
    /// offline product ships no remote PSL fetch — exclusion E2/E4).
    /// </summary>
    WholeDomain = 2
}

/// <summary>
/// Resource types for the per-type fetch toggles (FS-CRAWL-001 §5.13).
/// </summary>
public enum CrawlResourceType
{
    /// <summary>HTML documents (always fetched; the audit subject).</summary>
    Html = 0,

    /// <summary>JavaScript files.</summary>
    Script = 1,

    /// <summary>CSS stylesheets.</summary>
    Stylesheet = 2,

    /// <summary>Images.</summary>
    Image = 3,

    /// <summary>Web fonts.</summary>
    Font = 4,

    /// <summary>Any other file type.</summary>
    Other = 5
}

/// <summary>
/// Inclusion/exclusion scope configuration (FS-CRAWL-001 §5.1). Inclusion
/// patterns must match for a URL to be crawled; exclusion patterns disqualify
/// a URL and are evaluated after inclusion. Patterns are regular expressions
/// matched against the canonical URL with a bounded match timeout (§10.4
/// bound philosophy); invalid patterns are rejected at construction (§4.3).
/// </summary>
public sealed record CrawlScopeOptions
{
    /// <summary>Default bounded regex evaluation timeout in milliseconds.</summary>
    public const int DefaultRegexTimeoutMs = 250;

    /// <summary>Scope mode (default host-only, the baseline).</summary>
    public CrawlScopeMode Mode { get; init; } = CrawlScopeMode.HostOnly;

    /// <summary>
    /// Inclusion regex patterns; empty means every in-scope-host URL qualifies.
    /// At least one pattern must match when the list is non-empty.
    /// </summary>
    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    /// <summary>
    /// Exclusion regex patterns evaluated after inclusion; any match
    /// disqualifies the URL (recorded as skipped with the reason).
    /// </summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// Optional path-prefix rules; when non-empty the URL path must start with
    /// at least one prefix (FS-CRAWL-001 §5.1 scope definition).
    /// </summary>
    public IReadOnlyList<string> PathPrefixes { get; init; } = [];

    /// <summary>
    /// Asset domains (for example CDNs) treated as internal for resource
    /// fetching (FS-CRAWL-001 §5.13). Exact hosts or parent domains of the
    /// resource host.
    /// </summary>
    public IReadOnlyList<string> AssetDomains { get; init; } = [];

    /// <summary>
    /// Check the status of external links without crawling beyond them
    /// (FS-CRAWL-001 §5.13). Default off (baseline records external links only).
    /// </summary>
    public bool CheckExternalLinks { get; init; }

    /// <summary>Bounded regex match timeout in milliseconds.</summary>
    public int RegexTimeoutMs { get; init; } = DefaultRegexTimeoutMs;
}

/// <summary>
/// Resource and external scope toggles (FS-CRAWL-001 §5.13). Defaults keep
/// the baseline behavior: HTML pages are fetched, resources are recorded as
/// metadata only, external links are recorded without status checks.
/// </summary>
public sealed record ResourceScopeOptions
{
    /// <summary>Fetch JavaScript files (default off).</summary>
    public bool FetchScripts { get; init; }

    /// <summary>Fetch stylesheets (default off).</summary>
    public bool FetchStylesheets { get; init; }

    /// <summary>Fetch images (default off).</summary>
    public bool FetchImages { get; init; }

    /// <summary>Fetch fonts (default off).</summary>
    public bool FetchFonts { get; init; }

    /// <summary>Fetch other file types (default off).</summary>
    public bool FetchOtherFiles { get; init; }

    /// <summary>
    /// Returns whether a resource of the given type is fetched. HTML documents
    /// are always fetched; every other type obeys its toggle.
    /// </summary>
    /// <param name="resourceType">Classified resource type.</param>
    /// <returns>True when the resource should be fetched.</returns>
    public bool ShouldFetch(CrawlResourceType resourceType) => resourceType switch
    {
        CrawlResourceType.Html => true,
        CrawlResourceType.Script => FetchScripts,
        CrawlResourceType.Stylesheet => FetchStylesheets,
        CrawlResourceType.Image => FetchImages,
        CrawlResourceType.Font => FetchFonts,
        _ => FetchOtherFiles
    };
}

/// <summary>
/// Directive-handling toggles (FS-CRAWL-001 §5.12) with the specified
/// Googlebot-following defaults. Detection and reporting of the directives
/// (§7.3/§7.4) are unaffected by these toggles — they govern discovery and
/// crawl decisions only.
/// </summary>
public sealed record DirectiveHandlingOptions
{
    /// <summary>Follow links on pages marked nofollow (page level). Default off.</summary>
    public bool FollowPageNofollow { get; init; }

    /// <summary>Follow individual rel=nofollow links (link level). Default off.</summary>
    public bool FollowLinkNofollow { get; init; }

    /// <summary>Crawl pages marked noindex so they can be reported. Default on.</summary>
    public bool CrawlNoindexPages { get; init; } = true;

    /// <summary>Use canonical targets as discovery. Default on.</summary>
    public bool UseCanonicalDiscovery { get; init; } = true;

    /// <summary>Use hreflang alternates and rel next/previous as discovery. Default on.</summary>
    public bool UseHreflangAndPaginationDiscovery { get; init; } = true;

    /// <summary>Follow meta refresh targets. Default on.</summary>
    public bool FollowMetaRefresh { get; init; } = true;

    /// <summary>Follow iframe sources. Default off.</summary>
    public bool FollowIframes { get; init; }
}
