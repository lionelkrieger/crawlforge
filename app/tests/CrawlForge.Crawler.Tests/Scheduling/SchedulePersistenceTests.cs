using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>
/// Criterion #11 evidence: schedule_definitions persistence on the real stack
/// (SqliteDbProvider + SchemaMigrator + repositories), including limit overrides,
/// completion actions, and next-due timestamps surviving a backend restart.
/// </summary>
public sealed class SchedulePersistenceTests
{
    private static readonly DateTimeOffset NextDue = new(2026, 11, 15, 7, 30, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Schedule definition round-trips with recurrence, overrides, actions, and next-due")]
    public async Task ScheduleDefinitionRoundTrips()
    {
        using var database = new SchedulingDatabase();
        await using var stack = await SchedulingStack.CreateAsync(database);

        var actions = ScheduleCompletionActions.Serialize(
        [
            new ScheduleCompletionAction(CompletionActionKinds.LocalNotification, Title: "Audit done"),
            new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: 1, Format: ExportFormat.Csv, Datasets: ["findings"]),
        ]);

        var definition = new ScheduleDefinitionDto(
            0,
            stack.ProjectId,
            stack.ConfigurationId,
            "calendar:07:30;days=Mon,Wed,Fri",
            true,
            new CrawlLimitOverrides(MaxUrls: 500, MaxDepth: 3, RequestsPerSecond: 1.5),
            actions,
            NextDue);

        var created = await stack.Schedules.CreateAsync(definition);
        Assert.True(created.Id > 0);

        var readBack = await stack.Schedules.GetByIdAsync(created.Id);

        Assert.NotNull(readBack);
        Assert.Equal(created.Id, readBack.Id);
        Assert.Equal(stack.ProjectId, readBack.ProjectId);
        Assert.Equal(stack.ConfigurationId, readBack.ConfigurationId);
        Assert.Equal("calendar:07:30;days=Mon,Wed,Fri", readBack.RecurrenceRule);
        Assert.True(readBack.IsEnabled);
        Assert.Equal(new CrawlLimitOverrides(MaxUrls: 500, MaxDepth: 3, RequestsPerSecond: 1.5), readBack.LimitOverrides);
        Assert.Equal(actions, readBack.CompletionActionsJson);
        Assert.Equal(NextDue, readBack.NextDueAt);
    }

