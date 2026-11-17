using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the host AIMD explorer/override and the
/// n-gram explorer (site/url rows, cannibalization clusters, topical gaps).
/// </summary>
public sealed class HostsNgramsRouteTests
{
    private static async Task<long> SeedAuditAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        return audit.Id;
    }

    [Fact]
    public async Task Hosts_List_ProjectsCheckpointState_WithHistoryAndThrottledFlag()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        host.Fakes.Aimd.Seed(new HostThrottleState("example.com", 1.5, 2, 120, 3, DateTimeOffset.UtcNow, null));
        await host.Get<IWriteRepository<HostThrottleEventDto>>().CreateAsync(new HostThrottleEventDto(
            0, auditId, "example.com", 2.0, 1.5, "ttfb-strain", DateTimeOffset.UtcNow));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/hosts?includeHistory=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<HostStateWire>>();
        var row = Assert.Single(items);
        Assert.Equal("example.com", row.Host);
        Assert.Equal(1.5, row.EffectiveRate);
        Assert.Equal(2, row.Concurrency);
        Assert.Equal(120, row.TtfbEwmaMs);
        Assert.True(row.Throttled);
        Assert.Null(row.Override);
        var adjustment = Assert.Single(row.History!);
        Assert.Equal("ttfb-strain", adjustment.Trigger);
        Assert.Equal(2.0, adjustment.RateBefore);
        Assert.Equal(1.5, adjustment.RateAfter);
    }

    [Fact]
    public async Task Hosts_Override_PinsRateOnly_AndClears()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        host.Fakes.Aimd.Seed(new HostThrottleState("example.com", 2.0, 1, 0, 0, null, null));

        var response = await host.Client.PostJsonAsync($"/api/audits/{auditId}/hosts/example.com/override", new HostOverrideRequest(0.5, 4));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pinned = await response.ReadDataAsync<HostStateWire>();
        Assert.Equal(0.5, pinned.Override!.PinnedRate);
        Assert.Null(pinned.Override.PinnedConcurrency);
        Assert.Equal(0.5, pinned.EffectiveRate);

        var cleared = await (await host.Client.PostJsonAsync($"/api/audits/{auditId}/hosts/example.com/override", new HostOverrideRequest(null, null))).ReadDataAsync<HostStateWire>();
        Assert.Null(cleared.Override);

        var negative = await host.Client.PostJsonAsync($"/api/audits/{auditId}/hosts/example.com/override", new HostOverrideRequest(-1, null));
        await ControllerTestHelpers.AssertErrorShape(negative, "validation");
    }

    [Fact]
    public async Task Hosts_UnknownAudit_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.GetAsync("/api/audits/999/hosts");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ngrams_List_SiteRows_ProjectSummaryFields()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        await host.Get<IWriteRepository<NgramSiteSummaryDto>>().CreateAsync(new NgramSiteSummaryDto(
            0, auditId, TextZone.Title, 1, "seo", 5, 20, 0.4, 3));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/ngrams");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<NgramRowWire>>();
        var row = Assert.Single(items);
        Assert.Equal("seo", row.Ngram);
        Assert.Equal(1, row.N);
        Assert.Equal("title", row.Zone);
        Assert.Equal(20, row.Count);
        Assert.Equal(0.4, row.Density);
        Assert.Null(row.UrlId);
    }

    [Fact]
    public async Task Ngrams_List_UrlRows_ProjectFrequencyFields()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var url = await host.SeedUrlAsync(auditId, "https://example.com/page");
        await host.Get<IWriteRepository<NgramFrequencyDto>>().CreateAsync(new NgramFrequencyDto(
            0, auditId, url.Id, TextZone.Body, 2, "seo audit", 7, 0.25));

        var items = await (await host.Client.GetAsync($"/api/audits/{auditId}/ngrams?urlId={url.Id}")).ReadDataAsync<List<NgramRowWire>>();
        var row = Assert.Single(items);
        Assert.Equal("seo audit", row.Ngram);
        Assert.Equal("body", row.Zone);
        Assert.Equal(7, row.Count);
        Assert.Equal(0.25, row.Density);
        Assert.Equal(url.Id.ToString(), row.UrlId);
    }

    [Fact]
    public async Task Ngrams_Cannibalization_ReadsPersistedFindings()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var urlA = await host.SeedUrlAsync(auditId, "https://example.com/a");
        var urlB = await host.SeedUrlAsync(auditId, "https://example.com/b");
        var evidence = $"{{\"term\":\"seo\",\"zone\":\"title\",\"order\":1,\"siteDf\":2,\"sameCanonicalTarget\":false,\"members\":[{{\"urlId\":{urlA.Id},\"url\":\"https://example.com/a\",\"tf\":3.0}},{{\"urlId\":{urlB.Id},\"url\":\"https://example.com/b\",\"tf\":2.0}}]}}";
        await host.SeedFindingAsync(auditId, FindingRuleKeys.ContentKeywordCannibalization, AuditCategory.Content, FindingSeverity.Warning, null, evidence);

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/ngrams/cannibalization");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.ReadDataAsync<List<NgramCannibalizationClusterWire>>();
        var cluster = Assert.Single(items);
        Assert.Equal("seo", cluster.Ngram);
        Assert.Equal(2, cluster.Pages.Count);
        Assert.Equal(urlA.Id.ToString(), cluster.Pages[0].UrlId);
        Assert.Equal(3, cluster.Pages[0].Occurrences);
    }

    [Fact]
    public async Task Ngrams_Gaps_ReadsPersistedFindings_AndComputesGapScore()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        await host.SeedFindingAsync(auditId, FindingRuleKeys.ContentTopicalGap, AuditCategory.Content, FindingSeverity.Notice, null,
            "{\"term\":\"audit\",\"order\":1,\"anchorDf\":2,\"threshold\":10,\"sampleSourceUrls\":[\"https://example.com/src\"]}");

        var items = await (await host.Client.GetAsync($"/api/audits/{auditId}/ngrams/gaps")).ReadDataAsync<List<TopicalGapWire>>();
        var gap = Assert.Single(items);
        Assert.Equal("audit", gap.Ngram);
        Assert.Equal(2, gap.ObservedCount);
        Assert.Equal(10, gap.ExpectedCount);
        Assert.Equal(0.8, gap.GapScore);
    }

    [Fact]
    public async Task Ngrams_InvalidZone_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var auditId = await SeedAuditAsync(host);
        var response = await host.Client.GetAsync($"/api/audits/{auditId}/ngrams?zone=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "validation");
    }
}
