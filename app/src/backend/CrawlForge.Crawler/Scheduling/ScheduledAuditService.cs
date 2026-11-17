using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Scheduled-audits background service (FS-CRAWL-001 §6). Fires recurring local
/// schedules against the injected clock with:
/// <list type="bullet">
/// <item>catch-up semantics — a due time missed while the machine was off runs exactly
/// once at the next opportunity, recorded with <see cref="ScheduleTriggerType.CatchUp"/>
/// (no backlog replay: the next due time is always recomputed strictly after the firing);</item>
/// <item>no overlap — two audits of the same schedule never run simultaneously; a due
/// time arriving during an active run is recorded as skipped-overlap;</item>
/// <item>global concurrency — at most <see cref="SchedulingOptions.MaxConcurrentAudits"/>
/// audits run at once; excess due runs queue in due-time order;</item>
/// <item>restart durability — next-due state lives in schedule_definitions; unfinished
/// runs are reconciled at start so the no-overlap guard survives restarts.</item>
/// </list>
/// Audit execution is delegated through <see cref="IAuditExecutionService"/> (phase-03
/// pipeline); completion actions run only when pre-authorized and never fail the audit;
/// run history is pruned through <see cref="IRetentionPruner"/>.
/// </summary>
public sealed class ScheduledAuditService : IScheduledAuditService, IDisposable
{
    private const int MaxSchedulesPerPass = 10_000;

    private readonly IReadRepository<ScheduleDefinitionDto> _scheduleRead;
    private readonly IWriteRepository<ScheduleDefinitionDto> _scheduleWrite;
    private readonly IReadRepository<ScheduleRunDto> _runRead;
    private readonly IWriteRepository<ScheduleRunDto> _runWrite;
    private readonly IReadRepository<AuditDto> _auditRead;
    private readonly IAuditExecutionService _execution;
    private readonly ISchedulerClock _clock;
    private readonly ScheduleEventPublisher _events;
    private readonly ICompletionActionExecutor _completionActions;
    private readonly IRetentionPruner _pruner;
    private readonly SchedulingOptions _options;

    private readonly object _stateLock = new();
    private readonly Dictionary<long, ActiveRun> _activeRuns = [];
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTimeOffset? _lastPruneAt;
    private int _disposed;

    /// <summary>Creates the scheduler from its locked dependencies.</summary>
    /// <param name="scheduleRead">Schedule query side.</param>
    /// <param name="scheduleWrite">Schedule command side.</param>
    /// <param name="runRead">Run-history query side.</param>
    /// <param name="runWrite">Run-history command side.</param>
    /// <param name="auditRead">Audit query side (terminal-state observation).</param>
    /// <param name="execution">Audit-start port (locked contract, phase-03 implementation).</param>
    /// <param name="clock">Injectable scheduler clock.</param>
    /// <param name="events">Typed hub event publisher.</param>
    /// <param name="completionActions">Pre-authorized completion-action executor.</param>
    /// <param name="pruner">Retention pruner (locked contract).</param>
    /// <param name="options">Scheduler options.</param>
    public ScheduledAuditService(
        IReadRepository<ScheduleDefinitionDto> scheduleRead,
        IWriteRepository<ScheduleDefinitionDto> scheduleWrite,
        IReadRepository<ScheduleRunDto> runRead,
        IWriteRepository<ScheduleRunDto> runWrite,
        IReadRepository<AuditDto> auditRead,
        IAuditExecutionService execution,
        ISchedulerClock clock,
        ScheduleEventPublisher events,
        ICompletionActionExecutor completionActions,
        IRetentionPruner pruner,
        SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(scheduleRead);
        ArgumentNullException.ThrowIfNull(scheduleWrite);
        ArgumentNullException.ThrowIfNull(runRead);
        ArgumentNullException.ThrowIfNull(runWrite);
        ArgumentNullException.ThrowIfNull(auditRead);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(completionActions);
        ArgumentNullException.ThrowIfNull(pruner);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _scheduleRead = scheduleRead;
        _scheduleWrite = scheduleWrite;
        _runRead = runRead;
        _runWrite = runWrite;
        _auditRead = auditRead;
        _execution = execution;
        _clock = clock;
        _events = events;
        _completionActions = completionActions;
        _pruner = pruner;
        _options = options;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_loopTask is not null)
        {
            return;
        }

