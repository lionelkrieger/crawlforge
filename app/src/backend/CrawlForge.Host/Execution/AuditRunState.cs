using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Politeness;

namespace CrawlForge.Host.Execution;

/// <summary>
/// Mutable counters of one audit run (WP-06-01 execution engine). Persisted as
/// the audits.counters_json payload; local run facts only — no identity,
/// machine, or account dimensions (privacy invariant).
/// </summary>
public sealed class AuditRunCounters
{
    /// <summary>URLs discovered (seeds + links + list ingestion).</summary>
    public long Discovered { get; set; }

    /// <summary>URLs fetched over HTTP.</summary>
    public long Fetched { get; set; }

    /// <summary>URLs analyzed through the audit pipeline.</summary>
    public long Analyzed { get; set; }

    /// <summary>Fetches that failed at transport level.</summary>
    public long Failed { get; set; }

    /// <summary>URLs classified Blocked (WAF challenge or robots exclusion at crawl time).</summary>
    public long Blocked { get; set; }

    /// <summary>Blocked URLs attributed to a corroborated WAF challenge (subset of Blocked).</summary>
    public long WafBlocked { get; set; }

    /// <summary>Serializes the counters to the storage payload shape.</summary>
    /// <returns>Counters JSON object.</returns>
    public object ToPayload() => new
    {
        discovered = Discovered,
        fetched = Fetched,
        analyzed = Analyzed,
        failed = Failed,
        blocked = Blocked,
        wafBlocked = WafBlocked
    };
}

/// <summary>
/// One waf-blocks hub batch entry (frozen frontend contract: events.ts
/// WafBlockBatchEntry). Provider/challengeClass are wire enum strings;
/// identifiers are string EntityIds (G-G). WP-09-11 G05 producer.
/// </summary>
/// <param name="UrlId">URL record identifier (string EntityId).</param>
/// <param name="CanonicalUrl">Canonical URL of the blocked record.</param>
/// <param name="Provider">Wire WAF provider key.</param>
/// <param name="ChallengeClass">Wire challenge-class key.</param>
/// <param name="Confidence">Evaluator confidence (0..1).</param>
public sealed record WafBlockBatchEntry(
    string UrlId,
    string CanonicalUrl,
    string Provider,
    string ChallengeClass,
    double Confidence);

/// <summary>
/// In-memory state of one active audit run: cancellation, the pause gate,
/// counters, and the live frontier. The engine observes AuditDto status
/// transitions (pause/resume/cancel) through this state (lifecycle ruling).
/// </summary>
public sealed class AuditRunState : IDisposable
{
    /// <summary>Creates run state for an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationTokenSource">Run cancellation source.</param>
    public AuditRunState(long auditId, CancellationTokenSource cancellationTokenSource)
    {
        AuditId = auditId;
        CancellationTokenSource = cancellationTokenSource;
    }

    /// <summary>Audit identifier.</summary>
    public long AuditId { get; }

    /// <summary>Run cancellation source (cancelled on drain/cancel).</summary>
    public CancellationTokenSource CancellationTokenSource { get; }

    /// <summary>Run counters.</summary>
    public AuditRunCounters Counters { get; } = new();

    /// <summary>Live frontier, once the crawl phase built it.</summary>
    public IPriorityFrontier? Frontier { get; set; }

    /// <summary>Live per-audit AIMD controller, once the crawl phase built it.</summary>
    public AimdPolitenessController? Aimd { get; set; }

    /// <summary>
    /// WP-10-04 (D-021 WAF five-class response): hosts stopped by a hard WAF
    /// block (captcha/interstitial/bot-block). Queued entries for a stopped
    /// host are skipped at dequeue — the blocked URL is never re-fetched and
    /// no further fetches target the host for this run.
    /// </summary>
    public HashSet<string> StoppedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounded-history throttle recorder of the live crawl (flush at checkpoints).</summary>
    public HostThrottleEventRecorder? ThrottleRecorder { get; set; }

    /// <summary>Hub-side coalescing wrapper of the live crawl (flush at checkpoints).</summary>
    public CoalescingHostThrottleSink? ThrottleCoalescer { get; set; }

    /// <summary>Pause gate: completed while running; reset while paused.</summary>
    public TaskCompletionSource PauseGate { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Current observed status of the run.</summary>
    public AuditStatus Status { get; set; } = AuditStatus.Running;

    /// <summary>Timestamp of the last persisted counters update (throttle).</summary>
    public DateTimeOffset LastCounterPersistAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Audit start timestamp (from the persisted row) used for the wire
    /// elapsed/throughput derivations; set when the run loop begins.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Current audit phase (mirrors the persisted row; wire progress).</summary>
    public AuditPhase Phase { get; set; } = AuditPhase.Crawling;

    /// <summary>
    /// Resolved AIMD initial rate (R0) of this run; the throttled-host count
    /// compares each host's effective rate against it (Appendix C telemetry).
    /// </summary>
    public double AimdInitialRate { get; set; } = 1.0;

    /// <summary>
    /// WP-09-11 G05: pending url-batch entries accumulated at URL-record
    /// creation, flushed through the lossless Api publisher at ≤1/s cadence.
    /// </summary>
    public List<UrlBatchEntry> PendingUrlBatch { get; } = [];

    /// <summary>WP-09-11 G05: pending waf-blocks batch entries (≤1/s flush).</summary>
    public List<WafBlockBatchEntry> PendingWafBlocks { get; } = [];

    /// <summary>Timestamp of the last run-event batch flush (≤1/s producer cadence).</summary>
    public DateTimeOffset LastRunEventFlushAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Last findings-updated snapshot: severity-keyed total counts (wire keys).
    /// Deltas are derived against this snapshot at each flush so EVERY finding
    /// producer (pipeline, recorders, site passes) is captured without per-emit hooks.
    /// </summary>
    public Dictionary<string, int> FindingSeverityCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Last findings-updated snapshot: category-keyed total counts (wire keys).</summary>
    public Dictionary<string, int> FindingCategoryCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Canonical URLs declared by ingested sitemaps (crawl phase → post-crawl
    /// sitemap-vs-crawl correlation). Empty when no sitemap was ingested.
    /// </summary>
    public List<string> SitemapCanonicalUrls { get; } = [];

    /// <summary>True once the engine persisted the checkpoint for the current pause.</summary>
    public bool PauseCheckpointSaved { get; set; }

    /// <summary>Marks the run paused (engine checkpoints, then waits).</summary>
    public void SetPaused()
    {
        if (Status == AuditStatus.Running)
        {
            Status = AuditStatus.Paused;
            PauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Marks the run running again and releases the pause gate.</summary>
    public void SetRunning()
    {
        Status = AuditStatus.Running;
        PauseCheckpointSaved = false;
        PauseGate.TrySetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        PauseGate.TrySetResult();
        CancellationTokenSource.Dispose();
    }
}
