using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the audit report explorers: URL list and
/// detail (with additive diagnostics), links, findings, dashboard summary,
/// chart series, graph, and scores.
/// </summary>
public sealed class AuditExplorerRouteTests
{
    private static async Task<(long AuditId, UrlRecordDto Url)> SeedAuditWithUrlAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/page");
        return (audit.Id, url);
    }

    [Fact]
    public async Task Urls_List_ProjectsSummaries_WithTitlesAndDerivedStatusClass()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        await host.SeedUrlAsync(auditId, "https://example.com/missing", CrawlStatusClass.ClientError, 404, 1);
        var blocked = await host.SeedUrlAsync(auditId, "https://example.com/blocked", CrawlStatusClass.Blocked, 200, 1);
        await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(blocked with { BlockReason = "waf-challenge", WafProvider = WafProvider.Cloudflare });
        await host.Get<IWriteRepository<UrlElementDto>>().CreateAsync(new UrlElementDto(
            0, url.Id, "document_head", "{\"title\":\"Home Page\"}", 0));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/urls");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 3);
        var items = await response.ReadDataAsync<List<UrlRecordSummaryWire>>();
        var home = items.Single(u => u.Id == url.Id.ToString());
        Assert.Equal("Home Page", home.Title);
        Assert.Equal("2xx", home.StatusClass);
        Assert.Equal(200, home.StatusCode);
        Assert.True(home.Indexable);
        var missing = items.Single(u => u.CanonicalUrl.EndsWith("/missing"));
        Assert.Equal("4xx", missing.StatusClass);
        var blockedWire = items.Single(u => u.CanonicalUrl.EndsWith("/blocked"));
        Assert.Equal("waf-challenge", blockedWire.BlockReason);
        Assert.Equal("cloudflare", blockedWire.WafProvider);
        Assert.Equal("2xx", blockedWire.StatusClass);
    }

    [Fact]
    public async Task Urls_List_FiltersByStatusDepthIndexableSearchAndWaf()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        await host.SeedUrlAsync(auditId, "https://example.com/deep", CrawlStatusClass.ClientError, 404, 3);
        var blocked = await host.SeedUrlAsync(auditId, "https://example.com/waf", CrawlStatusClass.Blocked, 200, 1);
        await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(blocked with { WafProvider = WafProvider.DataDome, IsIndexable = false });

        var byStatus = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls?statusClass=4xx")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Single(byStatus);

        var byDepth = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls?depth=3")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Single(byDepth);

        var byIndexable = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls?indexable=false")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Equal(2, byIndexable.Count);

        var bySearch = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls?search=deep")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Single(bySearch);

        var byWaf = await (await host.Client.GetAsync($"/api/audits/{auditId}/urls?wafProvider=datadome")).ReadDataAsync<List<UrlRecordSummaryWire>>();
        Assert.Single(byWaf);

        var invalid = await host.Client.GetAsync($"/api/audits/{auditId}/urls?statusClass=9xx");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task UrlDetail_ProjectsAllAdditiveDiagnostics()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var other = await host.SeedUrlAsync(auditId, "https://example.com/other");
        var elements = host.Get<IWriteRepository<UrlElementDto>>();
        await elements.CreateAsync(new UrlElementDto(0, url.Id, "document_head", "{\"title\":\"Detail Page\"}", 0));
        await elements.CreateAsync(new UrlElementDto(0, url.Id, ElementGroups.HeaderDirectives,
            "{\"xRobotsTag\":\"noindex\",\"contentType\":\"text/html\",\"headerCount\":2}", 0));
        await elements.CreateAsync(new UrlElementDto(0, url.Id, ElementGroups.ContentScope,
            "{\"configured\":true,\"scopedWordCount\":120,\"fullWordCount\":200,\"includeSelectors\":[\"main\"],\"excludeSelectors\":[\"nav\"],\"perSelector\":[{\"expression\":\"main\",\"matchCount\":1},{\"expression\":\"nav\",\"matchCount\":2}]}", 0));
        await elements.CreateAsync(new UrlElementDto(0, url.Id, ElementGroups.SerpMetrics,
            "{\"title\":{\"widthPixels\":650.5,\"isTruncated\":true},\"description\":{\"widthPixels\":900,\"isTruncated\":false}}", 0));
        await elements.CreateAsync(new UrlElementDto(0, url.Id, "anchors", "{}", 0));
        await elements.CreateAsync(new UrlElementDto(0, url.Id, "anchors", "{}", 1));

        await host.SeedFindingAsync(auditId, "title.missing", urlId: url.Id, evidenceJson: "{\"message\":\"Title is missing\",\"snippet\":\"<head></head>\"}");
        await host.SeedFindingAsync(auditId, FindingRuleKeys.IndexabilityHeaderHtmlConflict, AuditCategory.Indexability, FindingSeverity.Error, url.Id,
            "{\"headerValue\":\"noindex\",\"htmlValue\":\"index\"}");

        var links = host.Get<IWriteRepository<LinkEdgeDto>>();
        await links.CreateAsync(new LinkEdgeDto(0, auditId, url.Id, other.CanonicalUrl, other.Id, "Other page", LinkRelationFlags.None, true)
        {
            DomZone = DomZone.MainContent,
            ZoneEvidence = "{\"weight\":0.9}",
            WeightedEquity = 0.75,
        });
        await links.CreateAsync(new LinkEdgeDto(0, auditId, other.Id, url.CanonicalUrl, url.Id, "Back", LinkRelationFlags.None, true));

        await host.Get<IWriteRepository<WafDetectionDto>>().CreateAsync(new WafDetectionDto(
            0, auditId, url.Id, WafProvider.Cloudflare, WafChallengeClass.Interstitial,
            "{\"snippet\":\"cf-challenge\",\"confidence\":0.9,\"headers\":{\"server\":\"cloudflare\"}}",
            "cf-interstitial", false, DateTimeOffset.UtcNow));
        await host.Get<IWriteRepository<NgramFrequencyDto>>().CreateAsync(new NgramFrequencyDto(0, auditId, url.Id, TextZone.Body, 2, "crawl forge", 7, 0.4));
        await host.Get<IWriteRepository<SemanticLinkOpportunityDto>>().CreateAsync(new SemanticLinkOpportunityDto(0, auditId, url.Id, other.Id, 0.82, null));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/urls/{url.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.ReadDataAsync<UrlDetailWire>();
        Assert.Equal("Detail Page", detail.Title);
        Assert.Equal("noindex", detail.ResponseHeaders["x-robots-tag"]);
        Assert.Equal(2, detail.ElementInventory.Counts["anchors"]);
        Assert.Equal(6, detail.ElementInventory.TotalElements);
        Assert.Equal(2, detail.Findings.Count);
        Assert.Contains(detail.Findings, f => f.Message == "Title is missing");
        Assert.Single(detail.OutboundLinks);
        Assert.Equal("mainContent", detail.OutboundLinks[0].DomZone);
        Assert.Equal(0.9, detail.OutboundLinks[0].EquityWeight);
        Assert.Equal(0.75, detail.OutboundLinks[0].WeightedEquity);
        Assert.Single(detail.InboundLinks);
        Assert.NotNull(detail.WafDetection);
        Assert.Equal("cloudflare", detail.WafDetection!.Provider);
        Assert.Equal("interstitial", detail.WafDetection.ChallengeClass);
        Assert.Equal(0.9, detail.WafDetection.Confidence);
        Assert.Equal("cf-interstitial", detail.WafDetection.SignatureKey);
        Assert.NotNull(detail.ScopeDiagnostics);
        Assert.True(detail.ScopeDiagnostics!.Included);
        Assert.Equal(120, detail.ScopeDiagnostics.ScopedWordCount);
        Assert.Equal(200, detail.ScopeDiagnostics.TotalWordCount);
        Assert.Equal(new[] { "main" }, detail.ScopeDiagnostics.MatchedIncludeSelectors);
        Assert.Equal(new[] { "nav" }, detail.ScopeDiagnostics.MatchedExcludeSelectors);
        Assert.NotNull(detail.TopNgrams);
        Assert.Equal("crawl forge", detail.TopNgrams![0].Ngram);
        Assert.Equal("body", detail.TopNgrams[0].Zone);
        Assert.NotNull(detail.SerpMetrics);
        Assert.Equal(650.5, detail.SerpMetrics!.TitlePixelWidth);
        Assert.True(detail.SerpMetrics.TitleTruncated);
        Assert.NotNull(detail.HeaderConflicts);
        Assert.True(detail.HeaderConflicts![0].Conflict);
        Assert.NotNull(detail.SemanticRelations);
        Assert.Equal(other.Id.ToString(), detail.SemanticRelations![0].RelatedUrlId);
        Assert.Equal(0.82, detail.SemanticRelations[0].Similarity);
    }

    [Fact]
    public async Task UrlDetail_CrossAuditOrUnknown_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var (otherAuditId, _) = await SeedAuditWithUrlAsync(host);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/audits/{otherAuditId}/urls/{url.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/audits/{auditId}/urls/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/audits/999/urls")).StatusCode);
    }

    [Fact]
    public async Task Links_List_FiltersByInternalAndDomZone()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var other = await host.SeedUrlAsync(auditId, "https://example.com/other");
        var links = host.Get<IWriteRepository<LinkEdgeDto>>();
        await links.CreateAsync(new LinkEdgeDto(0, auditId, url.Id, other.CanonicalUrl, other.Id, "Internal", LinkRelationFlags.None, true) { DomZone = DomZone.Navigation });
        await links.CreateAsync(new LinkEdgeDto(0, auditId, url.Id, "https://external.example.org/", null, "External", LinkRelationFlags.None, false));

        var all = await (await host.Client.GetAsync($"/api/audits/{auditId}/links")).ReadDataAsync<List<LinkEdgeWire>>();
        Assert.Equal(2, all.Count);
        Assert.Equal(string.Empty, all.Single(e => !e.Internal).TargetUrlId);

        var internalOnly = await (await host.Client.GetAsync($"/api/audits/{auditId}/links?internal=true")).ReadDataAsync<List<LinkEdgeWire>>();
        Assert.Single(internalOnly);

        var navOnly = await (await host.Client.GetAsync($"/api/audits/{auditId}/links?domZone=navigation")).ReadDataAsync<List<LinkEdgeWire>>();
        Assert.Single(navOnly);

        var invalidZone = await host.Client.GetAsync($"/api/audits/{auditId}/links?domZone=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalidZone.StatusCode);
    }

    [Fact]
    public async Task Findings_List_FiltersAndPaginates()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        await host.SeedFindingAsync(auditId, "title.missing", AuditCategory.Content, FindingSeverity.Error, url.Id);
        await host.SeedFindingAsync(auditId, "meta.description", AuditCategory.Content, FindingSeverity.Warning, url.Id);
        await host.SeedFindingAsync(auditId, "canonical.missing", AuditCategory.Indexability, FindingSeverity.Warning);

        var bySeverity = await (await host.Client.GetAsync($"/api/audits/{auditId}/findings?severity=error")).ReadDataAsync<List<FindingWire>>();
        Assert.Single(bySeverity);
        Assert.Equal("title.missing", bySeverity[0].RuleKey);

        var byCategory = await (await host.Client.GetAsync($"/api/audits/{auditId}/findings?category=indexability")).ReadDataAsync<List<FindingWire>>();
        Assert.Single(byCategory);

        var byRule = await (await host.Client.GetAsync($"/api/audits/{auditId}/findings?ruleKey=meta.description")).ReadDataAsync<List<FindingWire>>();
        Assert.Single(byRule);

        var byUrl = await (await host.Client.GetAsync($"/api/audits/{auditId}/findings?urlId={url.Id}")).ReadDataAsync<List<FindingWire>>();
        Assert.Equal(2, byUrl.Count);

        var paged = await host.Client.GetAsync($"/api/audits/{auditId}/findings?offset=0&limit=2");
        using var document = await paged.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 2, 3);

        var invalid = await host.Client.GetAsync($"/api/audits/{auditId}/findings?severity=catastrophic");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Summary_AggregatesCountsHealthWafAndThrottle()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"queued\":0,\"fetched\":10,\"error\":1,\"blocked\":2}");
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/ok");
        await host.SeedUrlAsync(audit.Id, "https://example.com/missing", CrawlStatusClass.ClientError, 404);
        await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(url with { TimingJson = "{\"ttfbMs\":120}" });
        await host.SeedFindingAsync(audit.Id, "title.missing", AuditCategory.Content, FindingSeverity.Error, url.Id);
        await host.SeedFindingAsync(audit.Id, "meta.description", AuditCategory.Content, FindingSeverity.Warning, url.Id);
        await host.Get<IWriteRepository<WafDetectionDto>>().CreateAsync(new WafDetectionDto(
            0, audit.Id, url.Id, WafProvider.Cloudflare, WafChallengeClass.BotBlock, "{}", null, false, DateTimeOffset.UtcNow));
        await host.Get<IWriteRepository<HostThrottleEventDto>>().CreateAsync(new HostThrottleEventDto(0, audit.Id, "example.com", 2.0, 1.0, "latency", DateTimeOffset.UtcNow));

        var response = await host.Client.GetAsync($"/api/audits/{audit.Id}/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.ReadDataAsync<AuditDashboardSummaryWire>();
        Assert.Equal(1, summary.CountsByStatus["2xx"]);
        Assert.Equal(1, summary.CountsByStatus["4xx"]);
        Assert.Equal(1, summary.CountsBySeverity["error"]);
        Assert.Equal(1, summary.CountsBySeverity["warning"]);
        Assert.Equal(2, summary.CountsByCategory["content"]);
        Assert.Equal(2, summary.CrawlHealth.TotalUrls);
        Assert.Equal(10, summary.CrawlHealth.Fetched);
        Assert.Equal(120, summary.CrawlHealth.AverageTtfbMs);
        Assert.NotNull(summary.Waf);
        Assert.Equal(1, summary.Waf!.TotalBlocked);
        Assert.Equal(0.1, summary.Waf.BlockRate);
        Assert.Equal(1, summary.Waf.ByProvider["cloudflare"]);
        Assert.Equal(1, summary.Waf.ByChallengeClass["botblock"]);
        Assert.NotNull(summary.Throttle);
        Assert.Equal(1, summary.Throttle!.ThrottledHostCount);
        Assert.Equal(1.0, summary.Throttle.MeanEffectiveRate);
        Assert.Equal(1, summary.Throttle.AdjustmentCount);
    }

    [Fact]
    public async Task Charts_ReturnPreBucketedPoints_FromMetricAggregates()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        var metrics = host.Get<IWriteRepository<MetricSummaryDto>>();
        await metrics.CreateAsync(new MetricSummaryDto(0, auditId, "waf-blocks", "{\"label\":\"cloudflare\",\"provider\":\"cloudflare\"}", 5));
        await metrics.CreateAsync(new MetricSummaryDto(0, auditId, "waf-blocks", "{\"label\":\"datadome\",\"provider\":\"datadome\"}", 2));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/charts/waf-blocks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var series = await response.ReadDataAsync<ChartSeriesResponseWire>();
        Assert.Equal("waf-blocks", series.Metric);
        Assert.Equal(2, series.Points.Count);
        Assert.Contains(series.Points, p => p.Label == "cloudflare" && p.Value == 5 && p.Dimensions!["provider"] == "cloudflare");

        var empty = await (await host.Client.GetAsync($"/api/audits/{auditId}/charts/unknown-metric")).ReadDataAsync<ChartSeriesResponseWire>();
        Assert.Empty(empty.Points);
    }

    [Fact]
    public async Task Graph_LinkNetwork_RespectsCaps_AndReportsTotals()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var second = await host.SeedUrlAsync(auditId, "https://example.com/second");
        var third = await host.SeedUrlAsync(auditId, "https://example.com/third");
        var links = host.Get<IWriteRepository<LinkEdgeDto>>();
        await links.CreateAsync(new LinkEdgeDto(0, auditId, url.Id, second.CanonicalUrl, second.Id, null, LinkRelationFlags.None, true) { WeightedEquity = 1.0 });
        await links.CreateAsync(new LinkEdgeDto(0, auditId, second.Id, third.CanonicalUrl, third.Id, null, LinkRelationFlags.None, true) { WeightedEquity = 2.0 });

        var full = await (await host.Client.GetAsync($"/api/audits/{auditId}/graph?type=link-network")).ReadDataAsync<GraphDataWire>();
        Assert.Equal(3, full.Nodes.Count);
        Assert.Equal(2, full.Edges.Count);
        Assert.Equal(3, full.TotalNodeCount);
        Assert.Equal(2, full.TotalEdgeCount);
        Assert.False(full.Truncated);
        Assert.Contains(full.Nodes, n => n.LinkEquityPercentile is not null);

        var capped = await (await host.Client.GetAsync($"/api/audits/{auditId}/graph?type=link-network&maxNodes=2&maxEdges=1")).ReadDataAsync<GraphDataWire>();
        Assert.Equal(2, capped.Nodes.Count);
        Assert.Single(capped.Edges);
        Assert.True(capped.Truncated);
        Assert.Equal(3, capped.TotalNodeCount);

        var invalid = await host.Client.GetAsync($"/api/audits/{auditId}/graph?type=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Graph_RedirectChains_ProjectsStoredRedirectTargets()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var target = await host.SeedUrlAsync(auditId, "https://example.com/final");
        await host.Get<IWriteRepository<UrlRecordDto>>().UpdateAsync(url with { RedirectTargetUrl = target.CanonicalUrl, StatusClass = CrawlStatusClass.Redirect, FinalStatusCode = 301 });

        var graph = await (await host.Client.GetAsync($"/api/audits/{auditId}/graph?type=redirect-chains")).ReadDataAsync<GraphDataWire>();
        Assert.Single(graph.Edges);
        Assert.Equal(url.Id.ToString(), graph.Edges[0].Source);
        Assert.Equal(target.Id.ToString(), graph.Edges[0].Target);
    }

    [Fact]
    public async Task Scores_ProjectOverallAndCategories()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        var scores = host.Get<IWriteRepository<AuditScoreDto>>();
        await scores.CreateAsync(new AuditScoreDto(0, auditId, null, 87.5, 1, 2, 3, 4));
        await scores.CreateAsync(new AuditScoreDto(0, auditId, AuditCategory.Content, 90, 0, 1, 1, 2));
        await scores.CreateAsync(new AuditScoreDto(0, auditId, AuditCategory.Indexability, 85, 1, 1, 2, 2));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/scores");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wire = await response.ReadDataAsync<AuditScoresWire>();
        Assert.Equal(87.5, wire.Overall);
        Assert.Equal(2, wire.Categories.Count);
        var content = wire.Categories.Single(c => c.Category == "content");
        Assert.Equal(90, content.Score);
        Assert.Equal(4, content.FindingCount);
    }
}
