using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-06-01 reopen defect D2 (criterion #15 REST facet): the REST reaudit
/// action routes through the reaudit port — refresh:false takes the offline
/// analysis-only path (no crawl start), refresh:true re-crawls. Mirrors the
/// MCP lifecycle wiring.
/// </summary>
public sealed class AuditsReauditRoutingTests
{
    [Fact]
    public async Task Reaudit_RefreshFalse_RoutesOffline_NoCrawlStart()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var source = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.PostJsonAsync($"/api/audits/{source.Id}/reaudit", new ReauditRequest(false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reaudit = await response.ReadDataAsync<ReauditResponse>();
        Assert.Equal(source.Id.ToString(), reaudit.SourceAuditId);

        // The offline path was taken: the reaudit port saw refresh:false and
        // NO crawl start was issued.
        var call = Assert.Single(host.Fakes.AuditExecution.ReAudits);
        Assert.Equal(source.Id, call.AuditId);
        Assert.False(call.Refresh);
        Assert.Empty(host.Fakes.AuditExecution.Starts);

        // The new run is an analysis-only pass over stored data.
        var newAudit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(reaudit.AuditId));
        Assert.NotNull(newAudit);
        Assert.Equal(AuditPhase.Analyzing, newAudit!.Phase);
        using var snapshot = JsonDocument.Parse(newAudit.ConfigurationSnapshotJson);
        Assert.False(snapshot.RootElement.GetProperty("reaudit").GetProperty("refresh").GetBoolean());
    }

    [Fact]
    public async Task Reaudit_DefaultBody_RoutesOffline()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var source = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.PostAsync($"/api/audits/{source.Id}/reaudit", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var call = Assert.Single(host.Fakes.AuditExecution.ReAudits);
        Assert.False(call.Refresh);
        Assert.Empty(host.Fakes.AuditExecution.Starts);
    }

    [Fact]
    public async Task Reaudit_RefreshTrue_Recrawls()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var source = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.PostJsonAsync($"/api/audits/{source.Id}/reaudit", new ReauditRequest(true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var call = Assert.Single(host.Fakes.AuditExecution.ReAudits);
        Assert.True(call.Refresh);
        // The refresh path starts a full crawl run.
        var start = Assert.Single(host.Fakes.AuditExecution.Starts);
        Assert.Equal(project.Id, start.ProjectId);
        Assert.Equal(configuration.Id, start.ConfigurationId);
    }
}
