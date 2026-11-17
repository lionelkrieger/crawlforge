namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Outcome of one completion action, recorded per run (FS-CRAWL-001 §6.3/§6.5).
/// Action failures are reported in run history but never fail the audit.
/// </summary>
/// <param name="Kind">Action kind (see <see cref="CompletionActionKinds"/>).</param>
/// <param name="Succeeded">Whether the action completed successfully.</param>
/// <param name="Message">Failure detail or success note.</param>
public sealed record CompletionActionOutcome(string Kind, bool Succeeded, string? Message);

/// <summary>
/// Executes the pre-authorized completion actions of a finished scheduled run.
/// Implementations must swallow per-action failures into outcomes.
/// </summary>
public interface ICompletionActionExecutor
{
    /// <summary>Executes all configured actions for a completed audit.</summary>
    /// <param name="auditId">The finished audit.</param>
    /// <param name="scheduleDefinitionId">The owning schedule.</param>
    /// <param name="actions">Pre-authorized actions (empty means nothing runs).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One outcome per action, in configuration order.</returns>
    Task<IReadOnlyList<CompletionActionOutcome>> ExecuteAsync(
        long auditId,
        long scheduleDefinitionId,
        IReadOnlyList<ScheduleCompletionAction> actions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Local OS notification surface. The shell adapter (phase 05) renders the OS
/// notification; until then the hub-backed default publishes a local-only event.
/// </summary>
public interface ILocalNotificationSink
{
    /// <summary>Surfaces a local notification to the user.</summary>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the notification is surfaced.</returns>
    Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Completion message posted to a notification endpoint the user configured
/// themselves (FS-CRAWL-001 §6.5). Only ever targets the user's own endpoint.
/// </summary>
public interface INotificationEndpointClient
{
    /// <summary>Posts a completion message to the user-configured endpoint.</summary>
    /// <param name="endpoint">Absolute http(s) endpoint URL from the user's configuration.</param>
    /// <param name="payload">Completion message payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the endpoint accepts the message.</returns>
    Task SendAsync(string endpoint, ScheduleNotificationPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Completion message payload for a user-configured notification endpoint.
/// Local audit facts only; no identity, machine, or account dimensions.
/// </summary>
/// <param name="AuditId">The finished audit.</param>
/// <param name="ScheduleDefinitionId">The owning schedule.</param>
/// <param name="Title">Message title.</param>
/// <param name="Message">Message body.</param>
/// <param name="CompletedAt">Completion timestamp (UTC).</param>
public sealed record ScheduleNotificationPayload(
    long AuditId,
    long ScheduleDefinitionId,
    string Title,
    string Message,
    DateTimeOffset CompletedAt);
