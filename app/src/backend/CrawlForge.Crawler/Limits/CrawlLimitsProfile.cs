namespace CrawlForge.Crawler.Limits;

/// <summary>
/// Scope of the per-domain page ceiling (FS-CRAWL-001 §4.2): counted per
/// registered domain or per exact host. Local configuration only.
/// </summary>
public enum DomainCeilingMode
{
    /// <summary>Count pages per registered domain (subdomains share the ceiling).</summary>
    RegisteredDomain,

    /// <summary>Count pages per exact host.</summary>
    Host
}

/// <summary>
/// Scope of the URL throughput ceiling (FS-CRAWL-001 §4.2): enforced globally
/// across the crawl or per host. Local configuration only.
/// </summary>
public enum ThroughputScope
{
    /// <summary>One shared throughput ceiling for the whole crawl.</summary>
    Global,

    /// <summary>The throughput ceiling applies to each host independently.</summary>
    PerHost
}

/// <summary>
/// The complete FS-CRAWL-001 §4.2 crawl-limit parameter set, including the
/// parameters beyond the locked <c>ResolvedCrawlLimits</c> contract surface
/// (per-domain ceiling, retry budget, redirect hops, response size). Unlimited
/// ceilings use the distinguished absence-of-value sentinel (<see langword="null"/>
/// for nullable fields, <see cref="double.PositiveInfinity"/> for the rate),
/// never a magic large number (§4.3). This profile is a local read model of the
/// resolver; cross-module consumers use the locked <c>ILimitsResolver</c> surface.
/// </summary>
/// <param name="MaxUrls">Total page ceiling; null = unlimited.</param>
/// <param name="PerDomainMaxUrls">Per-domain page ceiling; null = unlimited.</param>
/// <param name="DomainMode">Whether the per-domain ceiling counts registered domains or hosts.</param>
/// <param name="MaxDepth">Maximum link depth; null = unlimited.</param>
/// <param name="MaxDurationSeconds">Maximum crawl duration in seconds; null = unlimited.</param>
/// <param name="MaxConcurrency">Worker concurrency ceiling (positive, within the safety cap).</param>
/// <param name="RequestsPerSecond">URL throughput ceiling; positive infinity = unlimited.</param>
/// <param name="ThroughputScope">Whether the throughput ceiling is global or per host.</param>
/// <param name="FetchTimeoutSeconds">Per-request timeout in seconds (positive).</param>
/// <param name="RetryBudget">Retry budget per URL (non-negative).</param>
/// <param name="RedirectHopCeiling">Maximum redirect hops per URL (positive).</param>
/// <param name="ResponseSizeBytes">Response size ceiling in bytes (positive).</param>
public sealed record CrawlLimitsProfile(
    long? MaxUrls,
    long? PerDomainMaxUrls,
    DomainCeilingMode DomainMode,
    int? MaxDepth,
    int? MaxDurationSeconds,
    int MaxConcurrency,
    double RequestsPerSecond,
    ThroughputScope ThroughputScope,
    int FetchTimeoutSeconds,
    int RetryBudget,
    int RedirectHopCeiling,
    long ResponseSizeBytes);