        await ReconcileUnfinishedRunsAsync(cancellationToken).ConfigureAwait(false);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var token = _loopCts.Token;
        _loopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // The loop survives individual tick failures; the next tick retries.
                }

                try
                {
                    await Task.Delay(_options.TickInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_loopCts is null)
        {
            return;
        }

        await _loopCts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _loopCts.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    /// <inheritdoc/>
    public async Task<long> RunNowAsync(long scheduleDefinitionId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var schedule = await _scheduleRead.GetByIdAsync(scheduleDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Schedule {scheduleDefinitionId} does not exist.");

        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            if (IsActive(scheduleDefinitionId))
            {
                await RecordSkippedFiringAsync(schedule, ScheduleTriggerType.Manual, now, advanceDue: false, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Schedule {scheduleDefinitionId} already has an active run (no overlap).");
            }

            if (ActiveCount() >= _options.MaxConcurrentAudits)
            {
                throw new InvalidOperationException("The global audit concurrency limit is reached; retry when a run finishes.");
            }

            // Manual run-now does not disturb the recurrence cadence (next due unchanged).
            var auditId = await FireAsync(schedule, ScheduleTriggerType.Manual, now, advanceDue: false, cancellationToken).ConfigureAwait(false);
            return auditId ?? throw new InvalidOperationException($"Schedule {scheduleDefinitionId} failed to start an audit; see run history.");
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetNextDueAsync(CancellationToken cancellationToken = default)
    {
        var page = await _scheduleRead.ListAsync(new ListQuery(Limit: MaxSchedulesPerPass), cancellationToken).ConfigureAwait(false);
        var dueTimes = page.Items
            .Where(static s => s.IsEnabled && s.NextDueAt is not null)
            .Select(static s => s.NextDueAt!.Value)
            .ToList();

        return dueTimes.Count == 0 ? null : dueTimes.Min();
    }

    /// <summary>
    /// Processes one scheduling pass: prunes run history when due, recomputes missing
    /// next-due timestamps for enabled schedules, then fires due schedules in due-time
    /// order under the no-overlap and global-concurrency guards. Exposed so tests can
    /// drive deterministic ticks against the injected clock without real-time waiting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the pass finishes.</returns>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            await MaybePruneAsync(now, cancellationToken).ConfigureAwait(false);

            var page = await _scheduleRead.ListAsync(new ListQuery(Limit: MaxSchedulesPerPass), cancellationToken).ConfigureAwait(false);
            var enabled = page.Items.Where(static s => s.IsEnabled).ToList();

            // Self-heal enabled schedules without a next-due timestamp (for example after
            // re-enable): recompute per the recurrence rule; the new due is strictly future.
            foreach (var schedule in enabled.Where(static s => s.NextDueAt is null))
            {
                if (RecurrenceRule.TryParse(schedule.RecurrenceRule, out var rule, out _))
                {
                    var nextDue = rule.GetNextOccurrence(now, _clock.LocalTimeZone);
                    await _scheduleWrite.UpdateAsync(schedule with { NextDueAt = nextDue }, cancellationToken).ConfigureAwait(false);
                }
            }

            var due = enabled
                .Where(static s => s.NextDueAt is not null)
                .Where(s => s.NextDueAt!.Value <= now)
                .OrderBy(static s => s.NextDueAt)
                .ToList();

            foreach (var schedule in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsActive(schedule.Id))
                {
                    await RecordSkippedFiringAsync(schedule, ClassifyTrigger(now, schedule.NextDueAt!.Value), now, advanceDue: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (ActiveCount() >= _options.MaxConcurrentAudits)
                {
                    // Excess due runs queue in due-time order: they stay due for the next tick.
                    break;
                }

                await FireAsync(schedule, ClassifyTrigger(now, schedule.NextDueAt!.Value), now, advanceDue: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Stops the loop and cancels outstanding run watchers.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownCts.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Expected on shutdown.
        }

        Task[] watchers;
        lock (_stateLock)
        {
            watchers = _activeRuns.Values.Select(static r => r.WatchTask).ToArray();
        }

        try
        {
            Task.WhenAll(watchers).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Expected on shutdown.
        }

        _shutdownCts.Dispose();
        _tickGate.Dispose();
    }

    private ScheduleTriggerType ClassifyTrigger(DateTimeOffset now, DateTimeOffset dueAt) =>
        now - dueAt > _options.EffectiveCatchUpThreshold ? ScheduleTriggerType.CatchUp : ScheduleTriggerType.Scheduled;

    private bool IsActive(long scheduleId)
    {
        lock (_stateLock)
        {
            return _activeRuns.ContainsKey(scheduleId);
        }
    }

    private int ActiveCount()
    {
        lock (_stateLock)
        {
            return _activeRuns.Count;
        }
    }

    private async Task<long?> FireAsync(ScheduleDefinitionDto schedule, ScheduleTriggerType trigger, DateTimeOffset now, bool advanceDue, CancellationToken cancellationToken)
    {
        if (!RecurrenceRule.TryParse(schedule.RecurrenceRule, out var rule, out var ruleError))
        {
            await RecordFailedFiringAsync(schedule, trigger, now, nextDue: null, advanceDue, $"Invalid recurrence rule: {ruleError}", cancellationToken).ConfigureAwait(false);
            return null;
        }

        long auditId;
        try
        {
            auditId = await _execution.StartAuditAsync(schedule.ProjectId, schedule.ConfigurationId, trigger, schedule.LimitOverrides, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var nextDueOnFailure = advanceDue ? rule.GetNextOccurrence(now, _clock.LocalTimeZone) : (DateTimeOffset?)null;
            await RecordFailedFiringAsync(schedule, trigger, now, nextDueOnFailure, advanceDue, ex.Message, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var run = await _runWrite.CreateAsync(new ScheduleRunDto(0, schedule.Id, auditId, trigger, ScheduleRunOutcome.Success, now, null), cancellationToken).ConfigureAwait(false);

        if (advanceDue)
        {
            // Exactly-once catch-up: the next due is strictly after this firing, so a
            // missed window is never replayed as a backlog.
            var nextDue = rule.GetNextOccurrence(now, _clock.LocalTimeZone);
            await _scheduleWrite.UpdateAsync(schedule with { NextDueAt = nextDue }, cancellationToken).ConfigureAwait(false);
        }

        RegisterActiveRun(run, SafeParseActions(schedule.CompletionActionsJson));
        await _events.PublishFiredAsync(new ScheduleFiredPayload(schedule.Id, auditId, trigger.ToWireValue(), now), cancellationToken).ConfigureAwait(false);
        return auditId;
    }

    private async Task RecordSkippedFiringAsync(ScheduleDefinitionDto schedule, ScheduleTriggerType trigger, DateTimeOffset now, bool advanceDue, CancellationToken cancellationToken)
    {
        await _runWrite.CreateAsync(new ScheduleRunDto(0, schedule.Id, null, trigger, ScheduleRunOutcome.Skipped, now, now), cancellationToken).ConfigureAwait(false);

        if (advanceDue && RecurrenceRule.TryParse(schedule.RecurrenceRule, out var rule, out _))
        {
            var nextDue = rule.GetNextOccurrence(now, _clock.LocalTimeZone);
            await _scheduleWrite.UpdateAsync(schedule with { NextDueAt = nextDue }, cancellationToken).ConfigureAwait(false);
        }

        await _events.PublishFiredAsync(new ScheduleFiredPayload(schedule.Id, null, trigger.ToWireValue(), now), cancellationToken).ConfigureAwait(false);
        await _events.PublishRunCompletedAsync(new ScheduleRunCompletedPayload(
            schedule.Id, null, trigger.ToWireValue(), ScheduleRunOutcome.Skipped.ToWireValue(), now, now, [], "Previous run still active (skipped-overlap)."), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordFailedFiringAsync(ScheduleDefinitionDto schedule, ScheduleTriggerType trigger, DateTimeOffset now, DateTimeOffset? nextDue, bool advanceDue, string reason, CancellationToken cancellationToken)
    {
        await _runWrite.CreateAsync(new ScheduleRunDto(0, schedule.Id, null, trigger, ScheduleRunOutcome.Failed, now, now), cancellationToken).ConfigureAwait(false);

        if (advanceDue)
        {
            // A null nextDue clears an unparseable rule's due time so it cannot fail every tick.
            await _scheduleWrite.UpdateAsync(schedule with { NextDueAt = nextDue }, cancellationToken).ConfigureAwait(false);
        }

        await _events.PublishFiredAsync(new ScheduleFiredPayload(schedule.Id, null, trigger.ToWireValue(), now), cancellationToken).ConfigureAwait(false);
        await _events.PublishRunCompletedAsync(new ScheduleRunCompletedPayload(
            schedule.Id, null, trigger.ToWireValue(), ScheduleRunOutcome.Failed.ToWireValue(), now, now, [], reason), cancellationToken).ConfigureAwait(false);
    }

    private void RegisterActiveRun(ScheduleRunDto run, IReadOnlyList<ScheduleCompletionAction> actions)
    {
        var watchTask = Task.Run(() => WatchAuditAsync(run, actions, _shutdownCts.Token), CancellationToken.None);
        lock (_stateLock)
        {
            _activeRuns[run.ScheduleDefinitionId] = new ActiveRun(run, actions, watchTask);
        }
    }

    private async Task WatchAuditAsync(ScheduleRunDto run, IReadOnlyList<ScheduleCompletionAction> actions, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var audit = await _auditRead.GetByIdAsync(run.AuditId!.Value, cancellationToken).ConfigureAwait(false);

                if (audit is null)
                {
                    await FinalizeRunAsync(run, actions, AuditStatus.Failed, _clock.UtcNow, executeActions: false, "Audit row disappeared.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (IsTerminal(audit.Status))
                {
                    var reason = audit.Status == AuditStatus.Completed ? null : $"Audit finished with status {audit.Status}.";
                    await FinalizeRunAsync(run, actions, audit.Status, audit.FinishedAt ?? _clock.UtcNow, executeActions: true, reason, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(_options.AuditPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: the unfinished run is reconciled at the next StartAsync.
        }
        catch (Exception)
        {
            // Never leave the no-overlap guard wedged by a failed watcher.
            lock (_stateLock)
            {
                _activeRuns.Remove(run.ScheduleDefinitionId);
            }
        }
    }

    private async Task FinalizeRunAsync(ScheduleRunDto run, IReadOnlyList<ScheduleCompletionAction> actions, AuditStatus status, DateTimeOffset finishedAt, bool executeActions, string? failureReason, CancellationToken cancellationToken)
    {
        IReadOnlyList<CompletionActionOutcome> outcomes = [];
        if (executeActions && status == AuditStatus.Completed && actions.Count > 0 && run.AuditId is not null)
        {
            outcomes = await _completionActions.ExecuteAsync(run.AuditId.Value, run.ScheduleDefinitionId, actions, cancellationToken).ConfigureAwait(false);
        }

        await _runWrite.UpdateAsync(run with { FinishedAt = finishedAt }, cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            _activeRuns.Remove(run.ScheduleDefinitionId);
        }

        await _events.PublishRunCompletedAsync(new ScheduleRunCompletedPayload(
            run.ScheduleDefinitionId, run.AuditId, run.TriggerType.ToWireValue(), run.Outcome.ToWireValue(), run.StartedAt, finishedAt, outcomes, failureReason), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileUnfinishedRunsAsync(CancellationToken cancellationToken)
    {
        var page = await _runRead.ListAsync(new ListQuery(Limit: MaxSchedulesPerPass), cancellationToken).ConfigureAwait(false);
        foreach (var run in page.Items.Where(static r => r.FinishedAt is null))
        {
            if (run.AuditId is null)
            {
                await _runWrite.UpdateAsync(run with { FinishedAt = run.StartedAt }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var audit = await _auditRead.GetByIdAsync(run.AuditId.Value, cancellationToken).ConfigureAwait(false);
            if (audit is null || IsTerminal(audit.Status))
            {
                await FinalizeRunAsync(
                    run, [], audit?.Status ?? AuditStatus.Failed, audit?.FinishedAt ?? _clock.UtcNow,
                    executeActions: false, audit is null ? "Audit row missing at restart." : null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The audit survived the restart still running: restore the no-overlap guard.
                var schedule = await _scheduleRead.GetByIdAsync(run.ScheduleDefinitionId, cancellationToken).ConfigureAwait(false);
                RegisterActiveRun(run, schedule is null ? [] : SafeParseActions(schedule.CompletionActionsJson));
            }
        }
    }

    private async Task MaybePruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_lastPruneAt is not null && now - _lastPruneAt < _options.PruneInterval)
        {
            return;
        }

        _lastPruneAt = now;
        try
        {
            await _pruner.PruneAsync(new RetentionPolicy(OlderThan: now - _options.RunHistoryRetention), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Maintenance failure must not stop the scheduler; the next pass retries.
        }
    }

    private static bool IsTerminal(AuditStatus status) =>
        status is AuditStatus.Completed or AuditStatus.Cancelled or AuditStatus.Failed;

    private static IReadOnlyList<ScheduleCompletionAction> SafeParseActions(string? json)
    {
        try
        {
            return ScheduleCompletionActions.Parse(json);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private sealed record ActiveRun(ScheduleRunDto Run, IReadOnlyList<ScheduleCompletionAction> Actions, Task WatchTask);
}
