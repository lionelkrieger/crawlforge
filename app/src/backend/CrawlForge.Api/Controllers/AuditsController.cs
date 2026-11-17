using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// REST-local reaudit port (WP-06-01 reopen, defect D2): mirrors the MCP
/// lifecycle port so the REST reaudit action honors the refresh flag —
/// <c>false</c> routes to the offline analysis-only path over stored artifacts
/// (FS-CRAWL-001 §7.2, criterion #15), <c>true</c> to a full re-crawl. The Host
/// execution engine implements it; additive composition surface only — the
/// locked contracts are untouched.
/// </summary>
public interface IReAuditExecutionPort
{
    /// <summary>Reaudits a stored audit, honoring the refresh flag.</summary>
    /// <param name="auditId">Source audit identifier.</param>
    /// <param name="refresh">True re-crawls; false re-analyzes stored data offline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new audit identifier.</returns>
    Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken cancellationToken = default);
}

/// <summary>
/// Audit lifecycle and comparison (FS-CRAWL-001 §10.2): start, list, detail,
/// pause/resume/cancel transitions, reaudit, and audit comparison with the
/// additive host-mapped alignment (FS-CRAWL-002 §10.7).
/// </summary>
/// <remarks>
/// Host-phase note: the crawl engine MUST observe audit status transitions
/// written here (pause/resume/cancel); the REST layer performs validated state
/// transitions only. Audit start delegates to the locked execution contract.
/// </remarks>
[Route("api/audits")]
public sealed class AuditsController : ApiControllerBase
{
    private readonly IReadRepository<AuditDto> _auditReads;
    private readonly IWriteRepository<AuditDto> _auditWrites;
    private readonly IReadRepository<CrawlConfigurationDto> _configurations;
    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IReadRepository<ApplicationSettingsDto> _settings;
    private readonly IReadRepository<FindingDto> _findings;
    private readonly IReadRepository<UrlRecordDto> _urls;
    private readonly IAuditExecutionService _execution;
    private readonly IReAuditExecutionPort _reaudit;
    private readonly AuditComparisonReader _comparison;

    /// <summary>Creates the controller.</summary>
    /// <param name="auditReads">Audit read repository.</param>
    /// <param name="auditWrites">Audit write repository.</param>
    /// <param name="configurations">Configuration read repository.</param>
    /// <param name="projects">Project read repository.</param>
    /// <param name="settings">Settings read repository (category toggles).</param>
    /// <param name="findings">Finding read repository (host-mapped comparison).</param>
    /// <param name="urls">URL read repository (host-mapped comparison).</param>
    /// <param name="execution">Locked audit execution contract.</param>
    /// <param name="reaudit">Reaudit port honoring the offline/refresh routing.</param>
    /// <param name="comparison">Accepted comparison read model.</param>
    public AuditsController(
        IReadRepository<AuditDto> auditReads,
        IWriteRepository<AuditDto> auditWrites,
        IReadRepository<CrawlConfigurationDto> configurations,
        IReadRepository<ProjectDto> projects,
        IReadRepository<ApplicationSettingsDto> settings,
        IReadRepository<FindingDto> findings,
        IReadRepository<UrlRecordDto> urls,
        IAuditExecutionService execution,
        IReAuditExecutionPort reaudit,
        AuditComparisonReader comparison)
    {
        _auditReads = auditReads;
        _auditWrites = auditWrites;
        _configurations = configurations;
        _projects = projects;
        _settings = settings;
        _findings = findings;
        _urls = urls;
        _execution = execution;
        _reaudit = reaudit;
        _comparison = comparison;
    }

