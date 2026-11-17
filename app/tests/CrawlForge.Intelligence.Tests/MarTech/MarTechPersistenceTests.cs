using CrawlForge.Audit.Pipeline;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Data.Repositories;
using CrawlForge.Intelligence.MarTech;
using Xunit;

namespace CrawlForge.Intelligence.Tests.MarTech;

/// <summary>
/// Real-stack persistence and offline re-analysis tests (FS-CRAWL-002
/// §14.4): analytics_detections, findings, metric_summaries, and the
/// consent_state/datalayer_snapshot element groups persist through the
/// production SQLite stack; re-analysis runs from stored bodies + capture
/// with no network access.
/// </summary>
public sealed class MarTechPersistenceTests
{
    private const string StoredHtml = """
        <html><head>
        <script async src="https://www.googletagmanager.com/gtm.js?id=GTM-STORE1"></script>
        <script>
        gtag('consent', 'default', {ad_storage: 'denied', analytics_storage: 'denied'});
        dataLayer.push({"event": "page_view"});
        </script>
        </head><body>
        <script src="https://www.googletagmanager.com/gtag/js?id=G-STORE2"></script>
        <div id="cookie-banner">Cookies</div>
        </body></html>
        """;

    private static readonly ProxyCapturedRequest[] StoredCapture =
    [
        new("https://www.googletagmanager.com/gtm.js?id=GTM-STORE1", "GET", 200, "application/javascript",
            DateTimeOffset.UnixEpoch.AddSeconds(1)),
        new("https://www.google-analytics.com/g/collect?tid=G-STORE2&en=page_view&gcs=G100", "GET", 200, "text/plain",
            DateTimeOffset.UnixEpoch.AddSeconds(2)),
    ];

    [Fact]
    public async Task Detections_PersistAndReadBack_WithVendorAndContainerIndexes()
    {
        await using var stack = await MarTechRealStack.CreateAsync();
        var auditor = new MarTechAuditor();
        var context = AuditContexts.Create(
            html: StoredHtml,
            capturedRequests: StoredCapture,
            auditId: stack.AuditId,
            urlId: stack.UrlId);
        var result = await auditor.AuditAsync(context);
        Assert.NotEmpty(result.Detections);

        var repository = new SqlRepository<AnalyticsDetectionDto>(stack.Provider);
        foreach (var draft in result.Detections)
        {
            await repository.CreateAsync(new AnalyticsDetectionDto(
                0, stack.AuditId, stack.UrlId, draft.Vendor, draft.ContainerId, draft.DetectionSource, draft.Placement));
        }

        var page = await repository.ListAsync(new ListQuery(0, 100));
        Assert.Equal(result.Detections.Count, page.TotalCount);
        Assert.Contains(page.Items, row => row.Vendor == "google-tag-manager"
            && row.ContainerId == "GTM-STORE1" && row.DetectionSource == "both");
        Assert.Contains(page.Items, row => row.Vendor == "google-analytics-4" && row.ContainerId == "G-STORE2");
    }

    [Fact]
    public async Task Findings_PersistWithAnalyticsTaggingCategory()
    {
        await using var stack = await MarTechRealStack.CreateAsync();
        var auditor = new MarTechAuditor();
        var captures = new[]
        {
            StoredCapture[1],
            new ProxyCapturedRequest(StoredCapture[1].Url, "GET", 200, "text/plain", DateTimeOffset.UnixEpoch.AddSeconds(3)),
        };
        var context = AuditContexts.Create(
            html: StoredHtml,
            capturedRequests: captures,
            auditId: stack.AuditId,
            urlId: stack.UrlId);
        var result = await auditor.AuditAsync(context);
        var duplicate = Assert.Single(result.Findings, f => f.RuleKey == MarTechRuleKeys.DuplicateContainer);

        var repository = new SqlRepository<FindingDto>(stack.Provider);
        var persisted = await repository.CreateAsync(new FindingDto(
            0, stack.AuditId, duplicate.RuleKey, duplicate.Category, duplicate.Severity,
            stack.UrlId, duplicate.EvidenceJson, duplicate.RecommendationKey, false));

        var readBack = await repository.GetByIdAsync(persisted.Id);
        Assert.NotNull(readBack);
        Assert.Equal(AuditCategory.AnalyticsTagging, readBack!.Category);
        Assert.Equal(FindingSeverity.Warning, readBack.Severity);
        Assert.Contains("network-pageview", readBack.EvidenceJson);
    }

