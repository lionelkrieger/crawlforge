namespace CrawlForge.Api.Hubs;

/// <summary>
/// The complete real-time hub event surface (FS-CRAWL-001 §10.3 baseline,
/// FS-CRAWL-002 §7.2 + Appendix C additive). Event names are open strings
/// (locked <c>Constants.HubEventNames</c> fixes host-throttle/system-health);
/// these constants name the rest of the specified surface. Unknown events
/// pass through unchanged — additive evolution never breaks the hub.
/// No event carries identity, machine, or account dimensions.
/// </summary>
public static class HubEventCatalog
{
    // ── Baseline (FS-CRAWL-001 §10.3) ─────────────────────────────────────

    /// <summary>Per-audit crawl counters/throughput; at most once per second.</summary>
    public const string CrawlProgress = "crawl-progress";

    /// <summary>Throttled batches of (urlId, canonicalUrl, statusClass).</summary>
    public const string UrlBatch = "url-batch";

    /// <summary>Audit lifecycle transition (previous/new state).</summary>
    public const string StageChanged = "stage-changed";

    /// <summary>Severity/category finding count deltas; throttled.</summary>
    public const string FindingsUpdated = "findings-updated";

    /// <summary>Once, on audit completion (final counters, report readiness).</summary>
    public const string AuditCompleted = "audit-completed";

    /// <summary>Once, on audit failure (error code, message).</summary>
    public const string AuditFailed = "audit-failed";

    /// <summary>Backend readiness/shutdown notice (global).</summary>
    public const string BackendLifecycle = "backend-lifecycle";

    /// <summary>Schedule firing (schedule id, audit id, trigger type).</summary>
    public const string ScheduleFired = "schedule-fired";

    /// <summary>Scheduled run finalization with completion-action results.</summary>
    public const string ScheduleRunCompleted = "schedule-run-completed";

    /// <summary>Export destination delivery state change.</summary>
    public const string ExportDelivery = "export-delivery";

    // ── Additive (FS-CRAWL-002 §7.2 + Appendix C) ─────────────────────────

    /// <summary>WAF/challenge block batches; at most once per second.</summary>
    public const string WafBlocks = "waf-blocks";

    /// <summary>AIMD adjustment; coalesced ≤1/s per host.</summary>
    public const string HostThrottle = "host-throttle";

    /// <summary>2-second health heartbeat while the shell is connected.</summary>
    public const string SystemHealth = "system-health";

    /// <summary>Migration-matrix row batches; at most once per second.</summary>
    public const string MatrixRowValidated = "matrix-row-validated";

    /// <summary>Log import counters; at most once per second.</summary>
    public const string LogImportProgress = "log-import-progress";

    /// <summary>Once per log import finalization.</summary>
    public const string LogImportCompleted = "log-import-completed";

    /// <summary>Semantic embedding progress; at most once per second.</summary>
    public const string EmbeddingProgress = "embedding-progress";

    /// <summary>Once per semantic job finalization.</summary>
    public const string EmbeddingCompleted = "embedding-completed";

    /// <summary>
    /// Local OS notification request emitted by the accepted scheduler
    /// (additive open-string event; rendered by the shell, local only).
    /// </summary>
    public const string LocalNotification = "local-notification";

    /// <summary>
    /// Export job progress (G-I additive; WP-09-10 producer): string export/
    /// audit ids, format wire key, frozen job-state vocabulary, local file
    /// path when completed. At most once per second per job.
    /// </summary>
    public const string ExportProgress = "export-progress";

    /// <summary>Shell → server: join per-audit channels.</summary>
    public const string Subscribe = "subscribe";

    /// <summary>Shell → server: leave per-audit channels.</summary>
    public const string Unsubscribe = "unsubscribe";

    /// <summary>
    /// Default minimum emission interval for throttled event families
    /// (FS-CRAWL-001 §5.7 cadence discipline; Appendix C rules). Events
    /// without an entry are emitted immediately (once-per-lifecycle events).
    /// Explicit <c>HubEvent.ThrottleInterval</c> always wins over this table.
    /// </summary>
    /// <param name="eventName">Event name.</param>
    /// <returns>The default interval, or null when unthrottled.</returns>
    public static TimeSpan? DefaultInterval(string eventName) => eventName switch
    {
        CrawlProgress => TimeSpan.FromSeconds(1),
        UrlBatch => TimeSpan.FromSeconds(1),
        FindingsUpdated => TimeSpan.FromSeconds(1),
        WafBlocks => TimeSpan.FromSeconds(1),
        HostThrottle => TimeSpan.FromSeconds(1),
        MatrixRowValidated => TimeSpan.FromSeconds(1),
        LogImportProgress => TimeSpan.FromSeconds(1),
        EmbeddingProgress => TimeSpan.FromSeconds(1),
        ExportProgress => TimeSpan.FromSeconds(1),
        _ => null,
    };
}