    [Fact(DisplayName = "Next-due timestamp survives a fresh-provider restart (criterion #11)")]
    public async Task NextDueSurvivesProviderRestart()
    {
        using var database = new SchedulingDatabase();
        long scheduleId;

        await using (var stack = await SchedulingStack.CreateAsync(database))
        {
            var created = await stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
                0, stack.ProjectId, stack.ConfigurationId, "interval:06:00:00", true, null, null, NextDue));
            scheduleId = created.Id;
        }

        // Simulated backend restart: a brand-new provider over the same database file.
        using (var restarted = database.CreateProvider())
        {
            var schedules = new SqlRepository<ScheduleDefinitionDto>(restarted);
            var readBack = await schedules.GetByIdAsync(scheduleId);

            Assert.NotNull(readBack);
            Assert.Equal(NextDue, readBack.NextDueAt);
            Assert.True(readBack.IsEnabled);

            var advanced = NextDue.AddHours(6);
            await schedules.UpdateAsync(readBack with { NextDueAt = advanced });
        }

        // Second restart proves the persisted advance, not an in-memory artifact.
        using (var restartedAgain = database.CreateProvider())
        {
            var schedules = new SqlRepository<ScheduleDefinitionDto>(restartedAgain);
            var readBack = await schedules.GetByIdAsync(scheduleId);

            Assert.NotNull(readBack);
            Assert.Equal(NextDue.AddHours(6), readBack.NextDueAt);
        }
    }

    [Fact(DisplayName = "Null limit overrides and completion actions round-trip as null")]
    public async Task NullOptionalFieldsRoundTrip()
    {
        using var database = new SchedulingDatabase();
        await using var stack = await SchedulingStack.CreateAsync(database);

        var created = await stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, stack.ProjectId, stack.ConfigurationId, "interval:00:30:00", false, null, null, null));

        var readBack = await stack.Schedules.GetByIdAsync(created.Id);

        Assert.NotNull(readBack);
        Assert.Null(readBack.LimitOverrides);
        Assert.Null(readBack.CompletionActionsJson);
        Assert.Null(readBack.NextDueAt);
        Assert.False(readBack.IsEnabled);
    }

    [Fact(DisplayName = "Enabled flag updates persist")]
    public async Task EnabledFlagUpdatesPersist()
    {
        using var database = new SchedulingDatabase();
        await using var stack = await SchedulingStack.CreateAsync(database);

        var created = await stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, stack.ProjectId, stack.ConfigurationId, "interval:01:00:00", true, null, null, NextDue));

        await stack.Schedules.UpdateAsync(created with { IsEnabled = false, NextDueAt = null });
        var disabled = await stack.Schedules.GetByIdAsync(created.Id);
        Assert.NotNull(disabled);
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.NextDueAt);

        var recomputed = NextDue.AddHours(1);
        await stack.Schedules.UpdateAsync(disabled with { IsEnabled = true, NextDueAt = recomputed });
        var enabled = await stack.Schedules.GetByIdAsync(created.Id);
        Assert.NotNull(enabled);
        Assert.True(enabled.IsEnabled);
        Assert.Equal(recomputed, enabled.NextDueAt);
    }

    [Fact(DisplayName = "Schedule run history rows round-trip with audit reference and timestamps")]
    public async Task ScheduleRunHistoryRoundTrips()
    {
        using var database = new SchedulingDatabase();
        await using var stack = await SchedulingStack.CreateAsync(database);

        var schedule = await stack.Schedules.CreateAsync(new ScheduleDefinitionDto(
            0, stack.ProjectId, stack.ConfigurationId, "interval:01:00:00", true, null, null, NextDue));
        var audit = await stack.CreateAuditAsync();

        var started = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        var finished = started.AddMinutes(12);

        var success = await stack.Runs.CreateAsync(new ScheduleRunDto(0, schedule.Id, audit.Id, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Success, started, finished));
        var failed = await stack.Runs.CreateAsync(new ScheduleRunDto(0, schedule.Id, null, ScheduleTriggerType.Manual, ScheduleRunOutcome.Failed, started.AddHours(1), started.AddHours(1)));
        var skipped = await stack.Runs.CreateAsync(new ScheduleRunDto(0, schedule.Id, null, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Skipped, started.AddHours(2), started.AddHours(2)));

        var readSuccess = await stack.Runs.GetByIdAsync(success.Id);
        var readFailed = await stack.Runs.GetByIdAsync(failed.Id);
        var readSkipped = await stack.Runs.GetByIdAsync(skipped.Id);

        Assert.NotNull(readSuccess);
        Assert.Equal(schedule.Id, readSuccess.ScheduleDefinitionId);
        Assert.Equal(audit.Id, readSuccess.AuditId);
        Assert.Equal(ScheduleTriggerType.Scheduled, readSuccess.TriggerType);
        Assert.Equal(ScheduleRunOutcome.Success, readSuccess.Outcome);
        Assert.Equal(started, readSuccess.StartedAt);
        Assert.Equal(finished, readSuccess.FinishedAt);

        Assert.NotNull(readFailed);
        Assert.Null(readFailed.AuditId);
        Assert.Equal(ScheduleTriggerType.Manual, readFailed.TriggerType);
        Assert.Equal(ScheduleRunOutcome.Failed, readFailed.Outcome);

        Assert.NotNull(readSkipped);
        Assert.Equal(ScheduleRunOutcome.Skipped, readSkipped.Outcome);
    }
}
