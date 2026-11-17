using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Intelligence.Tests.MarTech;

/// <summary>
/// Builds PageAuditContext fixtures with configurable documents, rendering
/// mode, consent configuration, and timestamped proxy captures.
/// </summary>
public static class AuditContexts
{
    /// <summary>A URL record fixture.</summary>
    public static UrlRecordDto Url(
        long id = 1,
        long auditId = 100,
        string url = "https://target.test/",
        bool isIndexable = true) => new(
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
        isIndexable);

    /// <summary>A configuration fixture.</summary>
    public static CrawlConfigurationDto Configuration(
        RenderMode renderingMode = RenderMode.None,
        bool? consentModeEnabled = null,
        string? primaryContainerOverride = null,
        string? dataLayerSchemaJson = null,
        string? beaconPatternsJson = null) => new(
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
        UpdatedAt: DateTimeOffset.UnixEpoch)
    {
        ConsentModeEnabled = consentModeEnabled,
        PrimaryContainerOverride = primaryContainerOverride,
        DataLayerSchemaJson = dataLayerSchemaJson,
        BeaconPatternsJson = beaconPatternsJson,
    };

    /// <summary>A full page context fixture.</summary>
    public static PageAuditContext Create(
        string? html = null,
        string? renderedHtml = null,
        RenderMode renderingMode = RenderMode.None,
        IReadOnlyList<ProxyCapturedRequest>? capturedRequests = null,
        CrawlConfigurationDto? configuration = null,
        long urlId = 1,
        long auditId = 100,
        bool isIndexable = true)
    {
        DomDocument? document = html is null ? null : TestDom.Parse(html);
        DomDocument? rendered = renderedHtml is null ? null : TestDom.Parse(renderedHtml);
        return new PageAuditContext(
            auditId,
            Url(urlId, auditId, isIndexable: isIndexable),
            configuration ?? Configuration(renderingMode),
            Fetch: null,
            document,
            rendered,
            ScopedText: null,
            capturedRequests);
    }

    /// <summary>A timestamped proxy-captured request fixture.</summary>
    public static ProxyCapturedRequest Captured(
        string url,
        int secondsOffset = 0,
        string? contentType = null,
        int statusCode = 200) => new(
        url,
        "GET",
        statusCode,
        contentType,
        DateTimeOffset.UnixEpoch.AddSeconds(secondsOffset));
}
