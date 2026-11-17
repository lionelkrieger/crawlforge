using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// SignalR transport adapter over <see cref="IHubContext{T}"/> for the
/// CrawlForge real-time hub. Each event is delivered as a client method
/// named after the event with the parsed JSON payload as its single
/// argument (locked TS contract: per-event handlers, typed payloads).
/// </summary>
public sealed class SignalRHubChannel : IHubClientChannel
{
    private readonly IHubContext<CrawlForgeHub> _context;

    /// <summary>Creates the channel.</summary>
    /// <param name="context">SignalR hub context (DI).</param>
    public SignalRHubChannel(IHubContext<CrawlForgeHub> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public Task SendAllAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default) =>
        _context.Clients.All.SendCoreAsync(eventName, [ToArgument(payloadJson)], cancellationToken);

    /// <inheritdoc />
    public Task SendGroupAsync(string groupName, string eventName, string payloadJson, CancellationToken cancellationToken = default) =>
        _context.Clients.Group(groupName).SendCoreAsync(eventName, [ToArgument(payloadJson)], cancellationToken);

    /// <inheritdoc />
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        _context.Groups.AddToGroupAsync(connectionId, groupName, cancellationToken);

    /// <inheritdoc />
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        _context.Groups.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

    private static object ToArgument(string payloadJson)
    {
        try
        {
            // Deliver a structured object, not a double-encoded JSON string.
            return JsonSerializer.Deserialize<JsonElement>(payloadJson);
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }
}
