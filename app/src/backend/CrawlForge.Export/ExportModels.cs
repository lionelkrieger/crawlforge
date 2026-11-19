using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Export;

/// <summary>
/// Canonical dataset keys selectable in <see cref="ExportRequest.Datasets"/>
/// (FS-CRAWL-001 §8.4 workbook sheets + FS-CRAWL-002 Appendix B additive
/// datasets). Keys are open strings; unknown keys are skipped with a diagnostic.
/// </summary>
public static class ExportDatasetKeys
{
    /// <summary>Audit summary: counts, severity mix, metric aggregates.</summary>
    public const string Summary = "summary";

    /// <summary>Per-URL records: status, timing, indexability, metadata.</summary>
    public const string Urls = "urls";

    /// <summary>Finding rows with rule, category, severity, evidence.</summary>
    public const string Findings = "findings";

    /// <summary>Internal link edges with anchor text and relation flags.</summary>
    public const string Links = "links";

    /// <summary>Custom extraction with dynamic per-rule columns (§7.9).</summary>
    public const string CustomExtraction = "custom-extraction";

    /// <summary>JS snippet outputs with dynamic result columns (§7.17).</summary>
    public const string Snippets = "snippets";

    /// <summary>N-gram site summaries (WP-04-01 read model).</summary>
    public const string NGrams = "ngrams";

    /// <summary>Redirect matrix rows (WP-02-05 read model).</summary>
    public const string RedirectMatrix = "redirect-matrix";

    /// <summary>WAF block observations.</summary>
    public const string WafBlocks = "waf-blocks";

    /// <summary>Semantic opportunity rows.</summary>
    public const string SemanticOpportunity = "semantic-opportunity";

    /// <summary>Analytics observations.</summary>
    public const string Analytics = "analytics";

    /// <summary>Default selection when the request names no datasets.</summary>
    public static IReadOnlyList<string> Default { get; } = [Summary, Urls, Findings, Links];
}

/// <summary>
/// One export column. Dynamic datasets build columns through the locked
/// <c>DynamicColumnSchema</c> contract; fixed datasets declare stable columns.
/// </summary>
/// <param name="Key">Stable column key.</param>
/// <param name="Label">User-facing header label.</param>
/// <param name="ValueType">text, number, boolean, datetime, or string.</param>
public sealed record ExportColumn(string Key, string Label, string ValueType);

/// <summary>
/// One export row: cell values aligned to the dataset columns. Values are
/// pre-rendered invariant strings; writers never re-parse payloads.
/// </summary>
/// <param name="Values">Cell values (null = empty cell).</param>
public sealed record ExportRow(IReadOnlyList<string?> Values);

/// <summary>
/// A streaming export dataset: fixed columns plus a forward-only row sequence
/// backed by a database reader. Consumers must enumerate at most once and must
/// not materialize the sequence (500k-row bound, FS-CRAWL-002 §7.1).
/// </summary>
/// <param name="Key">Dataset key.</param>
/// <param name="Columns">Ordered columns.</param>
/// <param name="Rows">Forward-only row stream.</param>
public sealed record ExportDataset(string Key, IReadOnlyList<ExportColumn> Columns, IAsyncEnumerable<ExportRow> Rows);

/// <summary>
/// Export engine options. All paths are local; no option introduces network
/// traffic (FS-CRAWL-001 §2.6).
/// </summary>
public sealed class ExportOptions
{
    /// <summary>Default options.</summary>
    public static ExportOptions Default { get; } = new();

    /// <summary>Directory where export files are written (created on demand).</summary>
    public string OutputDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "exports");

    /// <summary>Maximum delivery attempts before the failed terminal state.</summary>
    public int MaxDeliveryAttempts { get; init; } = 3;

    /// <summary>Base delay of the bounded exponential delivery backoff.</summary>
    public TimeSpan DeliveryRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout of one webhook delivery request.</summary>
    public TimeSpan WebhookTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sitemap protocol URL limit per sitemap file (index splits above).</summary>
    public int SitemapMaxUrlsPerFile { get; init; } = 50_000;

    /// <summary>Default sitemap change-frequency hint.</summary>
    public string SitemapChangeFrequency { get; init; } = "weekly";

    /// <summary>Default sitemap priority hint for non-home pages.</summary>
    public double SitemapDefaultPriority { get; init; } = 0.5;

    /// <summary>Sitemap priority hint for the site root.</summary>
    public double SitemapHomePriority { get; init; } = 1.0;
}

/// <summary>
/// Delivery state machine states (FS-CRAWL-001 §8.5): a delivery is delivered,
/// retrying (bounded backoff), or failed; failures never fail the audit or the
/// local export.
/// </summary>
public static class ExportDeliveryStates
{
    /// <summary>Transfer in progress.</summary>
    public const string Delivering = "delivering";

    /// <summary>Transfer succeeded.</summary>
    public const string Delivered = "delivered";

    /// <summary>Attempt failed; a bounded retry is scheduled.</summary>
    public const string Retrying = "retrying";

