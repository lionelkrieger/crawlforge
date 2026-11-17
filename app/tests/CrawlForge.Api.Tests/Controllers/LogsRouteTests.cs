using System.Net;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Intelligence.LogAnalyzer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for server-log import management and
/// correlation: import, list, delete, correlation view, and consented DNS
/// verification.
/// </summary>
public sealed class LogsRouteTests
{
    private static async Task<long> SeedProjectAsync(ControllerTestHost host)
    {
        var (project, _) = await host.SeedProjectAsync();
        return project.Id;
    }

    [Fact]
    public async Task Logs_Import_StartsImport_DerivesBaseUriAndCidrOnly()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var projectId = await SeedProjectAsync(host);

        var response = await host.Client.PostJsonAsync($"/api/projects/{projectId}/logs/import", new LogImportRequestWire("C:/logs/access.log", "combined"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<LogImportStartResponse>();
        Assert.Equal("completed", result.State);
        Assert.True(host.Fakes.LogImports.Requests.TryDequeue(out var request));
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal("https://example.com", request.BaseUri);
        Assert.Equal(LogFileFormat.ApacheNginxCombined, request.Format);
        Assert.Equal(BotVerificationMode.CidrOnly, request.VerificationMode);
    }

    [Fact]
    public async Task Logs_Import_RejectsInvalidFormatAndUnknownProject()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var projectId = await SeedProjectAsync(host);
        var badFormat = await host.Client.PostJsonAsync($"/api/projects/{projectId}/logs/import", new LogImportRequestWire("C:/logs/access.log", "bogus"));
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(badFormat, "validation");
        var unknown = await host.Client.PostJsonAsync("/api/projects/999/logs/import", new LogImportRequestWire("C:/logs/access.log", null));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Logs_List_ProjectsImportRecords()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var projectId = await SeedProjectAsync(host);
        await host.Client.PostJsonAsync($"/api/projects/{projectId}/logs/import", new LogImportRequestWire("C:/logs/access.log", "w3c"));

        var response = await host.Client.GetAsync($"/api/projects/{projectId}/logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await response.ReadDataAsync<List<LogImportWire>>();
        var row = Assert.Single(items);
        Assert.Equal(projectId.ToString(), row.ProjectId);
        Assert.Equal("access.log", row.FileName);
        Assert.Equal("w3c", row.Format);
        Assert.Equal("completed", row.State);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(10, row.Counters.LinesParsed);
        Assert.Equal(9, row.Counters.LinesAccepted);
        Assert.Equal(1, row.Counters.LinesRejected);
        Assert.False(row.DnsVerificationConsented);
    }

    [Fact]
    public async Task Logs_Delete_PurgesImport()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var projectId = await SeedProjectAsync(host);
        var start = await (await host.Client.PostJsonAsync($"/api/projects/{projectId}/logs/import", new LogImportRequestWire("C:/logs/access.log", null))).ReadDataAsync<LogImportStartResponse>();

        var response = await host.Client.DeleteAsync($"/api/logs/{start.ImportId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<DeletionConfirmationWire>();
        Assert.True(result.Deleted);
        Assert.True(host.Fakes.LogImports.Purged.TryDequeue(out var purged));
        Assert.Equal(long.Parse(start.ImportId), purged);

        var missing = await host.Client.DeleteAsync("/api/logs/999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Logs_Correlation_ReturnsSummaryAndRows()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var url = await host.SeedUrlAsync(audit.Id, "https://example.com/page");
        await host.Client.PostJsonAsync($"/api/projects/{project.Id}/logs/import", new LogImportRequestWire("C:/logs/access.log", null));
        host.Fakes.LogCorrelation.Page = new LogCorrelationPage(
            LogCorrelationView.Orphans,
            [new LogCorrelationRow("https://example.com/page", 10, 5, "googlebot", true, "{\"lastSeen\":\"2026-01-01T00:00:00Z\"}")],
            1,
            []);

        var response = await host.Client.GetAsync($"/api/audits/{audit.Id}/logs/correlation?view=orphans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadDataAsync<LogCorrelationResponseWire>();
        Assert.Equal(1, result.Summary.OrphanCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal("https://example.com/page", row.Url);
        Assert.Equal(url.Id.ToString(), row.UrlId);
        Assert.Equal(10, row.Hits);
        Assert.Equal(5, row.BotHits);
        Assert.NotNull(row.LastSeen);
    }

    [Fact]
    public async Task Logs_Correlation_WithoutImport_ReturnsEmpty()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var result = await (await host.Client.GetAsync($"/api/audits/{audit.Id}/logs/correlation")).ReadDataAsync<LogCorrelationResponseWire>();
        Assert.Equal(0, result.Summary.OrphanCount);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Logs_VerifyDns_RequiresConsent()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var projectId = await SeedProjectAsync(host);
        var start = await (await host.Client.PostJsonAsync($"/api/projects/{projectId}/logs/import", new LogImportRequestWire("C:/logs/access.log", null))).ReadDataAsync<LogImportStartResponse>();

        var refused = await (await host.Client.PostJsonAsync($"/api/logs/{start.ImportId}/verify-dns", new DnsVerifyRequest(false))).ReadDataAsync<DnsVerifyResponse>();
        Assert.False(refused.Started);
        Assert.Contains("consent", refused.Message!);

        var accepted = await (await host.Client.PostJsonAsync($"/api/logs/{start.ImportId}/verify-dns", new DnsVerifyRequest(true))).ReadDataAsync<DnsVerifyResponse>();
        Assert.True(accepted.Started);
        Assert.True(host.Fakes.DnsVerification.Calls.TryDequeue(out var call));
        Assert.True(call.Consent);

        var missing = await host.Client.PostJsonAsync("/api/logs/999/verify-dns", new DnsVerifyRequest(true));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
