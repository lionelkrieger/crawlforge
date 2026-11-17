using System.Net;
using System.Net.Http.Json;
using CrawlForge.Api.Controllers;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Xunit;

namespace CrawlForge.Api.Tests.Controllers;

/// <summary>
/// Route-by-route contract tests for scheduled audits: recurrence validation
/// against the locked local grammar, sidecar display metadata, enable/disable
/// next-due computation, run-now delegation, and run-history projection.
/// </summary>
public sealed class ScheduleRouteTests
{
    [Fact]
    public async Task Schedules_Create_PersistsNameCronAndActions()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();

        var response = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new
        {
            configurationId = configuration.Id.ToString(),
            name = "Nightly crawl",
            cron = "calendar:02:30",
            completionActions = new object[]
            {
                new { kind = "local-notification", parameters = new { title = "Done" } },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wire = await response.ReadDataAsync<ScheduleWire>();
        Assert.Equal("Nightly crawl", wire.Name);
        Assert.Equal(project.Id.ToString(), wire.ProjectId);
        Assert.Equal(configuration.Id.ToString(), wire.ConfigurationId);
        Assert.True(wire.Enabled);
        Assert.Equal("calendar:02:30", wire.Cron);
        Assert.NotNull(wire.NextDueAt);
        Assert.True(wire.NextDueAt > DateTimeOffset.UtcNow);
        Assert.Single(wire.CompletionActions);
        Assert.Equal("local-notification", wire.CompletionActions[0].Kind);
        Assert.Equal("Done", wire.CompletionActions[0].Parameters!["title"].GetString());
        Assert.True(wire.CreatedAt > DateTimeOffset.UnixEpoch);

        var fetched = await (await host.Client.GetAsync($"/api/schedules/{wire.Id}")).ReadDataAsync<ScheduleWire>();
        Assert.Equal("Nightly crawl", fetched.Name);
        Assert.Equal(wire.CreatedAt, fetched.CreatedAt);
    }

    [Fact]
    public async Task Schedules_Create_ValidatesProjectConfigurationCronAndActions()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();

        var unknownProject = await host.Client.PostAsJsonAsync("/api/projects/999/schedules", new { configurationId = configuration.Id.ToString(), name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, unknownProject.StatusCode);

        var unknownConfiguration = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new { configurationId = "999", name = "X" });
        Assert.Equal(HttpStatusCode.NotFound, unknownConfiguration.StatusCode);

        var missingName = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new { configurationId = configuration.Id.ToString(), name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);

        var badCron = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new { configurationId = configuration.Id.ToString(), name = "X", cron = "0 2 * * *" });
        Assert.Equal(HttpStatusCode.BadRequest, badCron.StatusCode);
        await ControllerTestHelpers.AssertErrorShape(badCron, "validation");

        var badAction = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new
        {
            configurationId = configuration.Id.ToString(),
            name = "X",
            completionActions = new object[] { new { kind = "export-delivery" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
    }

    [Fact]
    public async Task Schedules_ListByProject_AndGlobalList()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var (otherProject, otherConfiguration) = await host.SeedProjectAsync("Other");
        await CreateScheduleAsync(host, project.Id, configuration.Id, "One");
        await CreateScheduleAsync(host, otherProject.Id, otherConfiguration.Id, "Two");

        var scoped = await host.Client.GetAsync($"/api/projects/{project.Id}/schedules");
        using var document = await scoped.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(document.RootElement, 0, 50, 1);
        var items = await scoped.ReadDataAsync<List<ScheduleWire>>();
        Assert.Equal("One", items[0].Name);

        var global = await (await host.Client.GetAsync("/api/schedules")).ReadDataAsync<List<ScheduleWire>>();
        Assert.Equal(2, global.Count);
    }

    [Fact]
    public async Task Schedules_Update_MergesSubset_AndRecomputesNextDue()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var created = await CreateScheduleAsync(host, project.Id, configuration.Id, "Initial", cron: "interval:01:00:00");

        var updated = await (await host.Client.PutAsJsonAsync($"/api/schedules/{created.Id}", new { name = "Renamed", cron = "interval:02:00:00" }))
            .ReadDataAsync<ScheduleWire>();
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("interval:02:00:00", updated.Cron);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);

