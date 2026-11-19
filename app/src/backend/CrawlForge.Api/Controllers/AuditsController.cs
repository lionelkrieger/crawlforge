using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Api.Hubs;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Api;
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
/// REST-local lifecycle execution port (WP-09-06 D2): mirrors the MCP
/// <c>IAuditLifecyclePort</c> so REST pause/resume/cancel reach the engine's
/// in-memory run state, not just the persisted status row. The Host execution
/// engine implements the equivalent lifecycle surface; additive composition
/// only — locked contracts untouched. Contract for implementers:
/// <list type="bullet">
/// <item>Pause: persist Paused, signal the run's pause gate; the run loop
/// checkpoints (FrontierSnapshot + AimdStateJson + CountersJson) on observation.</item>
/// <item>Resume: persist Running and release the gate; when the run is NOT in
/// memory (crash recovery — the previous process died and startup marked the
/// orphan Paused), rehydrate a new run from the latest checkpoint. This is the
/// path REST status writes alone cannot perform.</item>
/// <item>Cancel: persist Cancelled, release the gate so the loop exits and
/// finalizes.</item>
/// <item>Throw <see cref="InvalidOperationException"/> when the current state
/// forbids the transition (REST maps it to 409 invalid-state).</item>
/// </list>
/// Until Host registers an implementation, the controller falls back to
/// validated status writes (the engine loop polls persisted status, so
/// in-process pause/cancel still work; post-restart resume needs this port).
/// </summary>
public interface IAuditLifecycleExecutionPort
{
    /// <summary>Pauses a running audit (checkpoint on observation).</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted transition.</returns>
    Task<AuditLifecycleTransition> PauseAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused audit, rehydrating from checkpoint after a restart.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted transition.</returns>
    Task<AuditLifecycleTransition> ResumeAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a running or paused audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted transition.</returns>
    Task<AuditLifecycleTransition> CancelAsync(long auditId, CancellationToken cancellationToken = default);
}

/// <summary>Result of one engine-executed lifecycle transition.</summary>
/// <param name="AuditId">Audit identifier.</param>
/// <param name="Status">Persisted lifecycle status.</param>
/// <param name="Phase">Pipeline phase at transition time.</param>
public sealed record AuditLifecycleTransition(long AuditId, AuditStatus Status, AuditPhase Phase);

