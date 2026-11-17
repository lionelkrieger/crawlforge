using Microsoft.AspNetCore.SignalR;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// CrawlForge real-time hub (FS-CRAWL-001 §10.3; FS-CRAWL-002 §7.2 +
/// Appendix C). Thin SignalR adapter: connection lifecycle and the
/// subscribe/unsubscribe channel delegate to the singleton
/// <see cref="RealtimeHubService"/> core, which owns throttling, per-audit
/// routing, and the 2-second system-health heartbeat. Server events are
/// delivered as client methods named after the event (locked TS contract).
/// No authorization beyond the local loopback boundary; no identity
/// dimensions in any payload.
/// </summary>
public sealed class CrawlForgeHub : Hub
{
    /// <summary>
    /// Loopback hub route the composition root maps (Host, later phase).
    /// </summary>
    public const string HubRoutePath = "/hubs/realtime";

    private readonly RealtimeHubService _service;

    /// <summary>Creates the hub adapter.</summary>
    /// <param name="service">Singleton hub core (DI).</param>
    public CrawlForgeHub(RealtimeHubService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync() =>
        _service.ConnectAsync(Context.ConnectionId, Context.ConnectionAborted);

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception) =>
        _service.DisconnectAsync(Context.ConnectionId, Context.ConnectionAborted);

    /// <summary>
    /// Shell → server: joins per-audit channels (locked TS
    /// <c>AuditChannelPayload</c>: string audit identifiers). No
    /// authorization beyond the local boundary.
    /// </summary>
    /// <param name="auditIds">Audit identifiers to subscribe.</param>
    /// <returns>A task that completes when all joins are applied.</returns>
    public Task Subscribe(string[] auditIds) =>
        _service.SubscribeAsync(Context.ConnectionId, ParseIds(auditIds), Context.ConnectionAborted);

    /// <summary>
    /// Shell → server: leaves per-audit channels.
    /// </summary>
    /// <param name="auditIds">Audit identifiers to unsubscribe.</param>
    /// <returns>A task that completes when all leaves are applied.</returns>
    public Task Unsubscribe(string[] auditIds) =>
        _service.UnsubscribeAsync(Context.ConnectionId, ParseIds(auditIds), Context.ConnectionAborted);

    private static long[] ParseIds(string[]? auditIds)
    {
        if (auditIds is null || auditIds.Length == 0)
        {
            return [];
        }

        var parsed = new List<long>(auditIds.Length);
        foreach (var text in auditIds)
        {
            if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                parsed.Add(id);
            }
        }

        return parsed.ToArray();
    }
}
