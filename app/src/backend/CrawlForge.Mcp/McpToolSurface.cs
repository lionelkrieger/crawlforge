using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Mcp;

/// <summary>
/// MCP tool surface (FS-CRAWL-001 §11.3): all eight lifecycle tools, each
/// mapped one-to-one onto the corresponding local API operation with identical
/// semantics. The surface exposes no settings, destination, credential, proxy,
/// or database-provider management (§11.1). Dependencies arrive through
/// constructor composition; the Host binds the locked services and the
/// <see cref="IAuditLifecyclePort"/> adapter.
/// </summary>
public sealed class McpToolSurface : IMcpToolSurface
{
    private readonly IAuditExecutionService _execution;
    private readonly IAuditLifecyclePort _lifecycle;
    private readonly IScheduledAuditService _scheduler;
    private readonly ISinglePageAnalyzer _singlePage;
    private readonly IReadRepository<AuditDto> _audits;

    /// <summary>Creates the tool surface.</summary>
    /// <param name="execution">Locked audit-start service.</param>
    /// <param name="lifecycle">Audit-scoped lifecycle port.</param>
    /// <param name="scheduler">Locked schedule service.</param>
    /// <param name="singlePage">Locked single-page analyzer.</param>
    /// <param name="audits">Audit read repository.</param>
    public McpToolSurface(
        IAuditExecutionService execution,
        IAuditLifecyclePort lifecycle,
        IScheduledAuditService scheduler,
        ISinglePageAnalyzer singlePage,
        IReadRepository<AuditDto> audits)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _singlePage = singlePage ?? throw new ArgumentNullException(nameof(singlePage));
        _audits = audits ?? throw new ArgumentNullException(nameof(audits));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Definitions);

    /// <inheritdoc/>
    public async Task<McpInvocationResult> InvokeToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            return toolName switch
            {
                "start-audit" => await StartAuditAsync(arguments, cancellationToken).ConfigureAwait(false),
                "pause-audit" => await LifecycleAsync(arguments, _lifecycle.PauseAsync, cancellationToken).ConfigureAwait(false),
                "resume-audit" => await LifecycleAsync(arguments, _lifecycle.ResumeAsync, cancellationToken).ConfigureAwait(false),
                "cancel-audit" => await LifecycleAsync(arguments, _lifecycle.CancelAsync, cancellationToken).ConfigureAwait(false),
                "re-audit" => await ReAuditAsync(arguments, cancellationToken).ConfigureAwait(false),
                "analyze-single-page" => await AnalyzeSinglePageAsync(arguments, cancellationToken).ConfigureAwait(false),
                "run-schedule-now" => await RunScheduleNowAsync(arguments, cancellationToken).ConfigureAwait(false),
                "get-audit-progress" => await GetProgressAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => new McpInvocationResult(false, "{}", $"Unknown tool: {toolName}"),
            };
        }
        catch (McpArgumentMissingException ex)
        {
            return new McpInvocationResult(false, "{}", ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpInvocationResult(false, "{}", ex.Message);
        }
    }

    private static readonly IReadOnlyList<McpToolDefinition> Definitions =
    [
        new("start-audit",
            "Starts an audit exactly as the REST start operation (configuration reference; optional limit overrides).",
            """{"type":"object","properties":{"projectId":{"type":"integer","description":"Owning project identifier"},"configurationId":{"type":"integer","description":"Crawl configuration to snapshot"},"limitOverrides":{"type":"object","description":"Optional per-audit limit overrides"}},"required":["projectId","configurationId"]}"""),
        new("pause-audit",
            "Checkpoints and pauses a running audit.",
            """{"type":"object","properties":{"auditId":{"type":"integer","description":"Audit identifier"}},"required":["auditId"]}"""),
        new("resume-audit",
            "Rehydrates and resumes a paused audit.",
            """{"type":"object","properties":{"auditId":{"type":"integer","description":"Audit identifier"}},"required":["auditId"]}"""),
        new("cancel-audit",
            "Drains workers and finalizes partial data.",
            """{"type":"object","properties":{"auditId":{"type":"integer","description":"Audit identifier"}},"required":["auditId"]}"""),
        new("re-audit",
            "Triggers a follow-up analysis pass over the same dataset; returns the new audit identifier.",
            """{"type":"object","properties":{"auditId":{"type":"integer","description":"Audit to re-analyze"},"refresh":{"type":"boolean","description":"Refresh stored content first (default false)"}},"required":["auditId"]}"""),
        new("analyze-single-page",
            "Ad-hoc single-page audit: findings and element inventory for one URL.",
            """{"type":"object","properties":{"url":{"type":"string","description":"Target URL"},"render":{"type":"boolean","description":"Render before analysis (default false)"},"ruleKeys":{"type":"array","items":{"type":"string"},"description":"Optional rule filter"}},"required":["url"]}"""),
        new("run-schedule-now",
            "Manual trigger of a schedule; returns the started audit identifier.",
            """{"type":"object","properties":{"scheduleId":{"type":"integer","description":"Schedule definition identifier"}},"required":["scheduleId"]}"""),
        new("get-audit-progress",
            "Reads live progress for an audit (status, phase, counters).",
            """{"type":"object","properties":{"auditId":{"type":"integer","description":"Audit identifier"}},"required":["auditId"]}"""),
    ];

    private async Task<McpInvocationResult> StartAuditAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var projectId = McpArguments.RequireLong(arguments, "projectId");
        var configurationId = McpArguments.RequireLong(arguments, "configurationId");
        CrawlLimitOverrides? overrides = null;
        if (arguments.TryGetValue("limitOverrides", out var raw) && raw is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            overrides = element.Deserialize<CrawlLimitOverrides>(McpProtocol.JsonOptions);
        }

        var auditId = await _execution.StartAuditAsync(
            projectId, configurationId, ScheduleTriggerType.Manual, overrides, cancellationToken).ConfigureAwait(false);
        var audit = await _audits.GetByIdAsync(auditId, cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["auditId"] = auditId,
            ["status"] = audit?.Status.ToString() ?? AuditStatus.Queued.ToString(),
            ["phase"] = audit?.Phase.ToString() ?? AuditPhase.NotStarted.ToString(),
        };
        return Success(payload);
    }

    private async Task<McpInvocationResult> LifecycleAsync(
        IReadOnlyDictionary<string, object?> arguments,
        Func<long, CancellationToken, Task<AuditLifecycleSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        var auditId = McpArguments.RequireLong(arguments, "auditId");
        var snapshot = await operation(auditId, cancellationToken).ConfigureAwait(false);
        return Success(new Dictionary<string, object?>
        {
            ["auditId"] = snapshot.AuditId,
            ["status"] = snapshot.Status.ToString(),
            ["phase"] = snapshot.Phase.ToString(),
        });
    }

    private async Task<McpInvocationResult> ReAuditAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var auditId = McpArguments.RequireLong(arguments, "auditId");
        var refresh = McpArguments.GetBool(arguments, "refresh");
        var newAuditId = await _lifecycle.ReAuditAsync(auditId, refresh, cancellationToken).ConfigureAwait(false);
        return Success(new Dictionary<string, object?> { ["auditId"] = newAuditId, ["sourceAuditId"] = auditId });
    }

    private async Task<McpInvocationResult> AnalyzeSinglePageAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var url = McpArguments.RequireString(arguments, "url");
        var render = McpArguments.GetBool(arguments, "render");
        var ruleKeys = McpArguments.GetStringList(arguments, "ruleKeys");
        var result = await _singlePage.AnalyzeAsync(
            new SinglePageAnalysisRequest(url, render, ruleKeys), cancellationToken).ConfigureAwait(false);
        return Success(result);
    }

    private async Task<McpInvocationResult> RunScheduleNowAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var scheduleId = McpArguments.RequireLong(arguments, "scheduleId");
        var auditId = await _scheduler.RunNowAsync(scheduleId, cancellationToken).ConfigureAwait(false);
        return Success(new Dictionary<string, object?> { ["auditId"] = auditId, ["scheduleId"] = scheduleId });
    }

    private async Task<McpInvocationResult> GetProgressAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var auditId = McpArguments.RequireLong(arguments, "auditId");
        var progress = await _lifecycle.GetProgressAsync(auditId, cancellationToken).ConfigureAwait(false);
        return Success(progress);
    }

    private static McpInvocationResult Success(object payload) =>
        new(true, JsonSerializer.Serialize(payload, McpProtocol.JsonOptions), null);
}
