using System.Diagnostics;
using CrawlForge.Contracts.Api;
using CrawlForge.Data.Sqlite;

namespace CrawlForge.Api.Hubs;

/// <summary>
/// Full system-health heartbeat payload (FS-CRAWL-002 §7.2): the locked
/// <see cref="SystemHealthSnapshot"/> fields plus GC generation counts and
/// the optional running-audit identifier. Local technical data only — no
/// identity, machine, or account dimensions (privacy invariant).
/// </summary>
/// <param name="Timestamp">UTC emission time.</param>
/// <param name="BackendWorkingSetBytes">Backend process working-set RAM.</param>
/// <param name="ManagedHeapBytes">Managed heap size under server GC.</param>
/// <param name="GcGen0Count">Gen-0 collection count (aggregated across server-GC heaps).</param>
/// <param name="GcGen1Count">Gen-1 collection count.</param>
/// <param name="GcGen2Count">Gen-2 collection count.</param>
/// <param name="SqliteWalBytes">WAL file size; null when the external provider is active.</param>
/// <param name="AuditId">Running audit identifier, when an audit is running.</param>
public sealed record SystemHealthPayload(
    DateTimeOffset Timestamp,
    long BackendWorkingSetBytes,
    long ManagedHeapBytes,
    int GcGen0Count,
    int GcGen1Count,
    int GcGen2Count,
    long? SqliteWalBytes,
    long? AuditId);

/// <summary>
/// Captures local runtime health for the 2-second heartbeat, the on-demand
/// REST snapshot, and the pull-only metrics families. Measurement only —
/// nothing is ever transmitted beyond the loopback boundary.
/// </summary>
public interface ISystemHealthSampler
{
    /// <summary>Captures the locked REST snapshot shape.</summary>
    /// <returns>The current snapshot.</returns>
    SystemHealthSnapshot CaptureSnapshot();

    /// <summary>Captures the full heartbeat payload.</summary>
    /// <returns>The current payload.</returns>
    SystemHealthPayload CapturePayload();
}

/// <summary>
/// Default sampler: process working set, managed heap, GC generation counts,
/// and the passive WAL file stat (<see cref="IWalSizeSource"/>). The WAL
/// source is null when the external provider is active (nullable WAL bytes).
/// Also backs the locked <see cref="ISystemHealthService"/> REST surface
/// (amendment WP-05-A1): GC generation counts stay in the additive hub
/// payload, not the locked REST snapshot.
/// </summary>
public sealed class SystemHealthSampler : ISystemHealthSampler, ISystemHealthService
{
    private readonly IWalSizeSource? _walSource;
    private readonly Func<long?>? _runningAuditId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the sampler.</summary>
    /// <param name="walSource">Embedded-provider WAL stat; null for external providers.</param>
    /// <param name="runningAuditId">Optional running-audit lookup for the heartbeat.</param>
    /// <param name="timeProvider">Clock; null uses the system clock.</param>
    public SystemHealthSampler(
        IWalSizeSource? walSource = null,
        Func<long?>? runningAuditId = null,
        TimeProvider? timeProvider = null)
    {
        _walSource = walSource;
        _runningAuditId = runningAuditId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public SystemHealthSnapshot CaptureSnapshot()
    {
        var payload = CapturePayload();
        return new SystemHealthSnapshot(
            payload.BackendWorkingSetBytes,
            payload.ManagedHeapBytes,
            payload.SqliteWalBytes,
            payload.Timestamp);
    }

    /// <summary>
    /// Locked REST surface (WP-05-A1): fresh snapshot for GET /api/system/health.
    /// </summary>
    /// <returns>The current health snapshot.</returns>
    public SystemHealthSnapshot GetSnapshot() => CaptureSnapshot();

    /// <inheritdoc />
    public SystemHealthPayload CapturePayload()
    {
        long workingSet;
        using (var process = Process.GetCurrentProcess())
        {
            workingSet = process.WorkingSet64;
        }

        return new SystemHealthPayload(
            _timeProvider.GetUtcNow(),
            workingSet,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            _walSource?.TryGetWalSizeBytes(),
            _runningAuditId?.Invoke());
    }
}
