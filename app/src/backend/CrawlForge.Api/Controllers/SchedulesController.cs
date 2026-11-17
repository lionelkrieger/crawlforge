using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Crawler.Scheduling;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Scheduled audits (FS-CRAWL-001 §10.2, §6). Recurrence expressions use the
/// locked local grammar (interval:/calendar:) carried under the wire field
/// name <c>cron</c>; validation and next-due computation delegate to the locked
/// <see cref="RecurrenceRule"/>. Display names and timestamps live in the
/// API-surface metadata sidecar (the locked schema has no such columns).
/// Run-now delegates to the locked scheduler; history rows come from
/// schedule_runs. Additive GET /api/schedules lists across projects.
/// </summary>
[Route("api")]
public sealed class SchedulesController : ApiControllerBase
{
    private readonly IReadRepository<ScheduleDefinitionDto> _reads;
    private readonly IWriteRepository<ScheduleDefinitionDto> _writes;
    private readonly IReadRepository<ScheduleRunDto> _runs;
    private readonly IWriteRepository<ScheduleRunDto> _runWrites;
    private readonly IReadRepository<ProjectDto> _projects;
    private readonly IReadRepository<CrawlConfigurationDto> _configurations;
    private readonly IScheduledAuditService _scheduler;
    private readonly ApiSurfaceMetadataStore _metadata;

    /// <summary>Creates the controller.</summary>
    /// <param name="reads">Schedule read repository.</param>
    /// <param name="writes">Schedule write repository.</param>
    /// <param name="runs">Run-history read repository.</param>
    /// <param name="runWrites">Run-history write repository.</param>
    /// <param name="projects">Project read repository.</param>
    /// <param name="configurations">Configuration read repository.</param>
    /// <param name="scheduler">Locked scheduler service.</param>
    /// <param name="settingsReads">Settings read repository (metadata sidecar).</param>
    /// <param name="settingsWrites">Settings write repository (metadata sidecar).</param>
    public SchedulesController(
        IReadRepository<ScheduleDefinitionDto> reads,
        IWriteRepository<ScheduleDefinitionDto> writes,
        IReadRepository<ScheduleRunDto> runs,
        IWriteRepository<ScheduleRunDto> runWrites,
        IReadRepository<ProjectDto> projects,
        IReadRepository<CrawlConfigurationDto> configurations,
        IScheduledAuditService scheduler,
        IReadRepository<ApplicationSettingsDto> settingsReads,
        IWriteRepository<ApplicationSettingsDto> settingsWrites)
    {
        _reads = reads;
        _writes = writes;
        _runs = runs;
        _runWrites = runWrites;
        _projects = projects;
        _configurations = configurations;
        _scheduler = scheduler;
        _metadata = new ApiSurfaceMetadataStore(settingsReads, settingsWrites);
    }

