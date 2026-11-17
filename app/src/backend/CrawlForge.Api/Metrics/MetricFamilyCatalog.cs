namespace CrawlForge.Api.Metrics;

/// <summary>
/// Local metric family catalog (FS-CRAWL-001 §10.5; FS-CRAWL-002 §3.7/§7.3).
/// Families are open strings — new families arrive additively through
/// <c>IMetricsExposition.Record</c>. Labels are local technical dimensions
/// only (audit identifier, phase, status class, host); no user, machine, or
/// account dimension may ever appear (privacy invariant, criterion #20).
/// Families ending in <c>_total</c> are counters; all others are gauges.
/// </summary>
public static class MetricFamilyCatalog
{
    /// <summary>Crawl URL counter by status class (counter).</summary>
    public const string CrawlUrlsTotal = "crawl_urls_total";

    /// <summary>Crawl throughput, requests per second (gauge).</summary>
    public const string CrawlThroughputPerSecond = "crawl_throughput_per_second";

    /// <summary>Frontier queue depth (gauge).</summary>
    public const string FrontierDepth = "frontier_depth";

    /// <summary>Audit phase duration in seconds (gauge).</summary>
    public const string AuditPhaseDurationSeconds = "audit_phase_duration_seconds";

    /// <summary>Findings emitted by severity (counter).</summary>
    public const string FindingsEmittedTotal = "findings_emitted_total";

    /// <summary>Database operation latency in seconds (gauge).</summary>
    public const string DbOperationDurationSeconds = "db_operation_duration_seconds";

    /// <summary>Content-store size in bytes (gauge).</summary>
    public const string ContentStoreBytes = "content_store_bytes";

    /// <summary>Connected hub clients (gauge).</summary>
    public const string HubConnections = "hub_connections";

    /// <summary>Process working-set RAM in bytes (gauge, §7.3).</summary>
    public const string ProcessWorkingSetBytes = "process_working_set_bytes";

    /// <summary>Managed heap size in bytes (gauge, §7.3).</summary>
    public const string ProcessManagedHeapBytes = "process_managed_heap_bytes";

    /// <summary>GC collections by generation (counter, §7.3).</summary>
    public const string GcCollectionsTotal = "gc_collections_total";

    /// <summary>SQLite WAL size in bytes (gauge, §7.3; absent for external providers).</summary>
    public const string SqliteWalBytes = "sqlite_wal_bytes";

    /// <summary>Per-host effective request rate (gauge, §3.7).</summary>
    public const string HostEffectiveRate = "host_effective_rate";

    /// <summary>Per-host concurrency (gauge, §3.7).</summary>
    public const string HostConcurrency = "host_concurrency";

    /// <summary>AIMD adjustments by trigger (counter, §3.7).</summary>
    public const string HostThrottleAdjustmentsTotal = "host_throttle_adjustments_total";

    /// <summary>
    /// Help text for the specified families; unknown families get a generic
    /// description (additive evolution).
    /// </summary>
    /// <param name="family">Metric family name.</param>
    /// <returns>HELP line text.</returns>
    public static string HelpFor(string family) => family switch
    {
        CrawlUrlsTotal => "Crawled URLs by status class.",
        CrawlThroughputPerSecond => "Crawl throughput in requests per second.",
        FrontierDepth => "Current frontier queue depth.",
        AuditPhaseDurationSeconds => "Audit phase duration in seconds.",
        FindingsEmittedTotal => "Findings emitted by severity.",
        DbOperationDurationSeconds => "Database operation latency in seconds.",
        ContentStoreBytes => "Compressed content-store size in bytes.",
        HubConnections => "Connected real-time hub clients.",
        ProcessWorkingSetBytes => "Backend process working-set RAM in bytes.",
        ProcessManagedHeapBytes => "Managed GC heap size in bytes.",
        GcCollectionsTotal => "GC collections by generation (server-GC aggregated).",
        SqliteWalBytes => "SQLite write-ahead log size in bytes.",
        HostEffectiveRate => "Per-host effective request rate.",
        HostConcurrency => "Per-host request concurrency.",
        HostThrottleAdjustmentsTotal => "AIMD throttle adjustments by trigger.",
        _ => "Local CrawlForge metric.",
    };
}
