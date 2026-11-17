namespace CrawlForge.Crawler.Fetching;

/// <summary>Approved boundary class: crawl-target fetching (user-configured targets / proxy).</summary>
public sealed class CrawlTargetFetcher
{
    public async Task<string> FetchTargetAsync(string targetUrl)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync(targetUrl);
    }
}
