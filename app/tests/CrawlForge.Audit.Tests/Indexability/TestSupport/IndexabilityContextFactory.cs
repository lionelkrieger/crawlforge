using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.Indexability.TestSupport;

/// <summary>
/// Deterministic PageAuditContext construction for indexability tests.
/// </summary>
internal static class IndexabilityContextFactory
{
    public const long AuditId = 1;
    public const long UrlId = 1;
    public const string PageUrl = "https://example.com/page";

    public static UrlRecordDto UrlRecord(
        CrawlStatusClass statusClass = CrawlStatusClass.Success,
        int? finalStatusCode = 200,
        RobotsState robotsState = RobotsState.Allowed,
        string canonicalUrl = PageUrl) =>
        new(
            Id: UrlId,
            AuditId: AuditId,
            CanonicalUrl: canonicalUrl,
            Host: new Uri(canonicalUrl).Host,
            Path: new Uri(canonicalUrl).AbsolutePath,
            Depth: 0,
            DiscoverySource: DiscoverySource.Seed,
            StatusClass: statusClass,
            FinalStatusCode: finalStatusCode,
            RedirectTargetUrl: null,
            ContentType: "text/html",
            SizeBytes: 1024,
            TimingJson: null,
            RobotsState: robotsState,
            IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: statusClass == CrawlStatusClass.Success);

    public static CrawlConfigurationDto Configuration(RenderMode renderingMode = RenderMode.None) =>
        new(
            Id: 1,
            ProjectId: 1,
            Name: "test-configuration",
            SeedsJson: "[\"https://example.com/\"]",
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

    public static HttpFetchResult Fetch(
        string url = PageUrl,
        int statusCode = 200,
        IReadOnlyDictionary<string, string>? headers = null,
        string body = "<html></html>") =>
        new(
            FinalUrl: url,
            StatusCode: statusCode,
            Headers: headers ?? new Dictionary<string, string>(),
            Body: Encoding.UTF8.GetBytes(body),
            Ttfb: TimeSpan.FromMilliseconds(10),
            TotalDuration: TimeSpan.FromMilliseconds(20));

    public static PageAuditContext Context(
        DomDocument? document = null,
        DomDocument? renderedDocument = null,
        HttpFetchResult? fetch = null,
        UrlRecordDto? url = null,
        CrawlConfigurationDto? configuration = null,
        ScopedVisibleText? scopedText = null) =>
        new(
            AuditId: AuditId,
            Url: url ?? UrlRecord(),
            Configuration: configuration ?? Configuration(),
            Fetch: fetch ?? Fetch(),
            Document: document,
            RenderedDocument: renderedDocument,
            ScopedText: scopedText,
            CapturedRequests: null);
}