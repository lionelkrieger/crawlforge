using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Mcp;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Configurable audit-scoped lifecycle port double: records invocations and
/// returns configured snapshots.
/// </summary>
public sealed class FakeAuditLifecyclePort : IAuditLifecyclePort
{
    /// <summary>Recorded pause calls.</summary>
    public List<long> PauseCalls { get; } = [];

    /// <summary>Recorded resume calls.</summary>
    public List<long> ResumeCalls { get; } = [];

    /// <summary>Recorded cancel calls.</summary>
    public List<long> CancelCalls { get; } = [];

    /// <summary>Recorded re-audit calls (auditId, refresh).</summary>
    public List<(long AuditId, bool Refresh)> ReAuditCalls { get; } = [];

    /// <summary>Recorded progress calls.</summary>
    public List<long> ProgressCalls { get; } = [];

    /// <summary>Snapshot returned by pause/resume/cancel.</summary>
    public AuditLifecycleSnapshot Snapshot { get; set; } =
        new(1, AuditStatus.Paused, AuditPhase.Crawling);

    /// <summary>New audit id returned by re-audit.</summary>
    public long ReAuditResultId { get; set; } = 100;

    /// <summary>Progress snapshot returned by get-audit-progress.</summary>
    public AuditProgressSnapshot Progress { get; set; } =
        new(1, AuditStatus.Running, AuditPhase.Crawling, 10, 8, 6, 2, 60.0);

    /// <inheritdoc/>
    public Task<AuditLifecycleSnapshot> PauseAsync(long auditId, CancellationToken cancellationToken = default)
    {
        PauseCalls.Add(auditId);
        return Task.FromResult(Snapshot with { AuditId = auditId, Status = AuditStatus.Paused });
    }

    /// <inheritdoc/>
    public Task<AuditLifecycleSnapshot> ResumeAsync(long auditId, CancellationToken cancellationToken = default)
    {
        ResumeCalls.Add(auditId);
        return Task.FromResult(Snapshot with { AuditId = auditId, Status = AuditStatus.Running });
    }

    /// <inheritdoc/>
    public Task<AuditLifecycleSnapshot> CancelAsync(long auditId, CancellationToken cancellationToken = default)
    {
        CancelCalls.Add(auditId);
        return Task.FromResult(Snapshot with { AuditId = auditId, Status = AuditStatus.Cancelled });
    }

    /// <inheritdoc/>
    public Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken cancellationToken = default)
    {
        ReAuditCalls.Add((auditId, refresh));
        return Task.FromResult(ReAuditResultId);
    }

    /// <inheritdoc/>
    public Task<AuditProgressSnapshot> GetProgressAsync(long auditId, CancellationToken cancellationToken = default)
    {
        ProgressCalls.Add(auditId);
        return Task.FromResult(Progress with { AuditId = auditId });
    }
}

/// <summary>
/// Audit-start double that creates a real audit row through the repository,
/// mirroring the locked execution service contract.
/// </summary>
public sealed class FakeAuditExecutionService : IAuditExecutionService
{
    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IReadRepository<CrawlConfigurationDto> _configurations;
    private readonly IWriteRepository<AuditDto> _audits;

    /// <summary>Creates the double.</summary>
    /// <param name="projects">Project repository.</param>
    /// <param name="configurations">Configuration repository.</param>
    /// <param name="audits">Audit repository.</param>
    public FakeAuditExecutionService(
        IReadRepository<ProjectDto> projects,
        IReadRepository<CrawlConfigurationDto> configurations,
        IWriteRepository<AuditDto> audits)
    {
        _projects = projects;
        _configurations = configurations;
        _audits = audits;
    }

    /// <summary>Recorded start calls.</summary>
    public List<(long ProjectId, long ConfigurationId, ScheduleTriggerType Trigger)> StartCalls { get; } = [];

    /// <inheritdoc/>
    public async Task<long> StartAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType trigger,
        CrawlLimitOverrides? limitOverrides = null,
        CancellationToken cancellationToken = default)
    {
        StartCalls.Add((projectId, configurationId, trigger));
        _ = await _projects.GetByIdAsync(projectId, cancellationToken);
        _ = await _configurations.GetByIdAsync(configurationId, cancellationToken);
        var audit = await _audits.CreateAsync(new AuditDto(
            0, projectId, configurationId, "{}", AuditStatus.Queued, AuditPhase.NotStarted,
            null, null, "{}", "mcp-test", false), cancellationToken);
        return audit.Id;
    }
}

/// <summary>
/// Schedule service double returning a configured audit id.
/// </summary>
public sealed class FakeScheduledAuditService : IScheduledAuditService
{
    /// <summary>Audit id returned by run-now.</summary>
    public long RunNowResultId { get; set; } = 42;

    /// <summary>Recorded run-now calls.</summary>
    public List<long> RunNowCalls { get; } = [];

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<long> RunNowAsync(long scheduleDefinitionId, CancellationToken cancellationToken = default)
    {
        RunNowCalls.Add(scheduleDefinitionId);
        return Task.FromResult(RunNowResultId);
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetNextDueAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTimeOffset?>(null);
}

/// <summary>
/// Single-page analyzer double returning a configured result.
/// </summary>
public sealed class FakeSinglePageAnalyzer : ISinglePageAnalyzer
{
    /// <summary>Recorded requests.</summary>
    public List<SinglePageAnalysisRequest> Requests { get; } = [];

    /// <summary>Result to return.</summary>
    public SinglePageAnalysisResult Result { get; set; } = new(
        [new FindingDraft("test.rule", AuditCategory.Content, FindingSeverity.Warning, null, "{}", null)],
        [new ElementInventoryItem("title", "\"Example\"", 0)],
        null,
        null);

    /// <inheritdoc/>
    public Task<SinglePageAnalysisResult> AnalyzeAsync(SinglePageAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}