/// <summary>
/// REST-local list-mode start port (WP-09-06 G10): the locked
/// <see cref="IAuditExecutionService"/> creates the audit row and starts the
/// engine loop in one call, so a post-start <c>UrlListJson</c> update races the
/// engine's seed resolution (<c>ListSeedResolver.ResolveForAuditAsync</c>).
/// Contract for implementers: persist <c>urlListJson</c>/<c>isListMode</c>
/// ATOMICALLY with the audit-row creation — BEFORE the engine loop can observe
/// the row (persist-before-start). On a failure AFTER the row was created,
/// throw <see cref="AuditStartFailedException"/> carrying the new audit id so
/// the REST surface can publish audit-failed with error detail.
/// </summary>
public interface IListAuditStartPort
{
    /// <summary>Starts a list-mode audit with the URL list persisted before engine observation.</summary>
    /// <param name="projectId">Owning project identifier.</param>
    /// <param name="configurationId">Configuration to snapshot.</param>
    /// <param name="trigger">What fired this run.</param>
    /// <param name="limitOverrides">Optional per-audit limit overrides.</param>
    /// <param name="urlListJson">JSON array of list URLs; null when only the flag is set.</param>
    /// <param name="isListMode">Persisted list-mode flag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identifier of the newly created audit.</returns>
    Task<long> StartListAuditAsync(
        long projectId,
        long configurationId,
        ScheduleTriggerType trigger,
        CrawlLimitOverrides? limitOverrides,
        string? urlListJson,
        bool isListMode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Signals an audit-start failure AFTER the audit row was created (G10 port
/// contract): the REST surface publishes audit-failed with the carried id and
/// error detail, then returns the uniform 500 envelope.
/// </summary>
public sealed class AuditStartFailedException : Exception
{
    /// <summary>Creates the failure signal.</summary>
    /// <param name="auditId">Identifier of the created audit row.</param>
    /// <param name="message">Failure detail for the audit-failed event.</param>
    public AuditStartFailedException(long auditId, string message)
        : base(message)
    {
        AuditId = auditId;
    }

    /// <summary>Identifier of the created audit row.</summary>
    public long AuditId { get; }
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
    private readonly IAuditLifecycleExecutionPort? _lifecyclePort;
    private readonly IListAuditStartPort? _listStartPort;
    private readonly IRealtimeHub _hub;

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
    /// <param name="lifecyclePorts">Engine lifecycle port (WP-09-06 D2); empty until Host wires it — REST falls back to validated status writes.</param>
    /// <param name="listStartPorts">List-mode start port (WP-09-06 G10); empty until Host wires it — REST falls back to start-then-persist.</param>
    /// <param name="hub">Realtime hub for REST-side stage-changed / audit-failed publication.</param>
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
        AuditComparisonReader comparison,
        IEnumerable<IAuditLifecycleExecutionPort> lifecyclePorts,
        IEnumerable<IListAuditStartPort> listStartPorts,
        IRealtimeHub hub)
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
        _lifecyclePort = lifecyclePorts.FirstOrDefault();
        _listStartPort = listStartPorts.FirstOrDefault();
        _hub = hub;
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

        var urlListJson = request.UrlList is null ? null : JsonSerializer.Serialize(request.UrlList);
        var isListMode = request.ListMode == true || request.UrlList is not null;

        long auditId;
        try
        {
            // G10 (WP-09-06): persist-before-start. When the list-start port is
            // wired, UrlListJson/IsListMode are persisted ATOMICALLY with the
            // audit-row creation, before the engine loop can resolve list seeds
            // (ListSeedResolver.ResolveForAuditAsync). The locked start contract
            // creates the row internally, so a post-start update races the engine;
            // the fallback below keeps pre-wiring behavior (handoff documents the
            // Host registration that closes the race).
            if (isListMode && _listStartPort is not null)
            {
                auditId = await _listStartPort.StartListAuditAsync(
                    configuration.ProjectId, configurationId, ScheduleTriggerType.Manual,
                    overrides, urlListJson, isListMode, cancellationToken);
            }
            else
            {
                auditId = await _execution.StartAuditAsync(configuration.ProjectId, configurationId, ScheduleTriggerType.Manual, overrides, cancellationToken);
            }
        }
        catch (AuditStartFailedException ex)
        {
            // The start pipeline failed AFTER the row was created: publish
            // audit-failed with error detail (FS-CRAWL-001 §10.3) so the shell
            // sees the failure even though no engine loop will finalize it.
            await PublishAuditFailedAsync(ex.AuditId, "start-failed", ex.Message, cancellationToken);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ErrorEnvelope(ApiErrorCodes.Internal, ex.Message));
        }

        var audit = await _auditReads.GetByIdAsync(auditId, cancellationToken)
            ?? throw new InvalidOperationException("The execution service did not persist the audit row.");

        // Freeze the configuration snapshot; write enabledCategories only when
        // category toggles are configured (absent = all enabled, Scoring README).
        // WP-09-06 (WP-09-07 §3): the config-level enabledCategories document
        // member wins over the app-settings AuditTogglesJson fallback.
        // WP-09-11 (G-REAUDIT-SNAPSHOT): the snapshot stays in the STORAGE DTO
        // shape the engine wrote at start (the engine deserializes it as
        // CrawlConfigurationDto); merge enabledCategories into that document
        // instead of overwriting it with the wire projection.
        var snapshot = ParseSnapshotObject(audit.ConfigurationSnapshotJson)
            ?? JsonSerializer.SerializeToNode(configuration, ApiJsonOptions.Default)!.AsObject();
        var enabledCategories = await ResolveEnabledCategoriesAsync(configuration, cancellationToken);
        if (enabledCategories is not null)
        {
            snapshot["enabledCategories"] = JsonSerializer.SerializeToNode(enabledCategories);
        }
        else
        {
            // Keep the frozen convention: absent = all enabled (the wire
            // projection carries null; the snapshot omits the member).
            snapshot.Remove("enabledCategories");
        }

        audit = audit with
        {
            ConfigurationSnapshotJson = snapshot.ToJsonString(),
            IsListMode = isListMode,
            UrlListJson = urlListJson,
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

        // WP-09-06 D2: delegate to the engine when wired so the in-memory run
        // exits its loop and finalizes (stage-changed emitted engine-side).
        if (_lifecyclePort is not null)
        {
            try
            {
                var transition = await _lifecyclePort.CancelAsync(id, cancellationToken);
                return OkEnvelope(new AuditStateResponse(transition.AuditId.ToString(), AuditWireMapper.StateWire(transition.Status)));
            }
            catch (InvalidOperationException)
            {
                return InvalidStateError($"Audit {id} in state '{AuditWireMapper.StateWire(audit.Status)}' cannot be cancelled.");
            }
        }

        var updated = await _auditWrites.UpdateAsync(
            audit with { Status = AuditStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow }, cancellationToken);
        await PublishStageChangedAsync(id, audit.Status, AuditStatus.Cancelled, cancellationToken);
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

        // WP-09-06 D2: when the engine lifecycle port is wired, delegate so the
        // transition reaches the in-memory run (pause gate / cancel) and resume
        // rehydrates from the latest checkpoint after a crash. The engine owns
        // stage-changed emission on this path (handoff recipe).
        if (_lifecyclePort is not null)
        {
            try
            {
                var transition = next switch
                {
                    AuditStatus.Paused => await _lifecyclePort.PauseAsync(id, cancellationToken),
                    AuditStatus.Running => await _lifecyclePort.ResumeAsync(id, cancellationToken),
                    _ => await _lifecyclePort.CancelAsync(id, cancellationToken),
                };
                return OkEnvelope(new AuditStateResponse(transition.AuditId.ToString(), AuditWireMapper.StateWire(transition.Status)));
            }
            catch (InvalidOperationException)
            {
                // The engine re-validates under its own state; a concurrent
                // transition wins and REST reports the conflict.
                return InvalidStateError($"Audit {id} in state '{AuditWireMapper.StateWire(audit.Status)}' cannot {action}; expected '{AuditWireMapper.StateWire(required)}'.");
            }
        }

        // Fallback (pre-wiring): validated status write. The engine loop polls
        // persisted status (ObserveStatusAsync), so in-process pause/cancel are
        // observed and checkpointed; REST publishes stage-changed itself here.
        var updated = await _auditWrites.UpdateAsync(audit with { Status = next }, cancellationToken);
        await PublishStageChangedAsync(id, audit.Status, next, cancellationToken);
        return OkEnvelope(new AuditStateResponse(updated.Id.ToString(), AuditWireMapper.StateWire(updated.Status)));
    }

    /// <summary>
    /// Publishes stage-changed for a REST-executed transition (FS-CRAWL-001
    /// §10.3: on every lifecycle transition). Only the fallback path emits here;
    /// when the lifecycle port is wired the engine owns emission (single
    /// emission per transition — handoff recipe).
    /// </summary>
    private Task PublishStageChangedAsync(long auditId, AuditStatus previousState, AuditStatus newState, CancellationToken cancellationToken) =>
        _hub.PublishAsync(new HubEvent(
            HubEventCatalog.StageChanged,
            JsonSerializer.Serialize(new
            {
                auditId,
                previousState = AuditWireMapper.StateWire(previousState),
                newState = AuditWireMapper.StateWire(newState),
            })), cancellationToken);

    /// <summary>
    /// Publishes audit-failed with error detail (FS-CRAWL-001 §10.3; frozen
    /// frontend AuditFailedPayload: auditId/errorCode/message). Used by Api-side
    /// failure paths where no engine loop will finalize the run; engine-side
    /// run failures publish at FinalizeAsync (already implemented).
    /// </summary>
    private Task PublishAuditFailedAsync(long auditId, string errorCode, string message, CancellationToken cancellationToken) =>
        _hub.PublishAsync(new HubEvent(
            HubEventCatalog.AuditFailed,
            JsonSerializer.Serialize(new
            {
                auditId,
                errorCode,
                message,
            })), cancellationToken);

    private async Task<IReadOnlyList<string>?> ResolveEnabledCategoriesAsync(CrawlConfigurationDto configuration, CancellationToken cancellationToken)
    {
        // Config-level preference (WP-09-07 §3): ScopeRulesJson["enabledCategories"],
        // PascalCase enum names, case-insensitive. A full set is equivalent to null
        // (all enabled); an absent/empty member falls through to app settings.
        var behavior = ProjectWireMapper.ParseObject(configuration.ScopeRulesJson);
        if (behavior["enabledCategories"] is JsonArray categoryNodes)
        {
            var names = new List<string>();
            foreach (var node in categoryNodes)
            {
                if (node is JsonValue value
                    && value.TryGetValue<string>(out var name)
                    && Enum.TryParse<AuditCategory>(name, ignoreCase: true, out var category))
                {
                    var canonical = category.ToString();
                    if (!names.Contains(canonical))
                    {
                        names.Add(canonical);
                    }
                }
            }

            if (names.Count > 0)
            {
                return names.Count == Enum.GetValues<AuditCategory>().Length ? null : names;
            }
        }

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

    /// <summary>
    /// Parses the engine-written storage snapshot (DTO shape); null when absent,
    /// empty (test-double placeholder), or malformed so the caller falls back to
    /// a fresh DTO serialization.
    /// </summary>
    private static JsonObject? ParseSnapshotObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject { Count: > 0 } obj ? obj : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
