using System.Text;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.StructuredData.Support;

/// <summary>
/// Builds <see cref="PageAuditContext"/> instances over the PRODUCTION tolerant
/// parser (<see cref="AngleSharpDomParser"/>) so adapter tests exercise the
/// exact parse path the audit pipeline uses (CODEMAP §3 step 6), not the test
/// parser double. The fetch envelope carries the raw body and optional headers.
/// </summary>
public static class FixtureAuditContext
{
    /// <summary>The shared production parser instance (stateless, thread-safe).</summary>
    public static AngleSharpDomParser Parser { get; } = new();

    /// <summary>Creates a page context parsed by the production parser.</summary>
    /// <param name="html">Response markup.</param>
    /// <param name="url">Canonical page URL.</param>
    /// <param name="headers">Response headers, when present.</param>
    /// <param name="renderedHtml">Rendered markup, when rendering ran.</param>
    /// <param name="urlId">URL record identifier.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="statusClass">Crawl outcome class.</param>
    /// <param name="contentType">Response content type.</param>
    /// <param name="includeDocument">When false, no parsed document is supplied.</param>
    /// <returns>The composed page audit context.</returns>
    public static PageAuditContext Create(
        string html,
        string url = FixtureSitePages.StructuredDataUrl,
        IReadOnlyDictionary<string, string>? headers = null,
        string? renderedHtml = null,
        long urlId = 1,
        long auditId = 1,
        CrawlStatusClass statusClass = CrawlStatusClass.Success,
        string? contentType = "text/html; charset=utf-8",
        bool includeDocument = true)
    {
        var uri = new Uri(url);
        var urlRecord = new UrlRecordDto(
            urlId, auditId, url, uri.Host, uri.AbsolutePath, 0,
            DiscoverySource.Seed, statusClass, statusClass == CrawlStatusClass.Success ? 200 : 404, null,
            contentType, html.Length, null, RobotsState.Allowed, IndexabilityFlags.None, true);
        var configuration = new CrawlConfigurationDto(
            1, 1, "fixture", "[]", null, null, null, RenderMode.None,
            null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var fetch = new HttpFetchResult(
            url, statusClass == CrawlStatusClass.Success ? 200 : 404,
            headers ?? new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes(html), TimeSpan.Zero, TimeSpan.Zero);

        return new PageAuditContext(
            auditId,
            urlRecord,
            configuration,
            fetch,
            includeDocument ? Parser.Parse(html, url) : null,
            renderedHtml is null ? null : Parser.Parse(renderedHtml, url),
            null,
            null);
    }
}
