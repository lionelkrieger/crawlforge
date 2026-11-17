using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Crawl;

/// <summary>
/// Background service firing recurring audit schedules (FS-CRAWL-001 §6).
/// Schedules are local only; first-run state contains no schedules.
/// </summary>
public interface IScheduledAuditService
{
    /// <summary>Starts the scheduler loop.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the scheduler is running.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the scheduler loop.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the scheduler has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Triggers a schedule immediately (manual run-now).</summary>
    /// <param name="scheduleDefinitionId">Schedule definition identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier of the started audit.</returns>
    Task<long> RunNowAsync(long scheduleDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the earliest next-due timestamp across enabled schedules.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Next-due timestamp, or null when no schedule is enabled.</returns>
    Task<DateTimeOffset?> GetNextDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive (FS-CRAWL-002 §6, amendment WP-02-A1): the scheduler's audit-start
/// port. The phase-03 audit pipeline implements it and Host binds the
/// implementation; the scheduler only consumes it to start runs (scheduled,
/// manual, or catch-up) without depending on the pipeline directly.
/// </summary>
public interface IAuditExecutionService
{
    /// <summary>Starts one audit for a project/configuration pair.</summary>
    /// <param name="projectId">Owning project identifier.</param>
    /// <param name="configurationId">Crawl configuration identifier to snapshot.</param>
    /// <param name="trigger">What fired this run: scheduled, manual, or catch-up.</param>
    /// <param name="limitOverrides">Optional per-audit limit overrides; null keeps the resolved limits profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier of the newly created audit.</returns>
    Task<long> StartAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType trigger,
        [AdditiveMember] CrawlLimitOverrides? limitOverrides,
        CancellationToken cancellationToken = default);
}
