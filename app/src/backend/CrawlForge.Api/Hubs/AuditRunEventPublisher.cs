using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Api;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// One url-batch entry (frozen frontend contract: events.ts UrlBatchEntry,
/// guards.ts isUrlBatchPayload). <paramref name="StatusClass"/> is one of
/// 1xx/2xx/3xx/4xx/5xx; blocked entries carry the observed class plus the
/// additive <paramref name="BlockReason"/> / <paramref name="WafProvider"/>
/// annotations. Identifiers are string <c>EntityId</c> values (G-G).
/// </summary>
/// <param name="UrlId">URL record identifier (string EntityId).</param>
/// <param name="CanonicalUrl">Canonical URL of the record.</param>
/// <param name="StatusClass">HTTP status class: 1xx, 2xx, 3xx, 4xx, or 5xx.</param>
/// <param name="BlockReason">Additive block reason on blocked entries, when known.</param>
/// <param name="WafProvider">Additive detected WAF provider on blocked entries, when known.</param>
public sealed record UrlBatchEntry(
    string UrlId,
    string CanonicalUrl,
    string StatusClass,
    string? BlockReason = null,
    string? WafProvider = null);

/// <summary>
/// WP-09-06 G05: the Api-side publication surface for the engine's batch-flush
/// and finding-emit points (FS-CRAWL-001 §10.3, §5.7 cadence discipline).
/// Mirrors the accepted <c>ExportEventPublisher</c> pattern over the locked
/// <see cref="IRealtimeHub"/> abstraction.
/// <para>
/// Delivery is LOSSLESS: both events are published with an explicit zero
/// throttle interval, bypassing the hub's latest-wins coalescing. Coalescing
/// url-batch would drop whole batches (URL rows never reach the shell) and
/// coalescing incremental findings-updated deltas would corrupt the frontend
/// counters. Cadence (at most once per second per audit) is the PRODUCER's
/// responsibility: the engine accumulates entries/deltas and flushes through
/// these methods at its batch-flush / finding-emit points (wiring handoff).
/// </para>
/// <para>
/// Payloads are serialized camelCase with string EntityIds; null annotations
/// are omitted. The hub boundary normalizer passes them through unchanged
/// (ids are already strings; no counters members are present).
/// </para>
/// </summary>
public sealed class AuditRunEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> StatusClasses = new(StringComparer.Ordinal) { "1xx", "2xx", "3xx", "4xx", "5xx" };

    private readonly IRealtimeHub _hub;

    /// <summary>Creates the publisher over the locked hub abstraction.</summary>
    /// <param name="hub">Real-time hub (locked contract).</param>
    public AuditRunEventPublisher(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <summary>
    /// Publishes one url-batch (a flushed batch of URL records, NOT a per-URL
    /// message). Every batch is delivered; the engine flushes at most once per
    /// second per audit. Routes to the per-audit group <c>audit-{id}</c>.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="entries">The flushed batch entries (status classes validated).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    /// <exception cref="ArgumentException">An entry carries an unknown status class.</exception>
    public Task PublishUrlBatchAsync(long auditId, IReadOnlyList<UrlBatchEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (!StatusClasses.Contains(entry.StatusClass))
            {
                throw new ArgumentException($"Unknown status class '{entry.StatusClass}' (use 1xx, 2xx, 3xx, 4xx, or 5xx).", nameof(entries));
            }
        }

        var payload = new
        {
            auditId = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            urls = entries,
        };
        return _hub.PublishAsync(
            new HubEvent(
                HubEventCatalog.UrlBatch,
                JsonSerializer.Serialize(payload, JsonOptions),
                ThrottleKey: null,
                ThrottleInterval: TimeSpan.Zero),
            cancellationToken);
    }

    /// <summary>
    /// Publishes findings-updated count deltas (severity/category). Every
    /// emission is delivered; deltas are incremental since the previous
    /// emission and the engine emits at most once per second per audit.
    /// Routes to the per-audit group <c>audit-{id}</c>.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="severityDeltas">Severity-keyed count deltas (wire severity keys).</param>
    /// <param name="categoryDeltas">Category-keyed count deltas (wire category keys).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishFindingsUpdatedAsync(
        long auditId,
        IReadOnlyDictionary<string, int> severityDeltas,
        IReadOnlyDictionary<string, int> categoryDeltas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(severityDeltas);
        ArgumentNullException.ThrowIfNull(categoryDeltas);
        var payload = new
        {
            auditId = auditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            severityDeltas,
            categoryDeltas,
        };
        return _hub.PublishAsync(
            new HubEvent(
                HubEventCatalog.FindingsUpdated,
                JsonSerializer.Serialize(payload, JsonOptions),
                ThrottleKey: null,
                ThrottleInterval: TimeSpan.Zero),
            cancellationToken);
    }
}
