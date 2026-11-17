namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Supplies the robots crawl-delay floor for a host (FS-CRAWL-002 §3.2): the
/// AIMD controller may never exceed <c>1 / crawl-delay</c> requests per second.
/// Implemented locally by <see cref="RobotsCache"/>; tests may substitute fakes.
/// </summary>
public interface ICrawlDelaySource
{
    /// <summary>Returns the crawl-delay in seconds for the host, when known.</summary>
    /// <param name="host">Target host.</param>
    /// <returns>The crawl-delay floor, or null when no delay is published.</returns>
    double? GetCrawlDelaySeconds(string host);
}