        var disabled = await (await host.Client.PutAsJsonAsync($"/api/schedules/{created.Id}", new { enabled = false }))
            .ReadDataAsync<ScheduleWire>();
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextDueAt);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync("/api/schedules/999", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task Schedules_EnableDisable_RunNow_AndRuns()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var created = await CreateScheduleAsync(host, project.Id, configuration.Id, "Manual", cron: "interval:00:30:00");

        var disabled = await (await host.Client.PostAsync($"/api/schedules/{created.Id}/disable", null)).ReadDataAsync<ScheduleStateResponse>();
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextDueAt);

        var enabled = await (await host.Client.PostAsync($"/api/schedules/{created.Id}/enable", null)).ReadDataAsync<ScheduleStateResponse>();
        Assert.True(enabled.Enabled);
        Assert.NotNull(enabled.NextDueAt);

        host.Fakes.Scheduler.NextAuditId = 42;
        var runNow = await (await host.Client.PostAsync($"/api/schedules/{created.Id}/run-now", null)).ReadDataAsync<ScheduleRunNowResponse>();
        Assert.Equal("42", runNow.AuditId);
        Assert.True(host.Fakes.Scheduler.RunNowCalls.TryDequeue(out var scheduledId));
        Assert.Equal(created.Id, scheduledId.ToString());

        host.Fakes.Scheduler.RunNowException = new InvalidOperationException("A run is already active.");
        var conflict = await host.Client.PostAsync($"/api/schedules/{created.Id}/run-now", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var audit = await host.SeedAuditAsync(project.Id, configuration.Id);
        await host.Get<IWriteRepository<ScheduleRunDto>>().CreateAsync(new ScheduleRunDto(
            0, long.Parse(created.Id), audit.Id, ScheduleTriggerType.Manual, ScheduleRunOutcome.Success, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await host.Get<IWriteRepository<ScheduleRunDto>>().CreateAsync(new ScheduleRunDto(
            0, long.Parse(created.Id), null, ScheduleTriggerType.CatchUp, ScheduleRunOutcome.Failed, DateTimeOffset.UtcNow.AddMinutes(-5), null));

        var runsResponse = await host.Client.GetAsync($"/api/schedules/{created.Id}/runs");
        using var runsDocument = await runsResponse.ReadJsonAsync();
        ControllerTestHelpers.AssertPagination(runsDocument.RootElement, 0, 50, 2);
        var runs = await runsResponse.ReadDataAsync<List<ScheduleRunWire>>();
        Assert.Equal("manual", runs[0].TriggerType);
        Assert.Equal("completed", runs[0].Outcome);
        Assert.Equal(audit.Id.ToString(), runs[0].AuditId);
        Assert.Equal("catch-up", runs[1].TriggerType);
        Assert.Equal("failed", runs[1].Outcome);
        Assert.Equal(string.Empty, runs[1].AuditId);
        Assert.Empty(runs[1].CompletionActionResults);

        var refreshed = await (await host.Client.GetAsync($"/api/schedules/{created.Id}")).ReadDataAsync<ScheduleWire>();
        Assert.NotNull(refreshed.LastRunAt);
    }

    [Fact]
    public async Task Schedules_Delete_RemovesHistoryAndMetadata()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var created = await CreateScheduleAsync(host, project.Id, configuration.Id, "Doomed");
        await host.Get<IWriteRepository<ScheduleRunDto>>().CreateAsync(new ScheduleRunDto(
            0, long.Parse(created.Id), null, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Skipped, DateTimeOffset.UtcNow, null));

        var response = await host.Client.DeleteAsync($"/api/schedules/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadDataAsync<DeletionConfirmationWire>()).Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/schedules/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/schedules/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Schedules_ExportDeliveryAction_RoundTripsTypedParameters()
    {
        await using var host = await ControllerTestHost.CreateAsync();
        var (project, configuration) = await host.SeedProjectAsync();
        var destination = await (await host.Client.PostAsJsonAsync("/api/destinations", new { name = "Drop", kind = "local-folder", target = Path.GetTempPath() }))
            .ReadDataAsync<DestinationWire>();

        var response = await host.Client.PostAsJsonAsync($"/api/projects/{project.Id}/schedules", new
        {
            configurationId = configuration.Id.ToString(),
            name = "With export",
            completionActions = new object[]
            {
                new { kind = "export-delivery", parameters = new { destinationId = destination.Id, format = "csv", datasets = new[] { "urls" } } },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wire = await response.ReadDataAsync<ScheduleWire>();
        var action = wire.CompletionActions[0];
        Assert.Equal("export-delivery", action.Kind);
        Assert.Equal(destination.Id, action.Parameters!["destinationId"].GetString());
        Assert.Equal("csv", action.Parameters["format"].GetString());
        Assert.Equal("urls", action.Parameters["datasets"][0].GetString());
    }

    private static async Task<ScheduleWire> CreateScheduleAsync(
        ControllerTestHost host,
        long projectId,
        long configurationId,
        string name,
        string? cron = null)
    {
        var response = await host.Client.PostAsJsonAsync($"/api/projects/{projectId}/schedules", new
        {
            configurationId = configurationId.ToString(),
            name,
            cron,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadDataAsync<ScheduleWire>();
    }
}
