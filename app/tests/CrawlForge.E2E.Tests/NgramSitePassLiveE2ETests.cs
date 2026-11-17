using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// D14 live wiring proof (criterion #38 site facet): the n-gram SITE pass must
/// run in the live audit pipeline after the per-URL passes — materializing
/// ngram_site_summaries and emitting content.keyword-cannibalization /
/// content.topical-gap findings plus the ngram metric contributions. Before the
/// fix RunSitePassAsync had zero call sites and the site REST surfaces were
/// empty. The offline re-audit path converges on the same hook and is proven
/// here too.
/// </summary>
public sealed class NgramSitePassLiveE2ETests
{
    /// <summary>
    /// Builds a site where three pages share the identical title "Widget galaxy"
    /// (cannibalization cluster: the shared bigram is each page's sole primary
    /// title term) and link to three DISTINCT destinations with the anchor
    /// "zebra comet" that never appears in any title or heading (topical gap:
    /// anchor DF 3 across targets ≥ default MinAnchorDf 3, zero on-page
    /// title/headings DF).
    /// </summary>
    private static Task<LoopbackOrigin> StartSiteAsync() => LoopbackOrigin.StartAsync(routes =>
    {
        routes.Map("/", OriginResponse.Html(FixturePages.Page(
            "Site hub",
            "<p>Navigation hub for the n-gram site pass fixture.</p>" +
            FixturePages.Links("/a", "/b", "/c", "/d", "/e", "/f"))));
        routes.Map("/a", OriginResponse.Html(FixturePages.Page(
            "Widget galaxy",
            "<p>The widget galaxy laboratory studies widget galaxy patterns in depth.</p>" +
            "<p><a href=\"/d\">zebra comet</a></p>")));
        routes.Map("/b", OriginResponse.Html(FixturePages.Page(
            "Widget galaxy",
            "<p>The widget galaxy observatory tracks widget galaxy motion nightly.</p>" +
            "<p><a href=\"/e\">zebra comet</a></p>")));
        routes.Map("/c", OriginResponse.Html(FixturePages.Page(
            "Widget galaxy",
            "<p>The widget galaxy archive preserves widget galaxy records permanently.</p>" +
            "<p><a href=\"/f\">zebra comet</a></p>")));
        routes.Map("/d", OriginResponse.Html(FixturePages.Page(
            "Destination overview",
            "<p>This destination page describes ordinary topics and never repeats the inbound anchor phrase in its title or headings.</p>")));
        routes.Map("/e", OriginResponse.Html(FixturePages.Page(
            "Destination digest",
            "<p>This destination digest covers ordinary topics without the inbound anchor phrase in its title or headings.</p>")));
        routes.Map("/f", OriginResponse.Html(FixturePages.Page(
            "Destination report",
            "<p>This destination report surveys ordinary topics and keeps the inbound anchor phrase out of its title and headings.</p>")));
    });

    [Fact]
    public async Task LiveAudit_PopulatesSiteSummaries_Clusters_Gaps_Findings_AndMetrics()
    {
        await using var origin = await StartSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        // Site summaries materialized live (previously EMPTY: zero call sites).
        using var siteResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/ngrams");
        siteResponse.EnsureSuccessStatusCode();
        var site = await WireJson.EnvelopeAsync(siteResponse);
        Assert.True(site.GetProperty("data").GetArrayLength() > 0, "site ngram summaries must populate live");
        Assert.True(site.GetProperty("pagination").GetProperty("total").GetInt64() > 0);

        // Cannibalization clusters surface the shared title bigram.
        using var clusterResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/ngrams/cannibalization");
        clusterResponse.EnsureSuccessStatusCode();
        var clusterJson = await clusterResponse.Content.ReadAsStringAsync();
        Assert.Contains("widget galaxy", clusterJson);
        var clusters = await WireJson.EnvelopeAsync(clusterResponse);
        Assert.True(clusters.GetProperty("data").GetArrayLength() > 0);

        // Topical gaps surface the anchor-only bigram.
        using var gapResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/ngrams/gaps");
        gapResponse.EnsureSuccessStatusCode();
        var gapJson = await gapResponse.Content.ReadAsStringAsync();
        Assert.Contains("zebra comet", gapJson);

        // Site-level findings persisted (UrlId null) with structured evidence.
        // The gap family includes the anchor unigrams and the bigram.
        var findings = await backend.ListByAuditAsync<FindingDto>(auditId);
        var cannibalization = Assert.Single(findings, f => f.RuleKey == "content.keyword-cannibalization");
        Assert.Null(cannibalization.UrlId);
        Assert.Contains("widget galaxy", cannibalization.EvidenceJson);
        var gap = Assert.Single(findings, f => f.RuleKey == "content.topical-gap" && f.EvidenceJson.Contains("zebra comet"));
        Assert.Null(gap.UrlId);
        Assert.Equal(FindingSeverity.Notice, gap.Severity);

        // Metric contributions persisted by the site pass.
        var metrics = await backend.ListByAuditAsync<MetricSummaryDto>(auditId);
        Assert.Contains(metrics, m => m.MetricKey == "ngram-top");
        Assert.Contains(metrics, m => m.MetricKey == "cannibalization-clusters" && m.Value >= 1);
        Assert.Contains(metrics, m => m.MetricKey == "topical-gaps" && m.Value >= 1);
    }

    [Fact]
    public async Task OfflineReaudit_RegeneratesSiteSummaries_ForTheNewAudit()
    {
        await using var origin = await StartSiteAsync();
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);
        var auditId = await backend.StartAuditAsync(projectId, configurationId);
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        // Offline re-audit (no re-fetch) converges on the same analysis phase:
        // the site pass must run for the NEW audit id as well.
        var reAuditId = await backend.Service<IAuditLifecyclePort>().ReAuditAsync(auditId, refresh: false);
        await backend.WaitForStatusAsync(reAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(120));

        using var siteResponse = await backend.Http.GetAsync($"/api/audits/{reAuditId}/ngrams");
        siteResponse.EnsureSuccessStatusCode();
        var site = await WireJson.EnvelopeAsync(siteResponse);
        Assert.True(site.GetProperty("data").GetArrayLength() > 0, "re-audit site summaries must regenerate");

        var findings = await backend.ListByAuditAsync<FindingDto>(reAuditId);
        Assert.Contains(findings, f => f.RuleKey == "content.keyword-cannibalization");
        Assert.Contains(findings, f => f.RuleKey == "content.topical-gap");

        // The source audit keeps its own materialized summaries (per-audit rows).
        using var sourceResponse = await backend.Http.GetAsync($"/api/audits/{auditId}/ngrams");
        var source = await WireJson.EnvelopeAsync(sourceResponse);
        Assert.True(source.GetProperty("data").GetArrayLength() > 0);
    }
}
