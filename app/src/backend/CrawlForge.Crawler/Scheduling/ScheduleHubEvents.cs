using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Hub event names emitted by the scheduler (FS-CRAWL-001 §6/§10.3). Event names
/// are open strings; these arrive additively alongside the existing hub surface.
/// The shell consumes them in phase 05 — this workpiece defines the typed contracts only.
/// </summary>
public static class ScheduleHubEventNames
{
    /// <summary>A schedule firing started (or was skipped/failed at start).</summary>
    public const string ScheduleFired = "schedule-fired";

    /// <summary>A scheduled run reached its terminal state, including completion-action results.</summary>
    public const string ScheduleRunCompleted = "schedule-run-completed";

    /// <summary>Local OS notification request surfaced through the shell (local only).</summary>
    public const string LocalNotification = "local-notification";
}

/// <summary>
/// Typed payload of the <c>schedule-fired</c> event. Local audit facts only;
/// no identity, machine, or account dimensions (privacy invariant).
/// </summary>
/// <param name="ScheduleDefinitionId">The firing schedule.</param>
/// <param name="AuditId">The started audit, or null when the firing failed or was skipped.</param>
/// <param name="TriggerType">Wire trigger value: scheduled, manual, or catch-up.</param>
/// <param name="StartedAt">Firing start timestamp (UTC).</param>
public sealed record ScheduleFiredPayload(
    long ScheduleDefinitionId,
    long? AuditId,
    string TriggerType,
    DateTimeOffset StartedAt);

/// <summary>
/// Typed payload of the <c>schedule-run-completed</c> event, including per-run
/// completion-action outcomes (FS-CRAWL-001 §6.3).
/// </summary>
/// <param name="ScheduleDefinitionId">The owning schedule.</param>
/// <param name="AuditId">The finished audit, or null on start failure/skip.</param>
/// <param name="TriggerType">Wire trigger value: scheduled, manual, or catch-up.</param>
/// <param name="Outcome">Wire outcome value: success, failed, or skipped.</param>
/// <param name="StartedAt">Firing start timestamp (UTC).</param>
/// <param name="FinishedAt">Firing finish timestamp (UTC), when finished.</param>
/// <param name="CompletionActions">Completion-action outcomes in configuration order.</param>
/// <param name="FailureReason">Failure detail when the firing or audit failed; otherwise null.</param>
public sealed record ScheduleRunCompletedPayload(
    long ScheduleDefinitionId,
    long? AuditId,
    string TriggerType,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<CompletionActionOutcome> CompletionActions,
    string? FailureReason = null);

/// <summary>
/// Publishes the scheduler's typed hub events through the locked
/// <see cref="IRealtimeHub"/> abstraction.
/// </summary>
public sealed class ScheduleEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRealtimeHub _hub;

    /// <summary>Creates the publisher over the locked hub abstraction.</summary>
    /// <param name="hub">Real-time hub (locked contract).</param>
    public ScheduleEventPublisher(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <summary>Publishes a schedule-fired event.</summary>
    /// <param name="payload">Typed firing payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishFiredAsync(ScheduleFiredPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _hub.PublishAsync(new HubEvent(ScheduleHubEventNames.ScheduleFired, JsonSerializer.Serialize(payload, JsonOptions)), cancellationToken);
    }

    /// <summary>Publishes a schedule-run-completed event.</summary>
    /// <param name="payload">Typed completion payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishRunCompletedAsync(ScheduleRunCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _hub.PublishAsync(new HubEvent(ScheduleHubEventNames.ScheduleRunCompleted, JsonSerializer.Serialize(payload, JsonOptions)), cancellationToken);
    }

    /// <summary>Publishes a local notification request for the shell.</summary>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishLocalNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var payload = new { title, message };
        return _hub.PublishAsync(new HubEvent(ScheduleHubEventNames.LocalNotification, JsonSerializer.Serialize(payload, JsonOptions)), cancellationToken);
    }
}

/// <summary>
/// Default local-notification sink: publishes a local-only hub event that the
/// shell renders as an OS notification (phase 05). No remote surface is involved.
/// </summary>
public sealed class HubLocalNotificationSink : ILocalNotificationSink
{
    private readonly ScheduleEventPublisher _publisher;

    /// <summary>Creates the sink over the scheduler event publisher.</summary>
    /// <param name="publisher">Scheduler event publisher.</param>
    public HubLocalNotificationSink(ScheduleEventPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    /// <inheritdoc/>
    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default) =>
        _publisher.PublishLocalNotificationAsync(title, message, cancellationToken);
}

/// <summary>
/// Wire-value mappings for schedule enums. Values are open lowercase strings so
/// the hub payload stays stable across additive enum evolution.
/// </summary>
public static class ScheduleWireValues
{
    /// <summary>Maps a trigger type to its wire value.</summary>
    /// <param name="trigger">Trigger type.</param>
    /// <returns>scheduled, manual, or catch-up.</returns>
    public static string ToWireValue(this ScheduleTriggerType trigger) => trigger switch
    {
        ScheduleTriggerType.Scheduled => "scheduled",
        ScheduleTriggerType.Manual => "manual",
        ScheduleTriggerType.CatchUp => "catch-up",
        _ => trigger.ToString().ToLowerInvariant(),
    };

    /// <summary>Maps a run outcome to its wire value.</summary>
    /// <param name="outcome">Run outcome.</param>
    /// <returns>success, failed, or skipped.</returns>
    public static string ToWireValue(this ScheduleRunOutcome outcome) => outcome switch
    {
        ScheduleRunOutcome.Success => "success",
        ScheduleRunOutcome.Failed => "failed",
        ScheduleRunOutcome.Skipped => "skipped",
        _ => outcome.ToString().ToLowerInvariant(),
    };
}
