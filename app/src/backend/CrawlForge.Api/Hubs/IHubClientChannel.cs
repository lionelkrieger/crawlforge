namespace CrawlForge.Api.Hubs;

/// <summary>
/// Transport abstraction over the connected shell clients. The SignalR
/// adapter (<see cref="SignalRHubChannel"/>) implements it in production;
/// tests substitute an in-memory channel. Keeping transport behind this
/// seam makes the throttling/routing/heartbeat core fully testable.
/// </summary>
public interface IHubClientChannel
{
    /// <summary>Sends an event to every connected client.</summary>
    /// <param name="eventName">Hub event name.</param>
    /// <param name="payloadJson">JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the send is dispatched.</returns>
    Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>Sends an event to one per-audit channel group.</summary>
    /// <param name="groupName">Group name (audit-{id}).</param>
    /// <param name="eventName">Hub event name.</param>
    /// <param name="payloadJson">JSON payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the send is dispatched.</returns>
    Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>Joins a connection to a group.</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="groupName">Group name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the join is applied.</returns>
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);

    /// <summary>Removes a connection from a group.</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="groupName">Group name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the leave is applied.</returns>
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);
}
