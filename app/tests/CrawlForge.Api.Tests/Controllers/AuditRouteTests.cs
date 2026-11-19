using System.Net;
using System.Text.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for audit start/list/detail, lifecycle
/// transitions, reaudit, and comparison (including host-mapped alignment).
/// </summary>
public sealed class AuditRouteTests
{
    [Fact]
    public async Task Start_CreatesAudit_ReturnsQueuedState_AndFreezesSnapshot()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(configuration.Id.ToString(), null, null, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var start = await response.ReadDataAsync<AuditStartResponse>();
        Assert.Equal("queued", start.State);
        Assert.True(long.TryParse(start.AuditId, out var auditId));

        var call = Assert.Single(host.Fakes.AuditExecution.Starts);
        Assert.Equal(project.Id, call.ProjectId);
        Assert.Equal(configuration.Id, call.ConfigurationId);
        Assert.Equal(ScheduleTriggerType.Manual, call.Trigger);

        var audit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(auditId);
        Assert.NotNull(audit);
        using var snapshot = JsonDocument.Parse(audit!.ConfigurationSnapshotJson);
        // WP-09-11 (G-REAUDIT-SNAPSHOT alignment): the frozen snapshot stays in
        // the STORAGE DTO shape the engine deserializes as CrawlConfigurationDto
        // (numeric id) — the wire projection is applied at the API boundary, not
        // in storage. The previous wire-shape assertion encoded the defect.
        Assert.Equal(configuration.Id, snapshot.RootElement.GetProperty("id").GetInt64());
        // All categories enabled by default → enabledCategories absent.
        Assert.False(snapshot.RootElement.TryGetProperty("enabledCategories", out _));
    }

    [Fact]
    public async Task Start_WithDisabledCategory_WritesEnabledCategoriesConvention()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (_, configuration) = await host.SeedProjectAsync();
        var settings = await host.Get<IReadRepository<ApplicationSettingsDto>>().GetByIdAsync(1);
        await host.Get<IWriteRepository<ApplicationSettingsDto>>().UpdateAsync(settings! with
        {
            AuditTogglesJson = "{\"Indexability\":true,\"Content\":false,\"Linking\":true}",
        });

