using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Executes pre-authorized completion actions (FS-CRAWL-001 §6.5): local OS
/// notification, export delivery to a user-configured destination, and completion
/// messages to user-configured notification endpoints. Every action failure is
/// captured as an outcome; an action never fails the audit.
/// </summary>
public sealed class CompletionActionExecutor : ICompletionActionExecutor
{
    private readonly IExportEngine _exports;
    private readonly IExportDestinationDelivery _delivery;
    private readonly ILocalNotificationSink _notifications;
    private readonly INotificationEndpointClient _endpointClient;
    private readonly ISchedulerClock _clock;
    private readonly SchedulingOptions _options;

    /// <summary>Creates the executor from its locked dependencies.</summary>
    /// <param name="exports">Export engine (locked contract).</param>
    /// <param name="delivery">User-destination delivery (locked contract).</param>
    /// <param name="notifications">Local notification surface.</param>
    /// <param name="endpointClient">User-endpoint message client.</param>
    /// <param name="clock">Scheduler clock.</param>
    /// <param name="options">Scheduler options (export polling).</param>
    public CompletionActionExecutor(
        IExportEngine exports,
        IExportDestinationDelivery delivery,
        ILocalNotificationSink notifications,
        INotificationEndpointClient endpointClient,
        ISchedulerClock clock,
        SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(endpointClient);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _exports = exports;
        _delivery = delivery;
        _notifications = notifications;
        _endpointClient = endpointClient;
        _clock = clock;
        _options = options;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CompletionActionOutcome>> ExecuteAsync(
        long auditId,
        long scheduleDefinitionId,
        IReadOnlyList<ScheduleCompletionAction> actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var outcomes = new List<CompletionActionOutcome>(actions.Count);
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteOneAsync(auditId, scheduleDefinitionId, action, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new CompletionActionOutcome(action.Kind, true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // §6.5: action failures are logged in run history, never fail the audit.
                outcomes.Add(new CompletionActionOutcome(action.Kind, false, ex.Message));
            }
        }

        return outcomes;
    }

    private async Task ExecuteOneAsync(long auditId, long scheduleDefinitionId, ScheduleCompletionAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case CompletionActionKinds.LocalNotification:
                await _notifications.NotifyAsync(
                    action.Title ?? "CrawlForge",
                    action.Message ?? $"Scheduled audit {auditId} completed.",
                    cancellationToken).ConfigureAwait(false);
                break;

            case CompletionActionKinds.ExportDelivery:
                await DeliverExportAsync(auditId, action, cancellationToken).ConfigureAwait(false);
                break;

            case CompletionActionKinds.NotificationEndpoint:
                var payload = new ScheduleNotificationPayload(
                    auditId,
                    scheduleDefinitionId,
                    action.Title ?? "CrawlForge scheduled audit completed",
                    action.Message ?? $"Scheduled audit {auditId} completed.",
                    _clock.UtcNow);
                await _endpointClient.SendAsync(action.Endpoint!, payload, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown completion-action kind '{action.Kind}'.");
        }
    }

    private async Task DeliverExportAsync(long auditId, ScheduleCompletionAction action, CancellationToken cancellationToken)
    {
        var request = new ExportRequest(auditId, action.Format!.Value, action.Datasets ?? []);
        var job = await _exports.CreateExportAsync(request, cancellationToken).ConfigureAwait(false);

        var deadline = _clock.UtcNow + _options.ExportCompletionTimeout;
        while (true)
        {
            var current = await _exports.GetExportAsync(job.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Export job {job.Id} disappeared before completion.");

            if (current.Status == ExportJobStatus.Completed)
            {
                await _delivery.DeliverAsync(current.Id, action.DestinationId!.Value, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (current.Status == ExportJobStatus.Failed)
            {
                throw new InvalidOperationException($"Export job {current.Id} failed to generate.");
            }

            if (_clock.UtcNow >= deadline)
            {
                throw new TimeoutException($"Export job {current.Id} did not complete within {_options.ExportCompletionTimeout}.");
            }

            await Task.Delay(_options.ExportPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
