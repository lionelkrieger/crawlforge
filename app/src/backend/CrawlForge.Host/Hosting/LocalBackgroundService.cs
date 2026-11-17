using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Base class for long-lived local background services (the audit scheduler, retention
/// pruning, and export-delivery retries arrive with later phases). Failures are logged
/// locally and retried with bounded backoff; a failing service never takes down the API
/// or an in-flight audit (process-model spec §2.7). No remote reporting of any kind.
/// </summary>
public abstract class LocalBackgroundService : BackgroundService
{
    private readonly ILogger _logger;

    protected LocalBackgroundService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Base delay between restart attempts; grows linearly with the attempt count.</summary>
    protected virtual TimeSpan RestartDelay => TimeSpan.FromSeconds(5);

    /// <summary>Maximum restart attempts before the service stays stopped until the next host start.</summary>
    protected virtual int MaxRestartAttempts => 5;

    /// <summary>Runs the service body once. Long-lived services loop internally until cancelled.</summary>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempts++;
                _logger.LogError(
                    ex,
                    "Background service {Service} failed (attempt {Attempt}/{Max}); logged locally only.",
                    GetType().Name,
                    attempts,
                    MaxRestartAttempts);

                if (attempts >= MaxRestartAttempts)
                {
                    return;
                }

                try
                {
                    await Task.Delay(RestartDelay * attempts, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
