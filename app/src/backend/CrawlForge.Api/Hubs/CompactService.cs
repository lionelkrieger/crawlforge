using CrawlForge.Contracts.Api;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// Manual "Compact / Trim Working Set" service (criterion #41; FS-CRAWL-002
/// §7.2/§8.2; locked <see cref="ICompactService"/> from amendment WP-05-A1).
/// Performs managed GC compaction, a SQLite WAL checkpoint (truncate mode,
/// embedded provider), and an OS working-set trim where the platform
/// supports it; returns before/after <see cref="SystemHealthSnapshot"/>s so
/// the caller derives reclaimed bytes. Enforces the locked guards: ≥10 s
/// between invocations and refusal while an audit is running — both throw
/// <see cref="InvalidOperationException"/> (REST maps to conflict). Logged
/// locally; no telemetry leaves the machine. WP-05-01's POST
/// /api/system/compact route delegates here.
/// </summary>
public sealed class CompactService : ICompactService
{
    /// <summary>Wire action: managed GC compaction.</summary>
    public const string ActionGcCompaction = "managed-gc-compaction";

    /// <summary>Wire action: truncating WAL checkpoint.</summary>
    public const string ActionWalCheckpoint = "wal-checkpoint-truncate";

    /// <summary>Wire action: OS working-set trim.</summary>
    public const string ActionWorkingSetTrim = "working-set-trim";

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    private readonly ISystemHealthSampler _sampler;
    private readonly IWalCheckpointer? _walCheckpointer;
    private readonly Func<bool>? _isAuditRunning;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompactService>? _logger;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private DateTimeOffset? _lastInvocation;

    /// <summary>Creates the compact service.</summary>
    /// <param name="sampler">Health measurement for before/after snapshots.</param>
    /// <param name="walCheckpointer">Embedded-provider checkpoint; null for external providers.</param>
    /// <param name="isAuditRunning">In-flight audit guard; null assumes idle.</param>
    /// <param name="timeProvider">Clock; null uses the system clock.</param>
    /// <param name="logger">Optional local logger (no identity dimensions).</param>
    public CompactService(
        ISystemHealthSampler sampler,
        IWalCheckpointer? walCheckpointer = null,
        Func<bool>? isAuditRunning = null,
        TimeProvider? timeProvider = null,
        ILogger<CompactService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        _sampler = sampler;
        _walCheckpointer = walCheckpointer;
        _isAuditRunning = isAuditRunning;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Actions performed by the most recent successful invocation (additive
    /// diagnostics surface; wire values managed-gc-compaction,
    /// wal-checkpoint-truncate, working-set-trim). Empty before the first run.
    /// </summary>
    public IReadOnlyList<string> LastActionsPerformed { get; private set; } = [];

    /// <inheritdoc />
    public async Task<CompactResult> CompactAsync(CancellationToken cancellationToken = default)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastInvocation is { } last && now - last < MinimumInterval)
            {
                throw new InvalidOperationException(
                    $"Compact is rate-limited (minimum 10 s between invocations); retry after {(MinimumInterval - (now - last)).TotalSeconds:F0}s.");
            }

            // Locked guard: refuse while an audit is in flight — never
            // interrupt an audit or transaction.
            if (_isAuditRunning?.Invoke() == true)
            {
                throw new InvalidOperationException("Compact refused: an audit is in flight.");
            }

            _lastInvocation = now;
            var before = _sampler.CaptureSnapshot();
            var actions = new List<string>(3);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            actions.Add(ActionGcCompaction);

            if (_walCheckpointer is not null
                && await _walCheckpointer.TryCheckpointTruncateAsync(cancellationToken).ConfigureAwait(false))
            {
                actions.Add(ActionWalCheckpoint);
            }

            if (WorkingSetTrimmer.TryTrim())
            {
                actions.Add(ActionWorkingSetTrim);
            }

            var after = _sampler.CaptureSnapshot();
            LastActionsPerformed = actions;
            _logger?.LogInformation(
                "Compact performed: {Actions}; working set {Before} -> {After} bytes.",
                string.Join(",", actions),
                before.WorkingSetBytes,
                after.WorkingSetBytes);
            return new CompactResult(before, after, _timeProvider.GetUtcNow(), actions);
        }
        finally
        {
            _singleFlight.Release();
        }
    }
}
