using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Scheduling;
using CrawlForge.Data.ContentStore;
using Xunit;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>
/// Criterion #12 evidence and §6.2 execution rules: injectable-clock catch-up
/// (exactly once, recorded as catch-up), no-overlap under concurrent due ticks and
/// across restarts, disabled schedules, global concurrency queueing, manual run-now,
/// completion actions, typed emissions, and retention pruning. No real-time waiting:
/// every temporal scenario advances the fake clock explicitly.
/// </summary>
public sealed class ScheduledAuditServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact(DisplayName = "Machine-off gap fires exactly once, recorded as catch-up (criterion #12)")]
    public async Task MachineOffGapFiresExactlyOnceAsCatchUp()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-2));

        await harness.Service.TickAsync();

        Assert.Single(harness.Execution.Starts);
        Assert.True(harness.Execution.Starts.TryPeek(out var start));
        Assert.Equal(ScheduleTriggerType.CatchUp, start.Trigger);

        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        var run = runs.Items[0];
        Assert.Equal(ScheduleTriggerType.CatchUp, run.TriggerType);
        Assert.Equal(ScheduleRunOutcome.Success, run.Outcome);
        Assert.NotNull(run.AuditId);

        // Next due is recomputed strictly after the firing: no backlog replay.
        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.Equal(harness.Clock.UtcNow.AddHours(1), updated.NextDueAt);

        await harness.CompleteAuditAsync(run.AuditId!.Value);
        await harness.Hub.WaitForEventAsync(ScheduleHubEventNames.ScheduleRunCompleted);

        var finished = await harness.Stack.Runs.GetByIdAsync(run.Id);
        Assert.NotNull(finished);
        Assert.NotNull(finished.FinishedAt);

        // Repeated ticks at the same instant never re-fire the missed window.
        await harness.Service.TickAsync();
        await harness.Service.TickAsync();
        Assert.Single(harness.Execution.Starts);

        // The next regular due time fires as a normal scheduled run.
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.Service.TickAsync();
        Assert.Equal(2, harness.Execution.Starts.Count);
        Assert.Equal(ScheduleTriggerType.Scheduled, harness.Execution.Starts.Last().Trigger);

        var allRuns = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, allRuns.Items.Count(r => r.TriggerType == ScheduleTriggerType.CatchUp));
    }

    [Fact(DisplayName = "Many missed windows collapse into a single catch-up run")]
    public async Task ManyMissedWindowsCollapseIntoOneCatchUp()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        // Seven hourly windows missed while the machine was off.
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-7));

        await harness.Service.TickAsync();
        await harness.Service.TickAsync();

        Assert.Single(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        Assert.Equal(ScheduleTriggerType.CatchUp, runs.Items[0].TriggerType);
    }

    [Fact(DisplayName = "Concurrent due ticks never double-fire the same schedule")]
    public async Task ConcurrentDueTicksNeverDoubleFire()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1));

        await Task.WhenAll(harness.Service.TickAsync(), harness.Service.TickAsync(), harness.Service.TickAsync());

        Assert.Single(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
    }

    [Fact(DisplayName = "A due time arriving during an active run is recorded as skipped-overlap")]
    public async Task DueWhileActiveRecordsSkippedOverlap()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(rule: "interval:00:01:00", nextDue: harness.Clock.UtcNow.AddMinutes(-1));

        await harness.Service.TickAsync();
        Assert.Single(harness.Execution.Starts);

        // The audit stays active; the next due time arrives two minutes later.
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Service.TickAsync();

        Assert.Single(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(2, runs.TotalCount);
        var skipped = runs.Items.Single(r => r.Outcome == ScheduleRunOutcome.Skipped);
        Assert.Null(skipped.AuditId);
        Assert.NotNull(skipped.FinishedAt);

        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.True(updated.NextDueAt > harness.Clock.UtcNow);
    }

    [Fact(DisplayName = "No-overlap guard survives a service restart (reconciled unfinished run)")]
    public async Task NoOverlapSurvivesServiceRestart()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1));

        await harness.Service.TickAsync();
        Assert.Single(harness.Execution.Starts);

        // Keep the next due in the future across the restart so the restarted loop's
        // first tick is inert, then make it due while the reconciled run is active.
        await harness.Stack.Schedules.UpdateAsync(schedule with { NextDueAt = harness.Clock.UtcNow.AddHours(1) });

        using var restarted = harness.CreateService();
        await restarted.StartAsync();
        try
        {
            await harness.Stack.Schedules.UpdateAsync(schedule with { NextDueAt = harness.Clock.UtcNow.AddMinutes(-1) });
            await restarted.TickAsync();

            Assert.Single(harness.Execution.Starts);
            var runs = await harness.Stack.Runs.ListAsync(new());
            Assert.Equal(2, runs.TotalCount);
            Assert.Contains(runs.Items, r => r.Outcome == ScheduleRunOutcome.Skipped);
        }
        finally
        {
            await restarted.StopAsync();
        }
    }

    [Fact(DisplayName = "Disabled schedules never fire")]
    public async Task DisabledSchedulesNeverFire()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-1), enabled: false);

        await harness.Service.TickAsync();

        Assert.Empty(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(0, runs.TotalCount);
    }

    [Fact(DisplayName = "Re-enabled schedule without next-due recomputes it on the next tick")]
    public async Task ReEnableRecomputesNextDue()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(rule: "calendar:07:30", nextDue: null, enabled: false);
        await harness.Stack.Schedules.UpdateAsync(schedule with { IsEnabled = true });

        await harness.Service.TickAsync();

        Assert.Empty(harness.Execution.Starts);
        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.NotNull(updated.NextDueAt);
        Assert.True(updated.NextDueAt > harness.Clock.UtcNow);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 7, 30, 0, TimeSpan.Zero), updated.NextDueAt);
    }

    [Fact(DisplayName = "Excess due runs queue in due-time order under the global concurrency cap")]
    public async Task ExcessDueRunsQueueInDueOrder()
    {
        var options = new SchedulingOptions
        {
            TickInterval = TimeSpan.FromMinutes(30),
            AuditPollInterval = TimeSpan.FromMilliseconds(1),
            MaxConcurrentAudits = 1,
        };
        await using var harness = await SchedulerHarness.CreateAsync(options: options);
        var first = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-2));
        var second = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-1));

        await harness.Service.TickAsync();

        Assert.Single(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        Assert.Equal(first.Id, runs.Items[0].ScheduleDefinitionId);

        // The second schedule remains due until capacity frees up.
        var pending = await harness.Stack.Schedules.GetByIdAsync(second.Id);
        Assert.NotNull(pending);
        Assert.True(pending.NextDueAt <= harness.Clock.UtcNow);

        await harness.CompleteAuditAsync(runs.Items[0].AuditId!.Value);
        await harness.Hub.WaitForEventAsync(ScheduleHubEventNames.ScheduleRunCompleted);

        await harness.Service.TickAsync();
        Assert.Equal(2, harness.Execution.Starts.Count);
        var allRuns = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(2, allRuns.TotalCount);
        Assert.Contains(allRuns.Items, r => r.ScheduleDefinitionId == second.Id);
    }

    [Fact(DisplayName = "Run-now fires a manual run with limit overrides and preserves the cadence")]
    public async Task RunNowFiresManualRunWithOverrides()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var nextDue = harness.Clock.UtcNow.AddHours(2);
        var schedule = await harness.CreateScheduleAsync(nextDue: nextDue, overrides: new CrawlLimitOverrides(MaxUrls: 42));

        var auditId = await harness.Service.RunNowAsync(schedule.Id);

        Assert.True(auditId > 0);
        Assert.True(harness.Execution.Starts.TryPeek(out var start));
        Assert.Equal(ScheduleTriggerType.Manual, start.Trigger);
        Assert.Equal(42, start.Overrides?.MaxUrls);

        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        Assert.Equal(ScheduleTriggerType.Manual, runs.Items[0].TriggerType);

        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.Equal(nextDue, updated.NextDueAt);
    }

    [Fact(DisplayName = "Run-now during an active run records skipped-overlap and throws")]
    public async Task RunNowDuringActiveRunRecordsSkipAndThrows()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1));

        await harness.Service.TickAsync();
        Assert.Single(harness.Execution.Starts);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.RunNowAsync(schedule.Id));

        Assert.Single(harness.Execution.Starts);
        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(2, runs.TotalCount);
        var skipped = runs.Items.Single(r => r.Outcome == ScheduleRunOutcome.Skipped);
        Assert.Equal(ScheduleTriggerType.Manual, skipped.TriggerType);
    }

    [Fact(DisplayName = "Audit-start failure records a failed run and advances the due time")]
    public async Task StartFailureRecordsFailedRunAndAdvancesDue()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        harness.Execution.StartException = new InvalidOperationException("engine unavailable");
        var schedule = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1));

        await harness.Service.TickAsync();

        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        Assert.Equal(ScheduleRunOutcome.Failed, runs.Items[0].Outcome);
        Assert.Null(runs.Items[0].AuditId);

        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.True(updated.NextDueAt > harness.Clock.UtcNow);

        await harness.Service.TickAsync();
        var after = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, after.TotalCount);

        var completed = harness.Hub.EventsNamed(ScheduleHubEventNames.ScheduleRunCompleted).Single();
        var payload = JsonSerializer.Deserialize<ScheduleRunCompletedPayload>(completed.PayloadJson, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("failed", payload.Outcome);
        Assert.Contains("engine unavailable", payload.FailureReason);
    }

    [Fact(DisplayName = "Completion actions execute only after successful audit completion")]
    public async Task CompletionActionsExecuteOnSuccessfulCompletion()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var actionsJson = ScheduleCompletionActions.Serialize(
        [
            new ScheduleCompletionAction(CompletionActionKinds.LocalNotification, Title: "Done"),
            new ScheduleCompletionAction(CompletionActionKinds.ExportDelivery, DestinationId: 5, Format: ExportFormat.Csv),
        ]);
        harness.Completion.Outcomes = [new CompletionActionOutcome(CompletionActionKinds.LocalNotification, true, null)];
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1), completionActionsJson: actionsJson);

        await harness.Service.TickAsync();
        var run = (await harness.Stack.Runs.ListAsync(new())).Items[0];
        await harness.CompleteAuditAsync(run.AuditId!.Value);
        await harness.Hub.WaitForEventAsync(ScheduleHubEventNames.ScheduleRunCompleted);

        Assert.Single(harness.Completion.Calls);
        Assert.True(harness.Completion.Calls.TryPeek(out var call));
        Assert.Equal(2, call.Actions.Count);

        var completed = harness.Hub.EventsNamed(ScheduleHubEventNames.ScheduleRunCompleted).Single();
        var payload = JsonSerializer.Deserialize<ScheduleRunCompletedPayload>(completed.PayloadJson, JsonOptions);
        Assert.NotNull(payload);
        Assert.Single(payload.CompletionActions);
        Assert.True(payload.CompletionActions[0].Succeeded);
    }

    [Fact(DisplayName = "Completion actions do not run when the audit fails")]
    public async Task CompletionActionsSkippedWhenAuditFails()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var actionsJson = ScheduleCompletionActions.Serialize([new ScheduleCompletionAction(CompletionActionKinds.LocalNotification)]);
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddMinutes(-1), completionActionsJson: actionsJson);

        await harness.Service.TickAsync();
        var run = (await harness.Stack.Runs.ListAsync(new())).Items[0];
        await harness.CompleteAuditAsync(run.AuditId!.Value, AuditStatus.Failed);
        await harness.Hub.WaitForEventAsync(ScheduleHubEventNames.ScheduleRunCompleted);

        Assert.Empty(harness.Completion.Calls);
        var completed = harness.Hub.EventsNamed(ScheduleHubEventNames.ScheduleRunCompleted).Single();
        var payload = JsonSerializer.Deserialize<ScheduleRunCompletedPayload>(completed.PayloadJson, JsonOptions);
        Assert.NotNull(payload);
        Assert.NotNull(payload.FailureReason);
        Assert.Empty(payload.CompletionActions);
    }

    [Fact(DisplayName = "Hub emissions carry the locked typed wire values")]
    public async Task EmissionsCarryTypedWireValues()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-2));

        await harness.Service.TickAsync();

        var fired = harness.Hub.EventsNamed(ScheduleHubEventNames.ScheduleFired).Single();
        var firedPayload = JsonSerializer.Deserialize<ScheduleFiredPayload>(fired.PayloadJson, JsonOptions);
        Assert.NotNull(firedPayload);
        Assert.Equal("catch-up", firedPayload.TriggerType);
        Assert.NotNull(firedPayload.AuditId);

        var run = (await harness.Stack.Runs.ListAsync(new())).Items[0];
        await harness.CompleteAuditAsync(run.AuditId!.Value);
        await harness.Hub.WaitForEventAsync(ScheduleHubEventNames.ScheduleRunCompleted);

        var completed = harness.Hub.EventsNamed(ScheduleHubEventNames.ScheduleRunCompleted).Single();
        var payload = JsonSerializer.Deserialize<ScheduleRunCompletedPayload>(completed.PayloadJson, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("catch-up", payload.TriggerType);
        Assert.Equal("success", payload.Outcome);
        Assert.NotNull(payload.FinishedAt);
    }

    [Fact(DisplayName = "Run-history pruning flows through IRetentionPruner on the retention cadence")]
    public async Task PruningFlowsThroughRetentionPruner()
    {
        await using var harness = await SchedulerHarness.CreateAsync();

        await harness.Service.TickAsync();
        Assert.Single(harness.Pruner.Policies);
        Assert.True(harness.Pruner.Policies.TryPeek(out var policy));
        Assert.Equal(harness.Clock.UtcNow - harness.Options.RunHistoryRetention, policy.OlderThan);

        await harness.Service.TickAsync();
        Assert.Single(harness.Pruner.Policies);

        harness.Clock.Advance(TimeSpan.FromHours(25));
        await harness.Service.TickAsync();
        Assert.Equal(2, harness.Pruner.Policies.Count);
    }

    [Fact(DisplayName = "The real RetentionPruner prunes old schedule runs and keeps recent ones")]
    public async Task RealRetentionPrunerPrunesOldRunHistory()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var options = new SchedulingOptions
        {
            TickInterval = TimeSpan.FromMinutes(30),
            AuditPollInterval = TimeSpan.FromMilliseconds(1),
            RunHistoryRetention = TimeSpan.FromDays(30),
        };
        var pruner = new RetentionPruner(harness.Stack.Provider, new FakeArtifactFileStore());
        using var service = new ScheduledAuditService(
            harness.Stack.Schedules, harness.Stack.Schedules, harness.Stack.Runs, harness.Stack.Runs,
            harness.Stack.Audits, harness.Execution, harness.Clock, new ScheduleEventPublisher(harness.Hub),
            harness.Completion, pruner, options);

        var schedule = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(1));

        var oldInstant = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oldAudit = await harness.Stack.Audits.CreateAsync(new AuditDto(
            0, harness.Stack.ProjectId, harness.Stack.ConfigurationId, "{}", AuditStatus.Completed, AuditPhase.Finished, oldInstant, oldInstant.AddMinutes(5), "{}", "test-engine", false));
        var oldRun = await harness.Stack.Runs.CreateAsync(new ScheduleRunDto(0, schedule.Id, oldAudit.Id, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Success, oldInstant, oldInstant.AddMinutes(5)));

        var recentAudit = await harness.Stack.CreateAuditAsync();
        var recentRun = await harness.Stack.Runs.CreateAsync(new ScheduleRunDto(0, schedule.Id, recentAudit.Id, ScheduleTriggerType.Scheduled, ScheduleRunOutcome.Success, harness.Clock.UtcNow, harness.Clock.UtcNow));

        await service.TickAsync();

        Assert.Null(await harness.Stack.Runs.GetByIdAsync(oldRun.Id));
        Assert.NotNull(await harness.Stack.Runs.GetByIdAsync(recentRun.Id));
    }

    [Fact(DisplayName = "GetNextDue returns the earliest enabled next-due, or null")]
    public async Task GetNextDueReturnsEarliestEnabled()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var later = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(2));
        var earlier = await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(1));
        await harness.CreateScheduleAsync(nextDue: harness.Clock.UtcNow.AddHours(-1), enabled: false);

        Assert.Equal(earlier.NextDueAt, await harness.Service.GetNextDueAsync());

        await harness.Stack.Schedules.UpdateAsync(earlier with { IsEnabled = false });
        Assert.Equal(later.NextDueAt, await harness.Service.GetNextDueAsync());

        await harness.Stack.Schedules.UpdateAsync(later with { IsEnabled = false });
        Assert.Null(await harness.Service.GetNextDueAsync());
    }

    [Fact(DisplayName = "An invalid recurrence rule fails once and clears the due time")]
    public async Task InvalidRuleFailsOnceAndClearsDue()
    {
        await using var harness = await SchedulerHarness.CreateAsync();
        var schedule = await harness.CreateScheduleAsync(rule: "not-a-rule", nextDue: harness.Clock.UtcNow.AddHours(-1));

        await harness.Service.TickAsync();

        var runs = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, runs.TotalCount);
        Assert.Equal(ScheduleRunOutcome.Failed, runs.Items[0].Outcome);

        var updated = await harness.Stack.Schedules.GetByIdAsync(schedule.Id);
        Assert.NotNull(updated);
        Assert.Null(updated.NextDueAt);

        await harness.Service.TickAsync();
        var after = await harness.Stack.Runs.ListAsync(new());
        Assert.Equal(1, after.TotalCount);
    }
}
