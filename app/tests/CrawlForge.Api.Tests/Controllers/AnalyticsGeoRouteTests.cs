using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for the MarTech analytics explorer/coverage
/// and the GEO llms.txt validation / bot-governance matrix.
/// </summary>
public sealed class AnalyticsGeoRouteTests
{
    private static async Task<(long AuditId, UrlRecordDto Url)> SeedAuditWithUrlAsync(ControllerTestHost host)
    {
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/page");
        return (audit.Id, url);
    }

    [Fact]
    public async Task Analytics_ReturnsTagsConsentAndFindings()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        var writes = host.Get<IWriteRepository<AnalyticsDetectionDto>>();
        await writes.CreateAsync(new AnalyticsDetectionDto(0, auditId, url.Id, "Google Analytics", "GTM-1", "dom", null));
        await writes.CreateAsync(new AnalyticsDetectionDto(0, auditId, url.Id, "ConsentMode", null, "dom", "consent-mode-v2"));
        await writes.CreateAsync(new AnalyticsDetectionDto(0, auditId, url.Id, "Beacon", null, "proxy", null));
        await host.SeedFindingAsync(auditId, "analytics.missing-consent", AuditCategory.AnalyticsTagging);

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/analytics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<AnalyticsResponseWire>();
        Assert.Equal(3, result.Tags.Count);
        Assert.Contains(result.Tags, t => t.Name == "Google Analytics" && t.ContainerId == "GTM-1");
        Assert.True(result.Consent.ConsentModeV2Present);
        Assert.True(result.Consent.BeaconPresent);
        Assert.Equal(1, result.Consent.PagesWithConsent);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task Analytics_Coverage_ComputesPerContainerRatio()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, url) = await SeedAuditWithUrlAsync(host);
        await host.SeedUrlAsync(auditId, "https://example.com/other");
        await host.Get<IWriteRepository<AnalyticsDetectionDto>>().CreateAsync(
            new AnalyticsDetectionDto(0, auditId, url.Id, "Google Analytics", "GTM-1", "dom", null));

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/analytics/coverage");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<ContainerCoverageWire>>();
        var row = Assert.Single(items);
        Assert.Equal("GTM-1", row.ContainerId);
        Assert.Equal(2, row.IndexablePages);
        Assert.Equal(1, row.PagesWithContainer);
        Assert.Equal(0.5, row.Coverage);
    }

    [Fact]
    public async Task Analytics_InvalidView_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        var response = await host.Client.GetAsync($"/api/audits/{auditId}/analytics?view=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(response, "validation");
    }

    [Fact]
    public async Task Geo_LlmsTxt_ProjectsHostValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        host.Fakes.Geo.Result = new GeoReadinessResult(
            75,
            [new LlmsTxtValidation("example.com", true, false, ["https://example.com/broken"], ["Missing H1"])],
            []);

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/geo/llms-txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.ReadDataAsync<List<LlmsTxtValidationWire>>();
        var row = Assert.Single(items);
        Assert.Equal("example.com", row.Host);
        Assert.Equal("https://example.com/llms.txt", row.Url);
        Assert.True(row.Present);
        Assert.False(row.Valid);
        Assert.Contains("Missing H1", row.Issues);
        Assert.Contains(row.Issues, i => i.Contains("broken"));
    }

    [Fact]
    public async Task Geo_Governance_ProjectsRows_WithEvidenceAndFilter()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (auditId, _) = await SeedAuditWithUrlAsync(host);
        host.Fakes.Governance.Rows =
        [
            new GovernanceMatrixRowDto(0, auditId, "GPTBot", "/", AiAgentPurpose.Search, GovernanceVerdict.Allow,
                "{\"host\":\"example.com\",\"matchedRule\":{\"allow\":true,\"path\":\"/\"},\"directives\":[\"user-agent\",\"allow\"]}"),
            new GovernanceMatrixRowDto(0, auditId, "CCBot", "/", AiAgentPurpose.Training, GovernanceVerdict.Deny,
                "{\"host\":\"example.com\",\"matchedRule\":{\"allow\":false,\"path\":\"/\"}}"),
        ];

        var response = await host.Client.GetAsync($"/api/audits/{auditId}/geo/governance");
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 2);
        var items = await response.ReadDataAsync<List<BotGovernanceRowWire>>();
        var gpt = items.Single(r => r.BotName == "GPTBot");
        Assert.True(gpt.Allowed);
        Assert.True(gpt.RobotsTxtAllowed);
        Assert.Equal("example.com", gpt.Host);
        Assert.Null(gpt.LlmsTxtAllowed);
        Assert.Contains("user-agent", gpt.Notes!);
        var cc = items.Single(r => r.BotName == "CCBot");
        Assert.False(cc.Allowed);
        Assert.False(cc.RobotsTxtAllowed);

        var filtered = await (await host.Client.GetAsync($"/api/audits/{auditId}/geo/governance?botName=ccbot")).ReadDataAsync<List<BotGovernanceRowWire>>();
        Assert.Single(filtered);
    }

    [Fact]
    public async Task Geo_UnknownAudit_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var response = await host.Client.GetAsync("/api/audits/999/geo/governance");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
