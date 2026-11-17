using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.StructuredData.Support;

/// <summary>
/// Builds <see cref="PageAuditContext"/> instances for analyzer tests over the
/// test DOM parser double. The fetch envelope carries the raw body so
/// raw-HTML fingerprint surfaces behave exactly as in production.
/// </summary>
public static class StructuredDataTestContext
{
    /// <summary>The shared test parser instance.</summary>
    public static TestDomParser Parser { get; } = new();

    /// <summary>Creates a page context from markup and optional headers/rendered markup.</summary>
    /// <param name="html">Response markup.</param>
    /// <param name="url">Canonical page URL.</param>
    /// <param name="headers">Response headers, when present.</param>
    /// <param name="renderMode">Configured rendering mode.</param>
    /// <param name="renderedHtml">Rendered markup, when rendering ran.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <returns>The composed page audit context.</returns>
    public static PageAuditContext Create(
        string html,
        string url = "https://example.test/page",
        IReadOnlyDictionary<string, string>? headers = null,
        RenderMode renderMode = RenderMode.None,
        string? renderedHtml = null,
        long urlId = 1,
        long auditId = 1)
    {
        var uri = new Uri(url);
        var urlRecord = new UrlRecordDto(
            urlId, auditId, url, uri.Host, uri.AbsolutePath, 0,
            DiscoverySource.Seed, CrawlStatusClass.Success, 200, null,
            "text/html", html.Length, null, RobotsState.Allowed, IndexabilityFlags.None, true);
        var configuration = new CrawlConfigurationDto(
            1, 1, "test", "[]", null, null, null, renderMode,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var fetch = new HttpFetchResult(
            url, 200, headers ?? new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes(html), TimeSpan.Zero, TimeSpan.Zero);

        return new PageAuditContext(
            auditId,
            urlRecord,
            configuration,
            fetch,
            Parser.Parse(html, url),
            renderedHtml is null ? null : Parser.Parse(renderedHtml, url),
            null,
            null);
    }
}