    /// <summary>Lists schedules of one project.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged schedules.</returns>
    [HttpGet("projects/{id:long}/schedules")]
    public async Task<ObjectResult> ListByProjectAsync(long id, [FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (await _projects.GetByIdAsync(id, cancellationToken) is not { IsDeleted: false })
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        var root = await _metadata.LoadAsync(cancellationToken);
        var page = await _reads.ListAsync(BindListQuery(offset, limit, null, false, new Dictionary<string, string> { ["projectId"] = id.ToString() }), cancellationToken);
        var items = new List<ScheduleWire>();
        foreach (var schedule in page.Items)
        {
            items.Add(await ToWireAsync(schedule, root, cancellationToken));
        }

        return OkEnvelope(items, PaginationOf(page));
    }

    /// <summary>Lists schedules across projects (additive route).</summary>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged schedules.</returns>
    [HttpGet("schedules")]
    public async Task<ObjectResult> ListAllAsync([FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var root = await _metadata.LoadAsync(cancellationToken);
        var page = await _reads.ListAsync(BindListQuery(offset, limit, null, false), cancellationToken);
        var items = new List<ScheduleWire>();
        foreach (var schedule in page.Items)
        {
            items.Add(await ToWireAsync(schedule, root, cancellationToken));
        }

        return OkEnvelope(items, PaginationOf(page));
    }

    /// <summary>Creates a schedule for a project.</summary>
    /// <param name="id">Project identifier.</param>
    /// <param name="request">Schedule fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created schedule.</returns>
    [HttpPost("projects/{id:long}/schedules")]
    public async Task<ObjectResult> CreateAsync(long id, [FromBody] ScheduleCreateRequest request, CancellationToken cancellationToken)
    {
        if (await _projects.GetByIdAsync(id, cancellationToken) is not { IsDeleted: false })
        {
            return NotFoundError($"Project {id} does not exist.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError("A schedule name is required.", [new ApiFieldError("name", "Provide a name.")]);
        }

        if (!long.TryParse(request.ConfigurationId, out var configurationId)
            || await _configurations.GetByIdAsync(configurationId, cancellationToken) is not { } configuration
            || configuration.ProjectId != id)
        {
            return NotFoundError($"Configuration {request.ConfigurationId} does not exist in project {id}.");
        }

        if (!TryValidateCron(request.Cron, out var recurrence, out var cronError))
        {
            return ValidationError(cronError!, [new ApiFieldError("cron", "Use interval:<timespan> or calendar:HH:mm[;days=...].")]);
        }

        if (!TryBuildActions(request.CompletionActions, out var actionsJson, out var actionsError))
        {
            return ValidationError(actionsError!, [new ApiFieldError("completionActions", "Provide valid completion actions.")]);
        }

        var enabled = request.Enabled ?? true;
        var nextDue = enabled ? NextDue(recurrence) : null;
        var created = new ScheduleDefinitionDto(0, id, configurationId, recurrence, enabled, null, actionsJson, nextDue);
        var newId = (await _writes.CreateAsync(created, cancellationToken)).Id;

        var now = DateTimeOffset.UtcNow;
        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.WriteEntry(root, "schedules", newId.ToString(), new ApiSurfaceMetadataStore.Entry(request.Name.Trim(), now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return CreatedEnvelope(await ToWireAsync(created with { Id = newId }, root, cancellationToken));
    }

    /// <summary>Reads one schedule.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Schedule record.</returns>
    [HttpGet("schedules/{id:long}")]
    public async Task<ObjectResult> GetAsync(long id, CancellationToken cancellationToken)
    {
        var schedule = await _reads.GetByIdAsync(id, cancellationToken);
        if (schedule is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        var root = await _metadata.LoadAsync(cancellationToken);
        return OkEnvelope(await ToWireAsync(schedule, root, cancellationToken));
    }

    /// <summary>Updates a schedule (subset merge).</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="request">Fields to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated schedule.</returns>
    [HttpPut("schedules/{id:long}")]
    public async Task<ObjectResult> UpdateAsync(long id, [FromBody] ScheduleUpdateRequest request, CancellationToken cancellationToken)
    {
        var existing = await _reads.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        var configurationId = existing.ConfigurationId;
        if (request.ConfigurationId is not null)
        {
            if (!long.TryParse(request.ConfigurationId, out configurationId)
                || await _configurations.GetByIdAsync(configurationId, cancellationToken) is not { } configuration
                || configuration.ProjectId != existing.ProjectId)
            {
                return NotFoundError($"Configuration {request.ConfigurationId} does not exist in project {existing.ProjectId}.");
            }
        }

        var recurrence = existing.RecurrenceRule;
        if (request.Cron is not null)
        {
            if (!TryValidateCron(request.Cron, out recurrence, out var cronError))
            {
                return ValidationError(cronError!, [new ApiFieldError("cron", "Use interval:<timespan> or calendar:HH:mm[;days=...].")]);
            }
        }

        var actionsJson = existing.CompletionActionsJson;
        if (request.CompletionActions is not null)
        {
            if (!TryBuildActions(request.CompletionActions, out actionsJson, out var actionsError))
            {
                return ValidationError(actionsError!, [new ApiFieldError("completionActions", "Provide valid completion actions.")]);
            }
        }

        var enabled = request.Enabled ?? existing.IsEnabled;
        var nextDue = enabled ? NextDue(recurrence) : null;
        var updated = existing with
        {
            ConfigurationId = configurationId,
            RecurrenceRule = recurrence,
            IsEnabled = enabled,
            CompletionActionsJson = actionsJson,
            NextDueAt = nextDue,
        };
        await _writes.UpdateAsync(updated, cancellationToken);

        var root = await _metadata.LoadAsync(cancellationToken);
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "schedules", id.ToString());
        var now = DateTimeOffset.UtcNow;
        ApiSurfaceMetadataStore.WriteEntry(root, "schedules", id.ToString(), new ApiSurfaceMetadataStore.Entry(request.Name?.Trim() ?? entry?.Name, entry?.CreatedAt ?? now, now));
        await _metadata.SaveAsync(root, cancellationToken);

        return OkEnvelope(await ToWireAsync(updated, root, cancellationToken));
    }

    /// <summary>Deletes a schedule and its run history.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion confirmation.</returns>
    [HttpDelete("schedules/{id:long}")]
    public async Task<ObjectResult> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        var runs = await _runs.ListAsync(new ListQuery(0, 10_000, new Dictionary<string, string> { ["scheduleDefinitionId"] = id.ToString() }), cancellationToken);
        foreach (var run in runs.Items)
        {
            await _runWrites.DeleteAsync(run.Id, cancellationToken);
        }

        await _writes.DeleteAsync(id, cancellationToken);
        var root = await _metadata.LoadAsync(cancellationToken);
        ApiSurfaceMetadataStore.RemoveEntry(root, "schedules", id.ToString());
        await _metadata.SaveAsync(root, cancellationToken);
        return OkEnvelope(new DeletionConfirmationWire(id.ToString(), true));
    }

    /// <summary>Enables a schedule and recomputes its next-due time.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New state.</returns>
    [HttpPost("schedules/{id:long}/enable")]
    public Task<ObjectResult> EnableAsync(long id, CancellationToken cancellationToken) => SetEnabledAsync(id, true, cancellationToken);

    /// <summary>Disables a schedule and clears its next-due time.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New state.</returns>
    [HttpPost("schedules/{id:long}/disable")]
    public Task<ObjectResult> DisableAsync(long id, CancellationToken cancellationToken) => SetEnabledAsync(id, false, cancellationToken);

    /// <summary>Triggers a schedule immediately (manual run).</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Started audit identifier.</returns>
    [HttpPost("schedules/{id:long}/run-now")]
    public async Task<ObjectResult> RunNowAsync(long id, CancellationToken cancellationToken)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        try
        {
            var auditId = await _scheduler.RunNowAsync(id, cancellationToken);
            return OkEnvelope(new ScheduleRunNowResponse(auditId.ToString()));
        }
        catch (InvalidOperationException ex)
        {
            return ConflictError(ex.Message);
        }
    }

    /// <summary>Lists run history of a schedule.</summary>
    /// <param name="id">Schedule identifier.</param>
    /// <param name="offset">Page offset.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paged run history.</returns>
    [HttpGet("schedules/{id:long}/runs")]
    public async Task<ObjectResult> ListRunsAsync(long id, [FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (await _reads.GetByIdAsync(id, cancellationToken) is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        var page = await _runs.ListAsync(BindListQuery(offset, limit, "StartedAt", true, new Dictionary<string, string> { ["scheduleDefinitionId"] = id.ToString() }), cancellationToken);
        var items = page.Items.Select(run => new ScheduleRunWire(
            run.Id.ToString(),
            run.ScheduleDefinitionId.ToString(),
            run.AuditId?.ToString() ?? string.Empty,
            ApiEnumConverter<ScheduleTriggerType>.ToWireValue(run.TriggerType),
            run.StartedAt,
            run.FinishedAt,
            OutcomeWire(run.Outcome),
            Array.Empty<CompletionActionResultWire>())).ToList();
        return OkEnvelope(items, PaginationOf(page));
    }

    private async Task<ObjectResult> SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken)
    {
        var existing = await _reads.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFoundError($"Schedule {id} does not exist.");
        }

        var nextDue = enabled ? NextDue(existing.RecurrenceRule) : null;
        await _writes.UpdateAsync(existing with { IsEnabled = enabled, NextDueAt = nextDue }, cancellationToken);
        return OkEnvelope(new ScheduleStateResponse(id.ToString(), enabled, nextDue));
    }

    private async Task<ScheduleWire> ToWireAsync(ScheduleDefinitionDto schedule, JsonObject root, CancellationToken cancellationToken)
    {
        var entry = ApiSurfaceMetadataStore.ReadEntry(root, "schedules", schedule.Id.ToString());
        var runs = await _runs.ListAsync(new ListQuery(0, 1, new Dictionary<string, string> { ["scheduleDefinitionId"] = schedule.Id.ToString() }, "StartedAt", true), cancellationToken);
        var fallback = DateTimeOffset.UnixEpoch;
        return new ScheduleWire(
            schedule.Id.ToString(),
            schedule.ProjectId.ToString(),
            schedule.ConfigurationId.ToString(),
            entry?.Name ?? $"Schedule {schedule.Id}",
            schedule.IsEnabled,
            schedule.RecurrenceRule.Length == 0 ? null : schedule.RecurrenceRule,
            schedule.NextDueAt,
            runs.Items.Count > 0 ? runs.Items[0].StartedAt : null,
            ProjectActions(schedule.CompletionActionsJson),
            entry?.CreatedAt ?? fallback,
            entry?.UpdatedAt ?? fallback);
    }

    private static DateTimeOffset? NextDue(string recurrence)
    {
        if (recurrence.Length == 0 || !RecurrenceRule.TryParse(recurrence, out var rule, out _))
        {
            return null;
        }

        return rule.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
    }

    private static bool TryValidateCron(string? cron, out string recurrence, out string? error)
    {
        recurrence = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(cron))
        {
            return true;
        }

        if (!RecurrenceRule.TryParse(cron, out _, out var parseError))
        {
            error = parseError ?? "The recurrence expression is invalid.";
            return false;
        }

        recurrence = cron.Trim();
        return true;
    }

    private static string OutcomeWire(ScheduleRunOutcome outcome) => outcome switch
    {
        ScheduleRunOutcome.Success => "completed",
        ScheduleRunOutcome.Failed => "failed",
        _ => "cancelled",
    };

    private static IReadOnlyList<ScheduleCompletionActionWire> ProjectActions(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
        {
            return [];
        }

        var actions = ScheduleCompletionActions.Parse(actionsJson);
        return actions.Select(action =>
        {
            var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            void Add(string key, object? value)
            {
                if (value is not null)
                {
                    parameters[key] = JsonSerializer.SerializeToElement(value, ApiJsonOptions.Default);
                }
            }

            Add("destinationId", action.DestinationId?.ToString());
            Add("format", action.Format is { } format ? ExportsController.FormatWire(format) : null);
            Add("datasets", action.Datasets);
            Add("endpoint", action.Endpoint);
            Add("title", action.Title);
            Add("message", action.Message);
            return new ScheduleCompletionActionWire(action.Kind, parameters.Count > 0 ? parameters : null);
        }).ToList();
    }

    private static bool TryBuildActions(IReadOnlyList<ScheduleCompletionActionWire>? wire, out string? actionsJson, out string? error)
    {
        actionsJson = null;
        error = null;
        if (wire is null || wire.Count == 0)
        {
            return true;
        }

        try
        {
            var actions = wire.Select(item =>
            {
                var parameters = item.Parameters;
                long? destinationId = null;
                if (parameters is not null && parameters.TryGetValue("destinationId", out var destinationElement))
                {
                    var text = destinationElement.ValueKind == JsonValueKind.Number
                        ? destinationElement.GetRawText()
                        : destinationElement.GetString();
                    if (!long.TryParse(text, out var parsedDestination))
                    {
                        throw new FormatException("destinationId must be an entity identifier.");
                    }

                    destinationId = parsedDestination;
                }

                ExportFormat? format = null;
                if (parameters is not null && parameters.TryGetValue("format", out var formatElement))
                {
                    if (!ExportsController.TryParseFormat(formatElement.GetString(), out var parsedFormat))
                    {
                        throw new FormatException($"Unknown export format '{formatElement.GetString()}'.");
                    }

                    format = parsedFormat;
                }

                IReadOnlyList<string>? datasets = null;
                if (parameters is not null && parameters.TryGetValue("datasets", out var datasetsElement) && datasetsElement.ValueKind == JsonValueKind.Array)
                {
                    datasets = datasetsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                }

                string? GetString(string key) =>
                    parameters is not null && parameters.TryGetValue(key, out var element) ? element.GetString() : null;

                return new ScheduleCompletionAction(
                    item.Kind,
                    destinationId,
                    format,
                    datasets,
                    GetString("endpoint"),
                    GetString("title"),
                    GetString("message"));
            }).ToList();

            actionsJson = ScheduleCompletionActions.Serialize(actions);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
