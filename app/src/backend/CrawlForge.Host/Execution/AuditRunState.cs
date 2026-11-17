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

    /// <summary>Serializes the counters to the storage payload shape.</summary>
    /// <returns>Counters JSON object.</returns>
    public object ToPayload() => new
    {
        discovered = Discovered,
        fetched = Fetched,
        analyzed = Analyzed,
        failed = Failed
    };
}

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