    /// <summary>Starts an audit from a configuration; returns immediately.</summary>
    /// <param name="request">Start payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Audit identifier and initial state.</returns>
    [HttpPost]
    public async Task<ObjectResult> StartAuditAsync([FromBody] AuditStartRequest request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.ConfigurationId, out var configurationId))
        {
            return ValidationError("The configuration identifier is invalid.", [new ApiFieldError("configurationId", "Must be a numeric identifier.")]);
        }

        var configuration = await _configurations.GetByIdAsync(configurationId, cancellationToken);
        if (configuration is null)
        {
            return NotFoundError($"Configuration {configurationId} does not exist.");
        }

        var project = await _projects.GetByIdAsync(configuration.ProjectId, cancellationToken);
        if (project is null || project.IsDeleted)
        {
            return NotFoundError($"Project {configuration.ProjectId} does not exist.");
        }

        if (request.ListMode == true && (request.UrlList is null || request.UrlList.Count == 0))
        {
            return ValidationError("List-mode audits require a URL list.", [new ApiFieldError("urlList", "Provide at least one URL when listMode is true.")]);
        }

        if (request.UrlList is not null)
        {
            var fieldErrors = new List<ApiFieldError>();
            for (var index = 0; index < request.UrlList.Count; index++)
            {
                if (!Uri.TryCreate(request.UrlList[index], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    fieldErrors.Add(new ApiFieldError($"urlList[{index}]", "Must be an absolute http or https URL."));
                }
            }

            if (fieldErrors.Count > 0)
            {
                return ValidationError("One or more list URLs are invalid.", fieldErrors);
            }
        }

        var overrides = request.LimitOverrides is null
            ? null
            : new CrawlLimitOverrides(request.LimitOverrides.MaxUrls, request.LimitOverrides.MaxDepth, request.LimitOverrides.MaxDurationSeconds);

        var auditId = await _execution.StartAuditAsync(configuration.ProjectId, configurationId, ScheduleTriggerType.Manual, overrides, cancellationToken);
        var audit = await _auditReads.GetByIdAsync(auditId, cancellationToken)
            ?? throw new InvalidOperationException("The execution service did not persist the audit row.");

        // Freeze the configuration snapshot; write enabledCategories only when
        // category toggles are configured (absent = all enabled, Scoring README).
        var snapshot = JsonSerializer.SerializeToNode(
            ProjectWireMapper.ToConfiguration(configuration, null), ApiJsonOptions.Default)!.AsObject();
        var enabledCategories = await ResolveEnabledCategoriesAsync(cancellationToken);
        if (enabledCategories is not null)
        {
            snapshot["enabledCategories"] = JsonSerializer.SerializeToNode(enabledCategories);
        }

        audit = audit with
        {
            ConfigurationSnapshotJson = snapshot.ToJsonString(),
            IsListMode = request.ListMode == true || request.UrlList is not null,
            UrlListJson = request.UrlList is null ? null : JsonSerializer.Serialize(request.UrlList),
        };
        await _auditWrites.UpdateAsync(audit, cancellationToken);

        return CreatedEnvelope(new AuditStartResponse(audit.Id.ToString(), AuditWireMapper.StateWire(audit.Status)));
    }

    /// <summary>Lists audits with project/state filters and pagination.</summary>
    /// <param name="projectId">Optional project filter.</param>
    /// <param name="state">Optional lifecycle state filter.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="sort">Optional sort field.</param>
    /// <param name="order">Sort direction: asc or desc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged audit summaries.</returns>
    [HttpGet]
    public async Task<ObjectResult> ListAuditsAsync(
        [FromQuery] string? projectId,
        [FromQuery] string? state,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? order = null,
        CancellationToken cancellationToken = default)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (projectId is not null)
        {
            if (!long.TryParse(projectId, out var parsedProjectId))
            {
                return ValidationError("The project identifier is invalid.", [new ApiFieldError("projectId", "Must be a numeric identifier.")]);
            }

            filters["projectId"] = parsedProjectId.ToString();
        }

        if (state is not null)
        {
            if (!ApiEnumConverter<AuditStatus>.TryParse(state, out var parsedState))
            {
                return ValidationError($"Unknown audit state '{state}'.", [new ApiFieldError("state", "Use a known lifecycle state.")]);
            }

            filters["status"] = ((int)parsedState).ToString();
        }

        if (!TryBindSort(sort, order, ["id", "startedAt", "completedAt", "state"], out var sortBy, out var descending, out var sortError))
        {
            return ValidationError(sortError!);
        }

        var repositorySort = sortBy switch
        {
            "completedAt" => "FinishedAt",
            "state" => "Status",
            _ => sortBy,
        };

        var page = await _auditReads.ListAsync(BindListQuery(offset, limit, repositorySort, descending, filters), cancellationToken);
        return OkEnvelope(page.Items.Select(AuditWireMapper.ToSummary).ToList(), PaginationOf(page));
    }

    /// <summary>Reads audit detail with derived progress fields.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Audit detail.</returns>
    [HttpGet("{id:long}")]
    public async Task<ObjectResult> GetAuditAsync(long id, CancellationToken cancellationToken)
    {
        var audit = await _auditReads.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        return OkEnvelope(AuditWireMapper.ToDetail(audit));
    }

    /// <summary>Pauses a running audit.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new state.</returns>
    [HttpPost("{id:long}/pause")]
    public Task<ObjectResult> PauseAuditAsync(long id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditStatus.Running, AuditStatus.Paused, "pause", cancellationToken);

    /// <summary>Resumes a paused audit.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new state.</returns>
    [HttpPost("{id:long}/resume")]
    public Task<ObjectResult> ResumeAuditAsync(long id, CancellationToken cancellationToken) =>
        TransitionAsync(id, AuditStatus.Paused, AuditStatus.Running, "resume", cancellationToken);

    /// <summary>Cancels a running or paused audit; terminal states are immutable.</summary>
    /// <param name="id">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new state.</returns>
    [HttpPost("{id:long}/cancel")]
    public async Task<ObjectResult> CancelAuditAsync(long id, CancellationToken cancellationToken)
    {
        var audit = await _auditReads.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        if (audit.Status is not (AuditStatus.Running or AuditStatus.Paused))
        {
            return InvalidStateError($"Audit {id} in state '{AuditWireMapper.StateWire(audit.Status)}' cannot be cancelled.");
        }

        var updated = await _auditWrites.UpdateAsync(
            audit with { Status = AuditStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, cancellationToken);
        return OkEnvelope(new AuditStateResponse(updated.Id.ToString(), AuditWireMapper.StateWire(updated.Status)));
    }

    /// <summary>Starts a reaudit linked to the source audit's configuration.</summary>
    /// <param name="id">Source audit identifier.</param>
    /// <param name="request">Optional refresh flag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New audit identifier linked to the source.</returns>
    [HttpPost("{id:long}/reaudit")]
    public async Task<ObjectResult> ReauditAsync(long id, [FromBody] ReauditRequest? request, CancellationToken cancellationToken)
    {
        var source = await _auditReads.GetByIdAsync(id, cancellationToken);
        if (source is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        // D2 (criterion #15 REST facet): route through the reaudit port so
        // refresh:false re-analyzes stored artifacts offline (zero network)
        // and refresh:true re-crawls — mirroring the MCP lifecycle wiring.
        var refresh = request?.Refresh ?? false;
        var newAuditId = await _reaudit.ReAuditAsync(id, refresh, cancellationToken);
        var newAudit = await _auditReads.GetByIdAsync(newAuditId, cancellationToken)
            ?? throw new InvalidOperationException("The execution service did not persist the audit row.");

        var snapshot = ProjectWireMapper.ParseObject(newAudit.ConfigurationSnapshotJson);
        snapshot["reaudit"] = new JsonObject
        {
            ["sourceAuditId"] = source.Id,
            ["refresh"] = request?.Refresh ?? false,
        };
        await _auditWrites.UpdateAsync(newAudit with { ConfigurationSnapshotJson = snapshot.ToJsonString() }, cancellationToken);

        return CreatedEnvelope(new ReauditResponse(newAudit.Id.ToString(), source.Id.ToString(), AuditWireMapper.StateWire(newAudit.Status)));
    }

    /// <summary>
    /// Compares two audits. With hostMapping=true, URLs align by path under the
    /// project's staging↔production host mapping (FS-CRAWL-002 §10.7).
    /// </summary>
    /// <param name="auditIdA">Base (earlier) audit identifier.</param>
    /// <param name="auditIdB">Target (later) audit identifier.</param>
    /// <param name="hostMapping">Apply host-mapped URL alignment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New/resolved findings and metric deltas.</returns>
    [HttpGet("compare")]
    public async Task<ObjectResult> CompareAuditsAsync(
        [FromQuery] string? auditIdA,
        [FromQuery] string? auditIdB,
        [FromQuery] bool hostMapping = false,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(auditIdA, out var baseId) || !long.TryParse(auditIdB, out var targetId))
        {
            return ValidationError("Both audit identifiers are required.", [new ApiFieldError("auditIdA", "Must be a numeric identifier."), new ApiFieldError("auditIdB", "Must be a numeric identifier.")]);
        }

        var baseAudit = await _auditReads.GetByIdAsync(baseId, cancellationToken);
        var targetAudit = await _auditReads.GetByIdAsync(targetId, cancellationToken);
        if (baseAudit is null || targetAudit is null)
        {
            return NotFoundError("One or both audits do not exist.");
        }

        var comparison = await _comparison.CompareAsync(baseId, targetId, cancellationToken);
        var metricDeltas = comparison.MetricDeltas
            .Select(d => new MetricDeltaWire(d.MetricKey, d.BaseValue ?? 0, d.TargetValue ?? 0, d.Delta ?? 0))
            .ToList();

        if (!hostMapping)
        {
            return OkEnvelope(new AuditCompareResponse(
                await ProjectComparisonFindingsAsync(comparison.NewFindings, targetId, cancellationToken),
                await ProjectComparisonFindingsAsync(comparison.ResolvedFindings, baseId, cancellationToken),
                metricDeltas));
        }

        var (newFindings, resolvedFindings) = await CompareHostMappedAsync(baseAudit, targetAudit, cancellationToken);
        return OkEnvelope(new AuditCompareResponse(newFindings, resolvedFindings, metricDeltas));
    }

    private async Task<ObjectResult> TransitionAsync(long id, AuditStatus required, AuditStatus next, string action, CancellationToken cancellationToken)
    {
        var audit = await _auditReads.GetByIdAsync(id, cancellationToken);
        if (audit is null)
        {
            return NotFoundError($"Audit {id} does not exist.");
        }

        if (audit.Status != required)
        {
            return InvalidStateError($"Audit {id} in state '{AuditWireMapper.StateWire(audit.Status)}' cannot {action}; expected '{AuditWireMapper.StateWire(required)}'.");
        }

        var updated = await _auditWrites.UpdateAsync(audit with { Status = next }, cancellationToken);
        return OkEnvelope(new AuditStateResponse(updated.Id.ToString(), AuditWireMapper.StateWire(updated.Status)));
    }

    private async Task<IReadOnlyList<string>?> ResolveEnabledCategoriesAsync(CancellationToken cancellationToken)
    {
        var page = await _settings.ListAsync(new ListQuery(0, 1), cancellationToken);
        if (page.Items.Count == 0)
        {
            return null;
        }

        var toggles = ProjectWireMapper.ParseObject(page.Items[0].AuditTogglesJson);
        if (toggles.Count == 0)
        {
            return null;
        }

        var enabled = toggles
            .Where(t => t.Value?.GetValue<bool>() ?? false)
            .Select(t => t.Key)
            .ToList();
        return enabled.Count == toggles.Count ? null : enabled;
    }

    private async Task<IReadOnlyList<FindingWire>> ProjectComparisonFindingsAsync(
        IReadOnlyList<FindingComparisonEntry> entries, long auditId, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var rows = await _findings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        var wire = new List<FindingWire>(entries.Count);
        foreach (var entry in entries)
        {
            var match = rows.Items.FirstOrDefault(f =>
                f.RuleKey == entry.RuleKey && f.UrlId == entry.UrlId && !f.IsSuppressed);
            wire.Add(match is not null
                ? AuditWireMapper.ToFinding(match)
                : new FindingWire(
                    string.Empty,
                    entry.RuleKey,
                    ApiEnumConverter<FindingSeverity>.ToWireValue(entry.Severity),
                    ApiEnumConverter<AuditCategory>.ToWireValue(entry.Category),
                    entry.UrlId?.ToString(),
                    entry.RuleKey.Replace('.', ' ').Replace('-', ' '),
                    null));
        }

        return wire;
    }

    private async Task<(IReadOnlyList<FindingWire> New, IReadOnlyList<FindingWire> Resolved)> CompareHostMappedAsync(
        AuditDto baseAudit, AuditDto targetAudit, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(baseAudit.ProjectId, cancellationToken);
        var mappings = ParseHostMappings(project?.HostMappingJson);

        var baseRows = await LoadFindingsWithUrlsAsync(baseAudit.Id, cancellationToken);
        var targetRows = await LoadFindingsWithUrlsAsync(targetAudit.Id, cancellationToken);

        var baseKeys = baseRows.Select(r => (r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings))).ToHashSet();
        var targetKeys = targetRows.Select(r => (r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings))).ToHashSet();

        var newFindings = targetRows
            .Where(r => !baseKeys.Contains((r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings))))
            .GroupBy(r => (r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings)))
            .Select(g => AuditWireMapper.ToFinding(g.First().Finding))
            .ToList();
        var resolvedFindings = baseRows
            .Where(r => !targetKeys.Contains((r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings))))
            .GroupBy(r => (r.Finding.RuleKey, NormalizeUrl(r.CanonicalUrl, mappings)))
            .Select(g => AuditWireMapper.ToFinding(g.First().Finding))
            .ToList();

        return (newFindings, resolvedFindings);
    }

    private async Task<IReadOnlyList<(FindingDto Finding, string? CanonicalUrl)>> LoadFindingsWithUrlsAsync(long auditId, CancellationToken cancellationToken)
    {
        var findings = await _findings.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        var urls = await _urls.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["auditId"] = auditId.ToString() }), cancellationToken);
        var urlMap = urls.Items.ToDictionary(u => u.Id, u => u.CanonicalUrl);
        return findings.Items
            .Where(f => !f.IsSuppressed)
            .Select(f => (f, f.UrlId is { } urlId && urlMap.TryGetValue(urlId, out var url) ? url : null))
            .ToList();
    }

    private static IReadOnlyList<ProjectWireMapper.HostMappingEntry> ParseHostMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProjectWireMapper.HostMappingEntry>>(json, ApiJsonOptions.Default) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeUrl(string? url, IReadOnlyList<ProjectWireMapper.HostMappingEntry> mappings)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url ?? string.Empty;
        }

        var host = mappings.FirstOrDefault(m => string.Equals(m.FromHost, uri.Host, StringComparison.OrdinalIgnoreCase))?.ToHost ?? uri.Host;
        return host + uri.PathAndQuery;
    }
}
