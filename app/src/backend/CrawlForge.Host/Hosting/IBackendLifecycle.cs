namespace CrawlForge.Host.Hosting;

/// <summary>
/// Host-local lifecycle participant (WP-00-01 infrastructure; intentionally NOT part of
/// the shared CrawlForge.Contracts surface). Participants start after first-launch
/// initialization and before the readiness signal, and are drained in reverse order on
/// graceful shutdown before the database closes (process-model spec §2.3 / §2.7).
/// </summary>
public interface IBackendLifecycle
{
    /// <summary>Lower values start earlier and stop later. Defaults to 0.</summary>
    int Order => 0;

    /// <summary>Starts the participant. Startup completes with no network access.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Drains the participant gracefully as part of shutdown.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
