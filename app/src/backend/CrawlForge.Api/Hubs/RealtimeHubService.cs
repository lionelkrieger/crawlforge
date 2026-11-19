using System.Text.Json;
using CrawlForge.Api.Metrics;
using CrawlForge.Contracts.Api;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// Real-time hub core (locked <see cref="IRealtimeHub"/>; FS-CRAWL-001 §10.3,
/// FS-CRAWL-002 §7.2 + Appendix C). Implements the specified emission rules:
/// latest-wins coalescing per throttle key (crawl-progress ≤1/s, url-batch /
/// findings-updated / waf-blocks / matrix-row-validated / log-import-progress /
/// embedding-progress ≤1/s, host-throttle ≤1/s per host), per-audit channel
/// routing, and the 2-second system-health heartbeat while any shell client
/// is connected (idle or running). Transport sits behind
/// <see cref="IHubClientChannel"/>; the SignalR adapter is a thin shell.
/// No event carries identity, machine, or account dimensions; nothing leaves
/// the loopback boundary (criteria #20/#41 surface).
/// </summary>
public sealed class RealtimeHubService : IRealtimeHub, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly TimeSpan FlushTick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> AuditScopedEvents = new(StringComparer.Ordinal)
    {
        HubEventCatalog.CrawlProgress,
        HubEventCatalog.UrlBatch,
        HubEventCatalog.StageChanged,
        HubEventCatalog.FindingsUpdated,
        HubEventCatalog.AuditCompleted,
        HubEventCatalog.AuditFailed,
        HubEventCatalog.WafBlocks,
        HubEventCatalog.HostThrottle,
        HubEventCatalog.MatrixRowValidated,
        HubEventCatalog.EmbeddingProgress,
        HubEventCatalog.EmbeddingCompleted,
    };

    private readonly IHubClientChannel _channel;
    private readonly ISystemHealthSampler _healthSampler;
    private readonly TimeProvider _timeProvider;
    private readonly IMetricsExposition? _metrics;
    private readonly ILogger<RealtimeHubService>? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ThrottleState> _throttles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
    private readonly ITimer _flushTimer;
    private readonly ITimer _heartbeatTimer;
    private int _disposed;

    /// <summary>Creates the hub core.</summary>
    /// <param name="channel">Client transport.</param>
    /// <param name="healthSampler">Health measurement for the heartbeat.</param>
    /// <param name="timeProvider">Clock; null uses the system clock.</param>
    /// <param name="metrics">Optional local metrics sink (connection gauge).</param>
    /// <param name="logger">Optional local logger (no identity dimensions).</param>
    public RealtimeHubService(
        IHubClientChannel channel,
        ISystemHealthSampler healthSampler,
        TimeProvider? timeProvider = null,
        IMetricsExposition? metrics = null,
        ILogger<RealtimeHubService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(healthSampler);
        _channel = channel;
        _healthSampler = healthSampler;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
        _logger = logger;
        _flushTimer = _timeProvider.CreateTimer(_ => FlushPending(), null, FlushTick, FlushTick);
        _heartbeatTimer = _timeProvider.CreateTimer(_ => EmitHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>Number of connected shell clients.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hubEvent);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var interval = hubEvent.ThrottleInterval ?? HubEventCatalog.DefaultInterval(hubEvent.Name);
        if (interval is null || interval <= TimeSpan.Zero)
        {
            await DispatchAsync(hubEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = hubEvent.ThrottleKey ?? DefaultThrottleKey(hubEvent);
        var now = _timeProvider.GetUtcNow();
        HubEvent? immediate = null;
        lock (_gate)
        {
            if (!_throttles.TryGetValue(key, out var state))
            {
                state = new ThrottleState();
                _throttles[key] = state;
            }

            if (state.LastSentAt is null || now - state.LastSentAt.Value >= interval)
            {
                state.LastSentAt = now;
                state.Pending = null;
                immediate = hubEvent;
            }
            else
            {
                // Latest-wins coalescing: the newest payload replaces any
                // pending one; the flush timer emits it at the interval end.
                state.Pending = hubEvent;
                state.Interval = interval.Value;
            }
        }

        if (immediate is not null)
        {
            await DispatchAsync(immediate, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Registers a connected shell client (hub adapter callback).</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when registration is applied.</returns>
    public Task ConnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        int count;
        lock (_gate)
        {
            _connections.Add(connectionId);
            count = _connections.Count;
        }

        _metrics?.Record(MetricFamilyCatalog.HubConnections, count);
        _logger?.LogDebug("Hub client connected; connections={Count}.", count);
        return Task.CompletedTask;
    }

    /// <summary>Unregisters a disconnected shell client (hub adapter callback).</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when unregistration is applied.</returns>
    public Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        int count;
        lock (_gate)
        {
            _connections.Remove(connectionId);
            count = _connections.Count;
        }

        _metrics?.Record(MetricFamilyCatalog.HubConnections, count);
        _logger?.LogDebug("Hub client disconnected; connections={Count}.", count);
        return Task.CompletedTask;
    }

    /// <summary>Joins per-audit channels (shell → server subscribe).</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="auditIds">Audit identifiers to subscribe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all joins are applied.</returns>
    public async Task SubscribeAsync(string connectionId, IReadOnlyList<long> auditIds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(auditIds);
        foreach (var auditId in auditIds.Distinct())
        {
            await _channel.AddToGroupAsync(connectionId, GroupName(auditId), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Leaves per-audit channels (shell → server unsubscribe).</summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <param name="auditIds">Audit identifiers to unsubscribe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all leaves are applied.</returns>
    public async Task UnsubscribeAsync(string connectionId, IReadOnlyList<long> auditIds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(auditIds);
        foreach (var auditId in auditIds.Distinct())
        {
            await _channel.RemoveFromGroupAsync(connectionId, GroupName(auditId), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Per-audit channel group name.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <returns>The group name.</returns>
    public static string GroupName(long auditId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"audit-{auditId}");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        await _heartbeatTimer.DisposeAsync().ConfigureAwait(false);
    }

    private async void FlushPending()
    {
        List<HubEvent> due = [];
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            foreach (var state in _throttles.Values)
            {
                if (state.Pending is { } pending
                    && state.LastSentAt is { } lastSent
                    && now - lastSent >= state.Interval)
                {
                    state.LastSentAt = now;
                    state.Pending = null;
                    due.Add(pending);
                }
            }
        }

        foreach (var hubEvent in due)
        {
            try
            {
                await DispatchAsync(hubEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Throttled hub event flush failed for {EventName}.", hubEvent.Name);
            }
        }
    }

    private async void EmitHeartbeat()
    {
        int connections;
        lock (_gate)
        {
            connections = _connections.Count;
        }

        if (connections == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return; // Heartbeat only while the shell is connected.
        }

        try
        {
            var payload = _healthSampler.CapturePayload();
            // G-G: the heartbeat carries the optional running-audit id as a
            // string EntityId too; it bypasses DispatchAsync, so normalize here.
            var json = HubPayloadNormalizer.Normalize(
                HubEventCatalog.SystemHealth, JsonSerializer.Serialize(payload, PayloadOptions));
            await _channel.SendAllAsync(HubEventCatalog.SystemHealth, json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "System-health heartbeat emission failed.");
        }
    }

    private async Task DispatchAsync(HubEvent hubEvent, CancellationToken cancellationToken)
    {
        // WP-09-06 (G06/G-G/G-HUB-COUNTERS): reconcile the engine's storage-shaped
        // payload to the frozen wire (string EntityIds, REST counter keys) at the
        // hub boundary; routing reads the normalized payload.
        var payloadJson = HubPayloadNormalizer.Normalize(hubEvent.Name, hubEvent.PayloadJson);
        var auditId = TryReadEntityId(payloadJson, "auditId");
        if (auditId is { } id && AuditScopedEvents.Contains(hubEvent.Name))
        {
            await _channel.SendGroupAsync(GroupName(id), hubEvent.Name, payloadJson, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _channel.SendAllAsync(hubEvent.Name, payloadJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private string DefaultThrottleKey(HubEvent hubEvent)
    {
        if (string.Equals(hubEvent.Name, HubEventCatalog.HostThrottle, StringComparison.Ordinal))
        {
            var host = TryReadString(hubEvent.PayloadJson, "host") ?? "unknown";
            var auditId = TryReadEntityId(hubEvent.PayloadJson, "auditId");
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{hubEvent.Name}:{auditId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "global"}:{host}");
        }

        if (string.Equals(hubEvent.Name, HubEventCatalog.LogImportProgress, StringComparison.Ordinal))
        {
            var importId = TryReadEntityId(hubEvent.PayloadJson, "importId");
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{hubEvent.Name}:{importId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "global"}");
        }

        var scope = TryReadEntityId(hubEvent.PayloadJson, "auditId");
        return scope is { } value
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{hubEvent.Name}:{value}")
            : hubEvent.Name;
    }

    /// <summary>
    /// Reads an entity identifier tolerantly (G-G): numeric producer ids and
    /// normalized string <c>EntityId</c> values both route to the same group.
    /// </summary>
    private static long? TryReadEntityId(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetInt64(out var value) ? value : null,
                JsonValueKind.String => long.TryParse(property.GetString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadString(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ThrottleState
    {
        public DateTimeOffset? LastSentAt { get; set; }

        public HubEvent? Pending { get; set; }

        public TimeSpan Interval { get; set; }
    }
}