    /// <summary>All attempts exhausted.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Hub event names emitted by the export engine (additive open strings,
/// FS-CRAWL-001 §10.3). Local facts only — no identity dimensions.
/// </summary>
public static class ExportHubEventNames
{
    /// <summary>Export job lifecycle progress (queued/running/completed/failed).</summary>
    public const string ExportProgress = "export-progress";

    /// <summary>Destination delivery state transitions.</summary>
    public const string ExportDelivery = "export-delivery";
}

/// <summary>
/// Typed payload of the <c>export-progress</c> hub event (WP-09-10, G-I fix).
/// Wire shape matches the proposed additive frontend contract: string entity
/// identifiers (<c>EntityId</c> convention) and <c>state</c> (the
/// ExportJobState vocabulary queued/running/completed/failed), mirroring
/// ExportJobWire. Additive catalog entry is a lead handoff (events.ts +
/// HubEventCatalog); the producer already emits this exact shape.
/// </summary>
/// <param name="ExportId">The export job (string identifier).</param>
/// <param name="AuditId">The exported audit (string identifier).</param>
/// <param name="Format">Wire format value.</param>
/// <param name="State">Wire job state value.</param>
/// <param name="FilePath">Local file path when completed.</param>
/// <param name="Message">Failure or progress detail.</param>
public sealed record ExportProgressPayload(
    string ExportId,
    string AuditId,
    string Format,
    string State,
    string? FilePath,
    string? Message);

/// <summary>
/// Typed payload of the <c>export-delivery</c> hub event (WP-09-10, G-F fix).
/// Wire shape matches the frozen frontend contract exactly (events.ts
/// ExportDeliveryPayload, guards.ts isExportDeliveryPayload): string
/// <c>exportId</c> + <c>destinationId</c> and a state from the frozen
/// vocabulary delivered/retrying/failed. The internal <c>delivering</c>
/// transition stays in the delivery ledger only — it is NOT a hub state
/// (the frontend guard rejects it). Attempt/message detail lives in the
/// ledger (<see cref="DeliveryLedgerEntry"/>), not on the wire.
/// </summary>
/// <param name="ExportId">The delivered export job (string identifier).</param>
/// <param name="DestinationId">The user-configured destination (string identifier).</param>
/// <param name="State">delivered, retrying, or failed.</param>
public sealed record ExportDeliveryPayload(
    string ExportId,
    string DestinationId,
    string State);

/// <summary>
/// Publishes the export engine's typed hub events through the locked
/// <see cref="IRealtimeHub"/> abstraction (mirrors the accepted scheduler
/// publisher pattern).
/// </summary>
public sealed class ExportEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IRealtimeHub _hub;

    /// <summary>Creates the publisher over the locked hub abstraction.</summary>
    /// <param name="hub">Real-time hub (locked contract).</param>
    public ExportEventPublisher(IRealtimeHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
    }

    /// <summary>Publishes an export-progress event (throttled per job).</summary>
    /// <param name="payload">Typed progress payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishProgressAsync(ExportProgressPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _hub.PublishAsync(
            new HubEvent(
                ExportHubEventNames.ExportProgress,
                JsonSerializer.Serialize(payload, JsonOptions),
                $"export-progress:{payload.ExportId}",
                TimeSpan.FromMilliseconds(250)),
            cancellationToken);
    }

    /// <summary>Publishes an export-delivery state transition.</summary>
    /// <param name="payload">Typed delivery payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the event is dispatched.</returns>
    public Task PublishDeliveryAsync(ExportDeliveryPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _hub.PublishAsync(
            new HubEvent(
                ExportHubEventNames.ExportDelivery,
                JsonSerializer.Serialize(payload, JsonOptions),
                $"export-delivery:{payload.ExportId}:{payload.DestinationId}",
                TimeSpan.FromMilliseconds(250)),
            cancellationToken);
    }
}

/// <summary>Maps export enums to their stable wire values.</summary>
public static class ExportWireExtensions
{
    /// <summary>Maps an export format to its wire value.</summary>
    /// <param name="format">Export format.</param>
    /// <returns>excel, csv, html, pdf, or xml-sitemap.</returns>
    public static string ToWireValue(this ExportFormat format) => format switch
    {
        ExportFormat.Excel => "excel",
        ExportFormat.Csv => "csv",
        ExportFormat.Html => "html",
        ExportFormat.Pdf => "pdf",
        ExportFormat.XmlSitemap => "xml-sitemap",
        _ => format.ToString().ToLowerInvariant(),
    };

    /// <summary>Maps an export job status to its wire value.</summary>
    /// <param name="status">Job status.</param>
    /// <returns>queued, running, completed, or failed.</returns>
    public static string ToWireValue(this ExportJobStatus status) => status switch
    {
        ExportJobStatus.Queued => "queued",
        ExportJobStatus.Running => "running",
        ExportJobStatus.Completed => "completed",
        ExportJobStatus.Failed => "failed",
        _ => status.ToString().ToLowerInvariant(),
    };
}
