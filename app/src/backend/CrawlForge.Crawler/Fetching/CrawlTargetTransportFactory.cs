namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Construction of the outbound HTTP transports that talk to the USER-CONFIGURED
/// CRAWL TARGET (FS-CRAWL-001 §2.6 approved boundary class "crawl target/proxy").
/// The governance network boundary (NET-02) approves outbound client construction
/// inside the crawler fetching scope; the execution engine composes these
/// transports but must not construct outbound clients itself.
/// <para>
/// Both crawl transports disable automatic redirects: the fetcher owns redirect
/// observation (durable chain recording, hop caps, loop detection — §5.3), so
/// the handler must never follow redirects silently.
/// </para>
/// </summary>
public static class CrawlTargetTransportFactory
{
    /// <summary>
    /// Creates one crawl-target HTTP client (cookie-aware crawl transport or the
    /// robots-cache transport; both are plain crawl-target traffic).
    /// </summary>
    /// <param name="timeoutSeconds">Per-request timeout in seconds.</param>
    /// <returns>A client with automatic redirects disabled.</returns>
    public static HttpClient Create(int timeoutSeconds) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
}