using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// D15 live wiring proof (criterion #48 zone/equity facet): link discovery must
/// persist anchor text + DOM-zone classification + evidence on every edge, and
/// the post-crawl enrichment pass must resolve target_url_id, compute weighted
/// link equity (metric rows), and stamp per-edge weighted_equity — so the live
/// graph REST surface and read model return zones/equity instead of NULLs.
/// The offline re-audit copies the graph with remapped references and
/// recomputes equity for the new audit.
/// </summary>
public sealed class LinkGraphEquityLiveE2ETests
{
    /// <summary>
    /// Hub with navigation, in-content, and footer anchors to two targets; the
    /// alpha page links to beta in-content. Zones are distinguishable by the
    /// classifier's landmark precedence (nav &gt; header &gt; footer; main = content).
    /// </summary>
    private static Task<LoopbackOrigin> StartSiteAsync() => LoopbackOrigin.StartAsync(routes =>
    {
        routes.Map("/", OriginResponse.Html(FixturePages.Page(
            "Graph hub",
            "<nav><a href=\"/a\">Nav alpha</a></nav>" +
            "<main><p>The hub describes the graph fixture in ordinary prose.</p>" +
            "<p><a href=\"/b\">Content beta</a></p></main>" +
            "<footer><a href=\"/a\">Footer alpha</a></footer>")));
        routes.Map("/a", OriginResponse.Html(FixturePages.Page(
            "Alpha page",
            "<main><p>Alpha links onward to beta from its content region.</p>" +
            "<p><a href=\"/b\">Alpha to beta</a></p></main>")));
        routes.Map("/b", OriginResponse.Html(FixturePages.Page(
            "Beta page",
            "<main><p>Beta is the equity sink of this small fixture graph.</p></main>")));
    });

    [Fact]
    public async Task LiveAudit_PopulatesZones_Anchors_Targets_AndWeightedEquity()
    {
        await using var origin = await StartSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        var edges = await backend.ListByAuditAsync<LinkEdgeDto>(auditId);
        Assert.NotEmpty(edges);

        // Every discovered anchor carries text, zone classification, evidence.
        Assert.All(edges, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.AnchorText));
            Assert.NotNull(e.DomZone);
            Assert.False(string.IsNullOrWhiteSpace(e.ZoneEvidence));
        });

        // Zone precedence: nav anchor → Navigation, footer anchor → Footer,
        // main-region anchors → MainContent.
        var hubEdges = edges.Where(e => e.AnchorText is "Nav alpha" or "Footer alpha" or "Content beta").ToList();
        Assert.Contains(hubEdges, e => e.AnchorText == "Nav alpha" && e.DomZone == DomZone.Navigation);
        Assert.Contains(hubEdges, e => e.AnchorText == "Footer alpha" && e.DomZone == DomZone.Footer);
        Assert.Contains(hubEdges, e => e.AnchorText == "Content beta" && e.DomZone == DomZone.MainContent);
        Assert.Contains(edges, e => e.AnchorText == "Alpha to beta" && e.DomZone == DomZone.MainContent);

        // Internal edges resolve their target URL references after the crawl.
        var internalEdges = edges.Where(e => e.IsInternal).ToList();
        Assert.NotEmpty(internalEdges);
        Assert.All(internalEdges, e => Assert.NotNull(e.TargetUrlId));

        // Participating internal edges carry positive weighted equity.
        var stamped = internalEdges.Where(e => e.TargetUrlId != e.SourceUrlId).ToList();
        Assert.NotEmpty(stamped);
        Assert.All(stamped, e =>
        {
            Assert.NotNull(e.WeightedEquity);
            Assert.True(e.WeightedEquity > 0, "edge weighted equity must be positive");
        });

        // Equity metric rows: per-URL weighted + unweighted + concentration.
        var metrics = await backend.ListByAuditAsync<MetricSummaryDto>(auditId);
        var urls = await backend.ListByAuditAsync<UrlRecordDto>(auditId);
        var weighted = metrics.Where(m => m.MetricKey == "link-equity-weighted").ToList();
        Assert.Equal(urls.Count, weighted.Count);
        Assert.All(weighted, m => Assert.True(m.Value > 0));
        Assert.Contains(metrics, m => m.MetricKey == "link-equity-unweighted");
        Assert.Contains(metrics, m => m.MetricKey == "equity-concentration" && m.Value > 0 && m.Value <= 1);

        // The equity sink (beta, two in-content inbound edges) outranks the hub.
        var beta = urls.Single(u => u.CanonicalUrl.EndsWith("/b", StringComparison.Ordinal));
        var hub = urls.Single(u => u.Depth == 0);
        var betaEquity = weighted.Single(m => m.DimensionsJson.Contains($"\"urlId\":{beta.Id}")).Value;
        var hubEquity = weighted.Single(m => m.DimensionsJson.Contains($"\"urlId\":{hub.Id}")).Value;
        Assert.True(betaEquity > hubEquity, "in-content equity must concentrate on the sink");

        // Live REST graph surface: link-network edges now render with weights.
        using var graphResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/graph?type=link-network");
        graphResponse.EnsureSuccessStatusCode();
        var graph = await WireJson.EnvelopeAsync(graphResponse);
        var graphEdges = graph.GetProperty("data").GetProperty("edges");
        Assert.True(graphEdges.GetArrayLength() > 0, "link-network edges require resolved targets");
        Assert.Contains(graphEdges.EnumerateArray(), e =>
            e.TryGetProperty("weight", out var weight) && weight.ValueKind == System.Text.Json.JsonValueKind.Number && weight.GetDouble() > 0);
    }

    [Fact]
    public async Task OfflineReaudit_CopiesGraph_RemapsReferences_RecomputesEquity()
    {
        await using var origin = await StartSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));
        var sourceEdges = await backend.ListByAuditAsync<LinkEdgeDto>(auditId);

        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        var reEdges = await backend.ListByAuditAsync<LinkEdgeDto>(reAuditId);
        Assert.Equal(sourceEdges.Count, reEdges.Count);
        Assert.All(reEdges, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.AnchorText));
            Assert.NotNull(e.DomZone);
        });

        var reInternal = reEdges.Where(e => e.IsInternal).ToList();
        Assert.All(reInternal, e => Assert.NotNull(e.TargetUrlId));
        Assert.All(reInternal.Where(e => e.TargetUrlId != e.SourceUrlId), e =>
        {
            Assert.NotNull(e.WeightedEquity);
            Assert.True(e.WeightedEquity > 0);
        });

        // References point at the NEW audit's URL records, never the source's.
        var reUrls = await backend.ListByAuditAsync<UrlRecordDto>(reAuditId);
        var reUrlIds = reUrls.Select(u => u.Id).ToHashSet();
        Assert.All(reInternal, e =>
        {
            Assert.Contains(e.SourceUrlId, reUrlIds);
            Assert.Contains(e.TargetUrlId!.Value, reUrlIds);
        });

        var reMetrics = await backend.ListByAuditAsync<MetricSummaryDto>(reAuditId);
        Assert.Contains(reMetrics, m => m.MetricKey == "link-equity-weighted" && m.Value > 0);
    }
}
