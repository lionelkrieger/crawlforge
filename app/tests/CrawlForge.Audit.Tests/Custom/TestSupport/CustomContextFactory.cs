using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Tests.Custom.TestSupport;

/// <summary>
/// Deterministic PageAuditContext construction for custom-surface tests.
/// </summary>
internal static class CustomContextFactory
{
    public const long AuditId = 1;
    public const long UrlId = 1;
    public const string PageUrl = "https://example.com/products/item";

    public static UrlRecordDto UrlRecord(string canonicalUrl = PageUrl, string? path = null) =>
        new(
            Id: UrlId,
            AuditId: AuditId,
            CanonicalUrl: canonicalUrl,
            Host: new Uri(canonicalUrl).Host,
            Path: path ?? new Uri(canonicalUrl).AbsolutePath,
            Depth: 0,
            DiscoverySource: DiscoverySource.Seed,
            StatusClass: CrawlStatusClass.Success,
            FinalStatusCode: 200,
            RedirectTargetUrl: null,
            ContentType: "text/html",
            SizeBytes: 1024,
            TimingJson: null,
            RobotsState: RobotsState.Allowed,
            IndexabilityFlags: IndexabilityFlags.None,
            IsIndexable: true);

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

    public static HttpFetchResult Fetch(string url = PageUrl, string body = "<html></html>") =>
        new(
            FinalUrl: url,
            StatusCode: 200,
            Headers: new Dictionary<string, string>(),
            Body: Encoding.UTF8.GetBytes(body),
            Ttfb: TimeSpan.FromMilliseconds(10),
            TotalDuration: TimeSpan.FromMilliseconds(20));

    public static PageAuditContext Context(
        DomDocument? document = null,
        DomDocument? renderedDocument = null,
        UrlRecordDto? url = null,
        RenderMode renderingMode = RenderMode.None,
        ScopedVisibleText? scopedText = null,
        string? fetchBody = null) =>
        new(
            AuditId: AuditId,
            Url: url ?? UrlRecord(),
            Configuration: Configuration(renderingMode),
            Fetch: Fetch(body: fetchBody ?? "<html></html>"),
            Document: document,
            RenderedDocument: renderedDocument,
            ScopedText: scopedText,
            CapturedRequests: null);
}