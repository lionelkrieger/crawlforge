using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Runs <see cref="IBackendLifecycle"/> participants in order at startup and drains them
/// in reverse order at shutdown. Startup failures fail fast (the backend must not signal
/// readiness half-initialized); drain failures are logged locally and never take down
/// the API or the remaining drain (process-model spec §2.7).
/// </summary>
public sealed class BackendLifecycleCoordinator
{
    private readonly IEnumerable<IBackendLifecycle> _participants;
    private readonly ILogger<BackendLifecycleCoordinator> _logger;

    public BackendLifecycleCoordinator(
        IEnumerable<IBackendLifecycle> participants,
        ILogger<BackendLifecycleCoordinator> logger)
    {
        _participants = participants;
        _logger = logger;
    }

    public async Task StartAllAsync(CancellationToken cancellationToken)
    {
        foreach (var participant in _participants.OrderBy(p => p.Order))
        {
            await participant.StartAsync(cancellationToken);
            _logger.LogInformation("Lifecycle participant {Participant} started.", participant.GetType().Name);
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var participant in _participants.OrderByDescending(p => p.Order))
        {
            try
            {
                await participant.StopAsync(cancellationToken);
                _logger.LogInformation("Lifecycle participant {Participant} drained.", participant.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Lifecycle participant {Participant} failed to drain; continuing shutdown.",
                    participant.GetType().Name);
            }
        }
    }
}
