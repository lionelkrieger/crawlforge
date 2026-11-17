namespace CrawlForge.Host.Composition;

/// <summary>
/// HTTP client construction for the approved crawl-target boundary
/// (FS-CRAWL-001 §2.6): the only outbound scope the crawler may touch is the
/// user-configured crawl target (or configured proxy). Redirects are disabled
/// at the handler so the fetcher observes every hop of a redirect chain
/// (§5.4). Centralized here so the boundary declaration is explicit.
/// </summary>
public static class CrawlTargetHttpClientFactory
{
    /// <summary>Creates a crawl-target client with the given per-request timeout.</summary>
    /// <param name="timeout">Overall request timeout.</param>
    /// <returns>Configured <see cref="HttpClient"/> owned by the caller.</returns>
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = timeout };
    }
}

/// <summary>
/// HTTP client construction for the approved user-destination boundary
/// (FS-CRAWL-001 §2.6): schedule-completion webhooks target only the
/// user-configured notification endpoint. Centralized here so the boundary
/// declaration is explicit.
/// </summary>
public static class NotificationDestinationHttpClientFactory
{
    /// <summary>Creates a notification-destination client with a 30s timeout.</summary>
    /// <returns>Configured <see cref="HttpClient"/> owned by the caller.</returns>
    public static HttpClient Create() =>
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
}
