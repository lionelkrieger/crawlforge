using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// WP-09-06 (G06): REST counter-key conformance at the route boundary.
/// Producer: the engine's stored counters_json shape
/// (AuditRunCounters.ToPayload → discovered/fetched/analyzed/failed, plus the
/// additive blocked/wafBlocked the emission recipe supplies). Consumer: the
/// frozen AuditCounters wire (queued/fetched/error/blocked/wafBlocked) served
/// by GET /api/audits/{id} and the audit list. These tests seed the REAL
/// producer keys through the real SQLite stack and read the REAL REST wire —
/// the same reconciliation the KnownGap conformance test locks.
/// </summary>
public sealed class AuditCounterWireRouteTests
{
    [Fact]
    public async Task Detail_ProducerKeys_MapToWireKeys()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"discovered\":120,\"fetched\":100,\"analyzed\":95,\"failed\":4,\"blocked\":6,\"wafBlocked\":3}");

        var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}")).ReadDataAsync<AuditDetailWire>();

        Assert.Equal(120, detail.Counters.Queued);    // discovered → queued
        Assert.Equal(100, detail.Counters.Fetched);   // fetched → fetched
        Assert.Equal(4, detail.Counters.Error);       // failed → error
        Assert.Equal(6, detail.Counters.Blocked);     // additive passthrough
        Assert.Equal(3, detail.Counters.WafBlocked);  // additive passthrough
    }

    [Fact]
    public async Task Detail_ProducerKeysWithoutBlocked_WireDefaultsToZero()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        // Exact AuditRunCounters.ToPayload() key set — no blocked/wafBlocked yet.
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"discovered\":10,\"fetched\":9,\"analyzed\":9,\"failed\":1}");

        var response = await host.Client.GetAsync($"/api/audits/{audit.Id}");
        using var document = await response.ReadJsonAsync();
        var counters = document.RootElement.GetProperty("data").GetProperty("counters");

        Assert.Equal(10, counters.GetProperty("queued").GetInt64());
        Assert.Equal(9, counters.GetProperty("fetched").GetInt64());
        Assert.Equal(1, counters.GetProperty("error").GetInt64());
        Assert.Equal(0, counters.GetProperty("blocked").GetInt64());
        foreach (var key in new[] { "queued", "fetched", "error", "blocked" })
        {
            Assert.Equal(JsonValueKind.Number, counters.GetProperty(key).ValueKind);
        }
    }

    [Fact]
    public async Task Detail_LegacyWireKeys_StillParse()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"queued\":50,\"fetched\":40,\"error\":3,\"blocked\":2,\"wafBlocked\":1}");

        var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}")).ReadDataAsync<AuditDetailWire>();

        Assert.Equal(50, detail.Counters.Queued);
        Assert.Equal(40, detail.Counters.Fetched);
        Assert.Equal(3, detail.Counters.Error);
        Assert.Equal(2, detail.Counters.Blocked);
        Assert.Equal(1, detail.Counters.WafBlocked);
    }

    [Fact]
    public async Task Detail_ProducerKeysWinOverLegacyKeys_WhenBothPresent()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"discovered\":80,\"queued\":1,\"fetched\":70,\"failed\":5,\"error\":99}");

        var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}")).ReadDataAsync<AuditDetailWire>();

        Assert.Equal(80, detail.Counters.Queued);
        Assert.Equal(5, detail.Counters.Error);
    }

    [Fact]
    public async Task Detail_EmptyOrMalformedCounters_DegradeToZero()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var empty = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished, "{}");
        var malformed = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished, "{not json");

        foreach (var audit in new[] { empty, malformed })
        {
            var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}")).ReadDataAsync<AuditDetailWire>();
            Assert.Equal(0, detail.Counters.Queued);
            Assert.Equal(0, detail.Counters.Fetched);
            Assert.Equal(0, detail.Counters.Error);
            Assert.Equal(0, detail.Counters.Blocked);
            Assert.Null(detail.Counters.WafBlocked);
        }
    }

    [Fact]
    public async Task List_SummariesCarryMappedCounters()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"discovered\":30,\"fetched\":28,\"analyzed\":28,\"failed\":2}");

        var response = await host.Client.GetAsync($"/api/audits?projectId={project.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        var counters = document.RootElement.GetProperty("data")[0].GetProperty("counters");
        Assert.Equal(30, counters.GetProperty("queued").GetInt64());
        Assert.Equal(28, counters.GetProperty("fetched").GetInt64());
        Assert.Equal(2, counters.GetProperty("error").GetInt64());
    }

    [Fact]
    public async Task Detail_NonNumericCounterValues_DegradeToZero()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed, AuditPhase.Finished,
            "{\"discovered\":\"lots\",\"fetched\":5,\"failed\":null}");

        var detail = await (await host.Client.GetAsync($"/api/audits/{audit.Id}")).ReadDataAsync<AuditDetailWire>();

        Assert.Equal(0, detail.Counters.Queued);
        Assert.Equal(5, detail.Counters.Fetched);
        Assert.Equal(0, detail.Counters.Error);
    }
}
