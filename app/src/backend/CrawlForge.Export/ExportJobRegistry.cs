using System.Collections.Concurrent;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Export;

/// <summary>
/// One export job's runtime state.
/// </summary>
/// <param name="Id">Job identifier.</param>
/// <param name="AuditId">Exported audit.</param>
/// <param name="Format">Output format.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
/// <param name="FilePath">Local file path when completed.</param>
/// <param name="Datasets">Selected dataset keys.</param>
/// <param name="FailureMessage">Failure detail when failed.</param>
public sealed record ExportJobRecord(
    long Id,
    long AuditId,
    ExportFormat Format,
    ExportJobStatus Status,
    DateTimeOffset CreatedAt,
    string? FilePath,
    IReadOnlyList<string> Datasets,
    string? FailureMessage)
{
    /// <summary>Projects the record to the locked DTO.</summary>
    /// <returns>The export job DTO.</returns>
    public ExportJobDto ToDto() => new(Id, AuditId, Format, Status, CreatedAt, FilePath, Datasets);
}

/// <summary>
/// In-memory export job registry (WP-05-03 design decision, orchestrator
/// approved). <b>Restart semantics:</b> jobs are runtime state — in-flight
/// jobs and the delivery ledger are lost on process restart; generated export
/// files remain on disk and destination rows are durable in the database.
/// Durable job/delivery history is a deferred schema-amendment candidate.
/// The scheduler polls <c>GetExportAsync</c> within one process, so no
/// cross-restart job continuity is required by the locked contracts.
/// </summary>
public sealed class ExportJobRegistry
{
    private readonly ConcurrentDictionary<long, ExportJobRecord> _jobs = new();
    private long _nextId;

    /// <summary>Creates a queued job and returns its record.</summary>
    /// <param name="auditId">Audit to export.</param>
    /// <param name="format">Output format.</param>
    /// <param name="datasets">Selected dataset keys.</param>
    /// <returns>The queued job record.</returns>
    public ExportJobRecord Create(long auditId, ExportFormat format, IReadOnlyList<string> datasets)
    {
        var id = Interlocked.Increment(ref _nextId);
        var record = new ExportJobRecord(id, auditId, format, ExportJobStatus.Queued, DateTimeOffset.UtcNow, null, datasets, null);
        _jobs[id] = record;
        return record;
    }

    /// <summary>Reads a job record.</summary>
    /// <param name="jobId">Job identifier.</param>
    /// <returns>The record, or null when absent.</returns>
    public ExportJobRecord? Get(long jobId) => _jobs.TryGetValue(jobId, out var record) ? record : null;

    /// <summary>Replaces a job record (status transitions).</summary>
    /// <param name="record">Updated record.</param>
    public void Set(ExportJobRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _jobs[record.Id] = record;
    }
}
