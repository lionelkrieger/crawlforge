namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Local scheduler tuning (FS-CRAWL-001 §6.2). All values are local configuration;
/// no remote service participates in scheduling. Defaults reproduce the spec's
/// small fixed global concurrency and are fully overridable for deterministic tests.
/// </summary>
public sealed class SchedulingOptions
{
    /// <summary>Cadence of the scheduler loop. The loop processes due schedules once per tick.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A due firing delayed beyond this threshold is classified as a catch-up run
    /// (the machine was off or asleep at the due time). Null means one tick interval:
    /// a healthy running scheduler processes due times within a single tick.
    /// </summary>
    public TimeSpan? CatchUpThreshold { get; set; }

    /// <summary>Maximum number of audits (scheduled or manual) running at once; excess due runs queue in due-time order.</summary>
    public int MaxConcurrentAudits { get; set; } = 2;

    /// <summary>Cadence of audit terminal-state observation for active scheduled runs.</summary>
    public TimeSpan AuditPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Run-history retention window applied through <c>IRetentionPruner</c>.</summary>
    public TimeSpan RunHistoryRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Minimum clock time between retention prune passes.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Maximum time a completion action may wait for an export job to finish generating.</summary>
    public TimeSpan ExportCompletionTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cadence of export-job state polling inside the export completion action.</summary>
    public TimeSpan ExportPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Effective catch-up threshold (never smaller than one tick).</summary>
    public TimeSpan EffectiveCatchUpThreshold => CatchUpThreshold ?? TickInterval;

    /// <summary>Validates the option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its permitted range.</exception>
    public void Validate()
    {
        if (TickInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TickInterval), TickInterval, "The tick interval must be positive.");
        }

        if (CatchUpThreshold is { } threshold && threshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CatchUpThreshold), threshold, "The catch-up threshold must be positive when set.");
        }

        if (MaxConcurrentAudits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAudits), MaxConcurrentAudits, "At least one concurrent audit must be permitted.");
        }

        if (AuditPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AuditPollInterval), AuditPollInterval, "The audit poll interval must be positive.");
        }

        if (RunHistoryRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RunHistoryRetention), RunHistoryRetention, "The run-history retention window must be positive.");
        }

        if (PruneInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PruneInterval), PruneInterval, "The prune interval must be positive.");
        }

        if (ExportCompletionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExportCompletionTimeout), ExportCompletionTimeout, "The export completion timeout must be positive.");
        }

        if (ExportPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExportPollInterval), ExportPollInterval, "The export poll interval must be positive.");
        }
    }
}
