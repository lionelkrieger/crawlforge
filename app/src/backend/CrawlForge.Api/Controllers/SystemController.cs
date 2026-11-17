using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Wire system-health snapshot (frozen frontend SystemHealthSnapshot shape):
/// the locked <see cref="SystemHealthSnapshot"/> projected with local GC counters
/// and the running-audit reference. Local technical data only.
/// </summary>
/// <param name="Timestamp">Snapshot timestamp (UTC).</param>
/// <param name="BackendWorkingSetBytes">Process working set in bytes.</param>
/// <param name="ManagedHeapBytes">Managed GC heap size in bytes.</param>
/// <param name="GcGen0Count">Gen-0 collection count.</param>
/// <param name="GcGen1Count">Gen-1 collection count.</param>
/// <param name="GcGen2Count">Gen-2 collection count.</param>
/// <param name="SqliteWalBytes">Write-ahead log size, when the embedded provider is active.</param>
/// <param name="AuditId">Running audit reference, when any.</param>
public sealed record SystemHealthWire(
    DateTimeOffset Timestamp,
    long BackendWorkingSetBytes,
    long ManagedHeapBytes,
    int GcGen0Count,
    int GcGen1Count,
    int GcGen2Count,
    long? SqliteWalBytes,
    string? AuditId);

/// <summary>POST /api/system/compact — before/after snapshots and actions performed.</summary>
/// <param name="Before">Health snapshot before compacting.</param>
/// <param name="After">Health snapshot after compacting.</param>
/// <param name="ActionsPerformed">Compact actions performed.</param>
public sealed record SystemCompactResponse(SystemHealthWire Before, SystemHealthWire After, IReadOnlyList<string> ActionsPerformed);

/// <summary>
/// Additive system surfaces (FS-CRAWL-002 §7.2): on-demand health snapshot and
/// the compact action. Both delegate to the WP-05-02 service surface through the
/// locked WP-05-A1 contracts; this controller implements no compact logic itself.
/// </summary>
[Route("api/system")]
public sealed class SystemController : ApiControllerBase
{
    private readonly ISystemHealthService _health;
    private readonly ICompactService _compact;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the controller.</summary>
    /// <param name="health">System health sampling surface.</param>
    /// <param name="compact">Compact action surface.</param>
    /// <param name="audits">Audit read repository (running-audit reference).</param>
    public SystemController(ISystemHealthService health, ICompactService compact, IReadRepository<AuditDto> audits)
    {
        _health = health;
        _compact = compact;
        _audits = audits;
    }

    /// <summary>Returns the current system-health snapshot.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health snapshot envelope.</returns>
    [HttpGet("health")]
    public async Task<ObjectResult> GetHealthAsync(CancellationToken cancellationToken)
    {
        var snapshot = _health.GetSnapshot();
        var runningAuditId = await FindRunningAuditIdAsync(cancellationToken);
        return OkEnvelope(ToWire(snapshot, runningAuditId));
    }

    /// <summary>
    /// Runs the compact action. Guard refusals (rate limit, running audit) surface
    /// as conflict responses per the error contract (§10.4).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Before/after snapshots and actions performed.</returns>
    [HttpPost("compact")]
    public async Task<ObjectResult> CompactAsync(CancellationToken cancellationToken)
    {
        CompactResult result;
        try
        {
            result = await _compact.CompactAsync(cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            return ConflictError(error.Message);
        }

        var runningAuditId = await FindRunningAuditIdAsync(cancellationToken);
        return OkEnvelope(new SystemCompactResponse(
            ToWire(result.Before, runningAuditId),
            ToWire(result.After, runningAuditId),
            result.ActionsPerformed ?? []));
    }

    private static SystemHealthWire ToWire(SystemHealthSnapshot snapshot, long? runningAuditId) =>
        new(
            snapshot.Timestamp,
            snapshot.WorkingSetBytes,
            snapshot.GcHeapBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            snapshot.WalSizeBytes,
            runningAuditId?.ToString());

    private async Task<long?> FindRunningAuditIdAsync(CancellationToken cancellationToken)
    {
        var page = await _audits.ListAsync(new(
            Offset: 0,
            Limit: 1,
            Filters: new Dictionary<string, string> { ["status"] = ((int)AuditStatus.Running).ToString() }),
            cancellationToken);
        return page.Items.Count > 0 ? page.Items[0].Id : null;
    }
}
