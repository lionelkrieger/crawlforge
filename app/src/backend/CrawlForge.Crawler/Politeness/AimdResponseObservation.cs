using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// One response observation fed to the AIMD controller: the locked
/// <see cref="HostResponseSample"/> plus optional transport extras that the
/// locked sample does not carry — the Retry-After header value (FS-CRAWL-002
/// §3.4) and the full response latency used by the concurrency coupling
/// (§3.4). When the full latency is absent, TTFB is the latency proxy.
/// </summary>
/// <param name="Sample">The locked response sample.</param>
/// <param name="RetryAfterSeconds">Retry-After header value in seconds, when present.</param>
/// <param name="FullResponseLatencyMs">Full response latency in milliseconds, when measured.</param>
public sealed record AimdResponseObservation(
    HostResponseSample Sample,
    double? RetryAfterSeconds = null,
    double? FullResponseLatencyMs = null);