    [Fact]
    public async Task MetricContributions_PersistIntoMetricSummaries()
    {
        await using var stack = await MarTechRealStack.CreateAsync();
        var auditor = new MarTechAuditor();
        var context = AuditContexts.Create(
            html: StoredHtml,
            capturedRequests: StoredCapture,
            auditId: stack.AuditId,
            urlId: stack.UrlId);
        var result = await auditor.AuditAsync(context);
        var metrics = MarTechMetricContributions.FromAudit(result, stack.UrlId);

        var repository = new SqlRepository<MetricSummaryDto>(stack.Provider);
        foreach (var metric in metrics)
        {
            await repository.CreateAsync(new MetricSummaryDto(
                0, stack.AuditId, metric.MetricKey, metric.DimensionsJson, metric.Value));
        }

        var page = await repository.ListAsync(new ListQuery(0, 100));
        Assert.Contains(page.Items, row => row.MetricKey == MarTechMetricKeys.MarTechStack);
        Assert.Contains(page.Items, row => row.MetricKey == MarTechMetricKeys.AnalyticsCoverage
            && row.DimensionsJson.Contains("google-tag-manager"));
    }

    [Fact]
    public async Task ElementGroups_ConsentStateAndDataLayerSnapshot_Persist()
    {
        await using var stack = await MarTechRealStack.CreateAsync();
        var parser = new AngleSharpDomParser();
        var document = parser.Parse(StoredHtml, "https://example.test/page");
        var extractor = new ElementInventoryExtractor();
        var inventory = await extractor.ExtractAsync(document);

        var repository = new SqlRepository<UrlElementDto>(stack.Provider);
        foreach (var item in inventory.Where(i =>
            i.Group == ElementGroups.ConsentState || i.Group == ElementGroups.DataLayerSnapshot))
        {
            await repository.CreateAsync(new UrlElementDto(0, stack.UrlId, item.Group, item.PayloadJson, item.Ordinal));
        }

        var page = await repository.ListAsync(new ListQuery(0, 100));
        Assert.Contains(page.Items, row => row.ElementGroup == ElementGroups.ConsentState);
        var dataLayer = Assert.Single(page.Items, row => row.ElementGroup == ElementGroups.DataLayerSnapshot);
        Assert.Contains("pushCount", dataLayer.PayloadJson);
    }

    [Fact]
    public async Task OfflineReAnalysis_FromStoredBodyAndCapture_MatchesOriginal()
    {
        await using var stack = await MarTechRealStack.CreateAsync();
        var artifact = await stack.StoreBodyAsync(StoredHtml);

        // Re-analysis reads ONLY stored artifacts: body from the content
        // store, capture from persisted rows. No fetch, no vendor lookup.
        await using var stream = await stack.ContentStore.OpenReadAsync(artifact.StorageKey);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var storedBody = await reader.ReadToEndAsync();

        var parser = new AngleSharpDomParser();
        var document = parser.Parse(storedBody, "https://example.test/page");
        var reanalysisContext = new Contracts.Audit.PageAuditContext(
            stack.AuditId,
            AuditContexts.Url(stack.UrlId, stack.AuditId, "https://example.test/page"),
            AuditContexts.Configuration(RenderMode.None),
            Fetch: null,
            document,
            RenderedDocument: null,
            ScopedText: null,
            StoredCapture);

        var auditor = new MarTechAuditor();
        var rerun = await auditor.AuditAsync(reanalysisContext);

        var originalContext = AuditContexts.Create(
            html: StoredHtml,
            capturedRequests: StoredCapture,
            auditId: stack.AuditId,
            urlId: stack.UrlId);
        var original = await auditor.AuditAsync(originalContext);

        Assert.Equal(
            original.Detections.OrderBy(d => d.Vendor).ThenBy(d => d.ContainerId),
            rerun.Detections.OrderBy(d => d.Vendor).ThenBy(d => d.ContainerId));
        Assert.Contains(rerun.Detections, d => d.ContainerId == "GTM-STORE1" && d.DetectionSource == "both");
    }
}
