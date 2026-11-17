namespace CrawlForge.Crawler.Fetching;

/// <summary>
/// Audit/URL context for one fetch. The locked <c>HttpFetchRequest</c> contract
/// carries no identifiers, so the crawl worker sets this ambient scope around
/// a fetch to let the fetcher record redirect hops against the right URL
/// record (FS-CRAWL-001 §5.4: every hop is recorded).
/// </summary>
/// <param name="AuditId">Audit identifier owning the URL record.</param>
/// <param name="UrlId">URL record identifier the fetch belongs to.</param>
public sealed record FetchContext(long AuditId, long UrlId);

/// <summary>
/// Ambient fetch context flowing across awaits. The crawl worker sets
/// <see cref="Current"/> before calling <c>IHttpFetcher.FetchAsync</c> and
/// clears it afterwards; when absent, the fetcher still follows and guards
/// redirects but records no hops.
/// </summary>
public static class FetchContextScope
{
    private static readonly AsyncLocal<FetchContext?> Ambient = new();

    /// <summary>Current fetch context, or null when no context is set.</summary>
    public static FetchContext? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
}