        var start = await (await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(configuration.Id.ToString(), null, null, null)))
            .ReadDataAsync<AuditStartResponse>();

        var audit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        using var snapshot = JsonDocument.Parse(audit!.ConfigurationSnapshotJson);
        var categories = snapshot.RootElement.GetProperty("enabledCategories");
        Assert.Equal(2, categories.GetArrayLength());
        Assert.Contains(categories.EnumerateArray(), c => c.GetString() == "Indexability");
        Assert.DoesNotContain(categories.EnumerateArray(), c => c.GetString() == "Content");
    }

    [Fact]
    public async Task Start_ListMode_SnapshotsUrlList()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (_, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(
            configuration.Id.ToString(),
            new CrawlLimitsWire(10, null, null, null),
            new[] { "https://example.com/a", "https://example.com/b" },
            true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var start = await response.ReadDataAsync<AuditStartResponse>();
        var audit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(start.AuditId));
        Assert.True(audit!.IsListMode);
        Assert.Contains("https://example.com/a", audit.UrlListJson);

        // WP-09-06 G10: list-mode starts go through the persist-before-start
        // port (the locked IAuditExecutionService start is the fallback path).
        var call = Assert.Single(host.Fakes.AuditExecution.ListStarts);
        Assert.Equal(10, call.Overrides?.MaxUrls);
    }

    [Fact]
    public async Task Start_ListModeWithoutUrls_ReturnsValidation()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (_, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest(configuration.Id.ToString(), null, null, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_UnknownConfiguration_ReturnsNotFound()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var response = await host.Client.PostJsonAsync("/api/audits", new AuditStartRequest("999", null, null, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(host.Fakes.AuditExecution.Starts);
    }

    [Fact]
    public async Task List_FiltersByProjectAndState_WithPagination()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Completed);
        await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);
        var (otherProject, otherConfiguration) = await host.SeedProjectAsync("Other");
        await host.SeedAuditAsync(otherProject.Id, otherConfiguration.Id);

        var response = await host.Client.GetAsync($"/api/audits?projectId={project.Id}&state=running");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = document.RootElement.GetProperty("data");
        Assert.Single(items.EnumerateArray());
        Assert.Equal("running", items[0].GetProperty("state").GetString());
        Assert.Equal("crawling", items[0].GetProperty("phase").GetString());

        var invalidState = await host.Client.GetAsync("/api/audits?state=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalidState.StatusCode);
    }

    [Fact]
    public async Task Detail_ReturnsDerivedProgressFields()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling,
            "{\"discovered\":100,\"fetched\":50,\"analyzed\":48,\"failed\":2,\"blocked\":3,\"wafBlocked\":1}");

        var response = await host.Client.GetAsync($"/api/audits/{audit.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.ReadDataAsync<AuditDetailWire>();
        // WP-09-06: producer keys map to the wire (discovered→queued, failed→error).
        Assert.Equal(100, detail.Counters.Queued);
        Assert.Equal(50, detail.Counters.Fetched);
        Assert.Equal(2, detail.Counters.Error);
        Assert.Equal(3, detail.Counters.Blocked);
        Assert.Equal(1, detail.Counters.WafBlocked);
        Assert.True(detail.ElapsedSeconds > 0);
        Assert.True(detail.ThroughputPerSecond > 0);
        Assert.NotNull(detail.EstimatedRemainderSeconds);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/audits/999")).StatusCode);
    }

    [Fact]
    public async Task Lifecycle_TransitionsAreStrict_AndTerminalStatesImmutable()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var audit = await host.SeedAuditAsync(project.Id, configuration.Id, AuditStatus.Running, AuditPhase.Crawling);

        var paused = await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("paused", (await paused.ReadDataAsync<AuditStateResponse>()).State);

        var doublePause = await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null);
        Assert.Equal(HttpStatusCode.Conflict, doublePause.StatusCode);
        using var conflict = await doublePause.ReadJsonAsync();
        ControllerTestHelpers.AssertErrorShape(conflict.RootElement, "invalid-state");

        var resumed = await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null);
        Assert.Equal("running", (await resumed.ReadDataAsync<AuditStateResponse>()).State);

        var cancelled = await host.Client.PostAsync($"/api/audits/{audit.Id}/cancel", content: null);
        Assert.Equal("cancelled", (await cancelled.ReadDataAsync<AuditStateResponse>()).State);

        // Terminal state: every transition is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/pause", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/resume", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{audit.Id}/cancel", content: null)).StatusCode);

        // Completed audits cannot be paused either.
        var completed = await host.SeedAuditAsync(project.Id, configuration.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync($"/api/audits/{completed.Id}/pause", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/api/audits/999/pause", content: null)).StatusCode);
    }

    [Fact]
    public async Task Reaudit_LinksNewAuditToSource()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var source = await host.SeedAuditAsync(project.Id, configuration.Id);

        var response = await host.Client.PostJsonAsync($"/api/audits/{source.Id}/reaudit", new ReauditRequest(true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reaudit = await response.ReadDataAsync<ReauditResponse>();
        Assert.Equal(source.Id.ToString(), reaudit.SourceAuditId);
        Assert.NotEqual(source.Id.ToString(), reaudit.AuditId);
        Assert.Equal("queued", reaudit.State);

        var newAudit = await host.Get<IReadRepository<AuditDto>>().GetByIdAsync(long.Parse(reaudit.AuditId));
        using var snapshot = JsonDocument.Parse(newAudit!.ConfigurationSnapshotJson);
        Assert.Equal(source.Id, snapshot.RootElement.GetProperty("reaudit").GetProperty("sourceAuditId").GetInt64());
        Assert.True(snapshot.RootElement.GetProperty("reaudit").GetProperty("refresh").GetBoolean());
    }

    [Fact]
    public async Task Compare_ReturnsNewResolvedFindings_AndMetricDeltas()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var baseAudit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var targetAudit = await host.SeedAuditAsync(project.Id, configuration.Id);

        var baseUrl = await host.SeedUrlAsync(baseAudit.Id, "https://example.com/kept");
        var targetUrl = await host.SeedUrlAsync(targetAudit.Id, "https://example.com/kept");
        var newUrl = await host.SeedUrlAsync(targetAudit.Id, "https://example.com/new");

        await host.SeedFindingAsync(baseAudit.Id, "title.missing", urlId: baseUrl.Id);
        await host.SeedFindingAsync(targetAudit.Id, "title.missing", urlId: targetUrl.Id);
        await host.SeedFindingAsync(baseAudit.Id, "meta.description", urlId: baseUrl.Id);
        await host.SeedFindingAsync(targetAudit.Id, "h1.missing", urlId: newUrl.Id);

        var metrics = host.Get<IWriteRepository<MetricSummaryDto>>();
        await metrics.CreateAsync(new MetricSummaryDto(0, baseAudit.Id, "serp-truncation", "{}", 10));
        await metrics.CreateAsync(new MetricSummaryDto(0, targetAudit.Id, "serp-truncation", "{}", 4));

        var response = await host.Client.GetAsync($"/api/audits/compare?auditIdA={baseAudit.Id}&auditIdB={targetAudit.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comparison = await response.ReadDataAsync<AuditCompareResponse>();
        Assert.Single(comparison.NewFindings);
        Assert.Equal("h1.missing", comparison.NewFindings[0].RuleKey);
        Assert.Single(comparison.ResolvedFindings);
        Assert.Equal("meta.description", comparison.ResolvedFindings[0].RuleKey);
        var delta = Assert.Single(comparison.MetricDeltas);
        Assert.Equal("serp-truncation", delta.Metric);
        Assert.Equal(10, delta.Before);
        Assert.Equal(4, delta.After);
        Assert.Equal(-6, delta.Delta);
    }

    [Fact]
    public async Task Compare_HostMapping_AlignsStagingAndProductionByPath()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        await host.Get<IWriteRepository<ProjectDto>>().UpdateAsync(project with
        {
            HostMappingJson = "[{\"fromHost\":\"staging.example.com\",\"toHost\":\"www.example.com\"}]",
        });

        var baseAudit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var targetAudit = await host.SeedAuditAsync(project.Id, configuration.Id);
        var stagingUrl = await host.SeedUrlAsync(baseAudit.Id, "https://staging.example.com/page");
        var productionUrl = await host.SeedUrlAsync(targetAudit.Id, "https://www.example.com/page");
        var productionNewUrl = await host.SeedUrlAsync(targetAudit.Id, "https://www.example.com/other");

        await host.SeedFindingAsync(baseAudit.Id, "title.missing", urlId: stagingUrl.Id);
        await host.SeedFindingAsync(targetAudit.Id, "title.missing", urlId: productionUrl.Id);
        await host.SeedFindingAsync(targetAudit.Id, "title.missing", urlId: productionNewUrl.Id);

        // Without host mapping the staging/production URLs differ → appears new.
        var plain = await (await host.Client.GetAsync($"/api/audits/compare?auditIdA={baseAudit.Id}&auditIdB={targetAudit.Id}"))
            .ReadDataAsync<AuditCompareResponse>();
        Assert.Equal(2, plain.NewFindings.Count);

        // With host mapping the same path aligns → only /other is new.
        var mapped = await (await host.Client.GetAsync($"/api/audits/compare?auditIdA={baseAudit.Id}&auditIdB={targetAudit.Id}&hostMapping=true"))
            .ReadDataAsync<AuditCompareResponse>();
        Assert.Single(mapped.NewFindings);
        Assert.Empty(mapped.ResolvedFindings);
    }

    [Fact]
    public async Task Compare_MissingOrUnknownAudits_ReturnsErrors()
    {
        await using var host = await ControllerTestHost.CreateAsync();

        var missing = await host.Client.GetAsync("/api/audits/compare");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var unknown = await host.Client.GetAsync("/api/audits/compare?auditIdA=1&auditIdB=2");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
