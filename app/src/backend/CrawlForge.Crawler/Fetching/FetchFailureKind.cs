namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Classification of permanent fetch failures (FS-CRAWL-001 §5.4): stored with
/// the URL record by the crawl worker. HTTP status outcomes are not transport
/// failures — they are returned as results and classified via
/// <see cref="Enums.CrawlStatusClass"/>.
/// </summary>
public enum FetchFailureKind
{
    /// <summary>The target host name could not be resolved.</summary>
    NameResolution = 0,

    /// <summary>TLS negotiation or certificate validation failed.</summary>
    Tls = 1,

    /// <summary>The request exceeded its timeout budget.</summary>
    Timeout = 2,

    /// <summary>The response body exceeded the configured size guard.</summary>
    Oversize = 3,

    /// <summary>The configured proxy failed or was unreachable.</summary>
    ProxyFailure = 4,

    /// <summary>The redirect chain revisited a URL (loop).</summary>
    RedirectLoop = 5,

    /// <summary>The redirect chain exceeded the hop ceiling.</summary>
    RedirectChainExceeded = 6,

    /// <summary>Robots rules disallow the URL (classified by the limits layer).</summary>
    RobotsBlocked = 7,

    /// <summary>Another transport-level failure (connection reset, refused, ...).</summary>
    Network = 8,

    /// <summary>Unclassified failure.</summary>
    Other = 9,
}