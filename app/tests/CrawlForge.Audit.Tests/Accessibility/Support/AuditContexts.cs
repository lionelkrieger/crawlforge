using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Builds PageAuditContext fixtures with configurable fetch, documents,
/// rendering mode, and proxy capture.
/// </summary>
public static class AuditContexts
{
    /// <summary>A URL record fixture.</summary>
    public static UrlRecordDto Url(long id = 1, long auditId = 100, string url = "https://target.test/") => new(
        id,
        auditId,
        url,
        "target.test",
        "/",
        Depth: 0,
        DiscoverySource.Seed,
        CrawlStatusClass.Success,
        FinalStatusCode: 200,
        RedirectTargetUrl: null,
        ContentType: "text/html",
        SizeBytes: 1024,
        TimingJson: null,
        RobotsState.Allowed,
        IndexabilityFlags.None,
        IsIndexable: true);

    /// <summary>A configuration fixture with the given rendering mode.</summary>
    public static CrawlConfigurationDto Configuration(RenderMode renderingMode = RenderMode.None) => new(
        Id: 10,
        ProjectId: 1,
        Name: "test-config",
        SeedsJson: "[\"https://target.test/\"]",
        ScopeRulesJson: null,
        Depth: null,
        LimitOverrides: null,
        RenderingMode: renderingMode,
        PolitenessJson: null,
        ProxyRoutingJson: null,
        CustomExtractionRulesJson: null,
        HeadersJson: null,
        CookiesJson: null,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>A fetch fixture with configurable TTFB and body size.</summary>
    public static HttpFetchResult Fetch(int bodyBytes = 1024, int ttfbMs = 120) => new(
        FinalUrl: "https://target.test/",
        StatusCode: 200,
        Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["content-type"] = "text/html" },
        Body: new ReadOnlyMemory<byte>(new byte[bodyBytes]),
        Ttfb: TimeSpan.FromMilliseconds(ttfbMs),
        TotalDuration: TimeSpan.FromMilliseconds(ttfbMs + 50));

    /// <summary>A full page context fixture.</summary>
    public static PageAuditContext Create(
        string? html = null,
        string? renderedHtml = null,
        RenderMode renderingMode = RenderMode.None,
        HttpFetchResult? fetch = null,
        IReadOnlyList<ProxyCapturedRequest>? capturedRequests = null,
        long urlId = 1,
        long auditId = 100)
    {
        DomDocument? document = html is null ? null : TestDom.Parse(html);
        DomDocument? rendered = renderedHtml is null ? null : TestDom.Parse(renderedHtml);
        return new PageAuditContext(
            auditId,
            Url(urlId, auditId),
            Configuration(renderingMode),
            fetch,
            document,
            rendered,
            ScopedText: null,
            capturedRequests);
    }

    /// <summary>A proxy-captured request fixture.</summary>
    public static ProxyCapturedRequest Captured(string url, string? contentType = null, int statusCode = 200) => new(
        url,
        "GET",
        statusCode,
        contentType,
        DateTimeOffset.UnixEpoch);
}
