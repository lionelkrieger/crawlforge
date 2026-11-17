using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// All eight lifecycle tools (FS-CRAWL-001 §11.3): one-to-one mapping onto the
/// local API operations, argument validation, and failure envelopes.
/// </summary>
public class McpToolSurfaceTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpToolSurfaceTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value);

    private static JsonElement Content(McpInvocationResult result)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        return JsonDocument.Parse(result.ContentJson).RootElement;
    }

    [Fact]
    public async Task StartAuditCreatesAuditAndReturnsInitialState()
    {
        var host = new McpTestHost(_stack);
        var (_, projectId, configurationId) = await _stack.CreateAuditAsync();
        var result = await host.Tools.InvokeToolAsync("start-audit",
            Args(("projectId", projectId), ("configurationId", configurationId)));

        var payload = Content(result);
        var auditId = payload.GetProperty("auditId").GetInt64();
        Assert.True(auditId > 0);
        Assert.Equal("Queued", payload.GetProperty("status").GetString());
        Assert.Single(host.Execution.StartCalls);
        Assert.Equal(ScheduleTriggerType.Manual, host.Execution.StartCalls[0].Trigger);
        Assert.NotNull(await _stack.Audits.GetByIdAsync(auditId));
    }

    [Fact]
    public async Task PauseResumeCancelDelegateToLifecyclePort()
    {
        var host = new McpTestHost(_stack);
        var (auditId, _, _) = await _stack.CreateAuditAsync();

        var paused = Content(await host.Tools.InvokeToolAsync("pause-audit", Args(("auditId", auditId))));
        Assert.Equal("Paused", paused.GetProperty("status").GetString());
        var resumed = Content(await host.Tools.InvokeToolAsync("resume-audit", Args(("auditId", auditId))));
        Assert.Equal("Running", resumed.GetProperty("status").GetString());
        var cancelled = Content(await host.Tools.InvokeToolAsync("cancel-audit", Args(("auditId", auditId))));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());

        Assert.Equal([auditId], _stack.Lifecycle.PauseCalls);
        Assert.Equal([auditId], _stack.Lifecycle.ResumeCalls);
        Assert.Equal([auditId], _stack.Lifecycle.CancelCalls);
    }

    [Fact]
    public async Task ReAuditReturnsNewAuditIdentifierAndRefreshFlag()
    {
        var host = new McpTestHost(_stack);
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        _stack.Lifecycle.ReAuditResultId = 777;

        var payload = Content(await host.Tools.InvokeToolAsync("re-audit",
            Args(("auditId", auditId), ("refresh", true))));

        Assert.Equal(777, payload.GetProperty("auditId").GetInt64());
        Assert.Equal(auditId, payload.GetProperty("sourceAuditId").GetInt64());
        Assert.Equal((auditId, true), Assert.Single(_stack.Lifecycle.ReAuditCalls));
    }

    [Fact]
    public async Task AnalyzeSinglePagePassesUrlRenderAndRuleFilters()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Tools.InvokeToolAsync("analyze-single-page",
            Args(("url", "https://example.test/page"), ("render", true),
                ("ruleKeys", new[] { "content.keyword-cannibalization" })));

        var payload = Content(result);
        Assert.True(payload.GetProperty("findings").GetArrayLength() >= 1);
        Assert.True(payload.GetProperty("inventory").GetArrayLength() >= 1);
        var request = Assert.Single(host.SinglePage.Requests);
        Assert.Equal("https://example.test/page", request.Url);
        Assert.True(request.Render);
        Assert.Equal(["content.keyword-cannibalization"], request.RuleKeys);
    }

    [Fact]
    public async Task RunScheduleNowTriggersScheduler()
    {
        var host = new McpTestHost(_stack);
        host.Scheduler.RunNowResultId = 555;
        var payload = Content(await host.Tools.InvokeToolAsync("run-schedule-now", Args(("scheduleId", 9L))));
        Assert.Equal(555, payload.GetProperty("auditId").GetInt64());
        Assert.Equal([9L], host.Scheduler.RunNowCalls);
    }

    [Fact]
    public async Task GetAuditProgressReturnsCounters()
    {
        var host = new McpTestHost(_stack);
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var payload = Content(await host.Tools.InvokeToolAsync("get-audit-progress", Args(("auditId", auditId))));
        Assert.Equal(auditId, payload.GetProperty("auditId").GetInt64());
        Assert.Equal(10, payload.GetProperty("discovered").GetInt64());
        Assert.Equal(60.0, payload.GetProperty("percentComplete").GetDouble());
    }

    [Fact]
    public async Task UnknownToolFailsWithMessage()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Tools.InvokeToolAsync("drop-database", Args());
        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown tool", result.ErrorMessage);
    }

    [Fact]
    public async Task MissingRequiredArgumentFailsGracefully()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Tools.InvokeToolAsync("pause-audit", Args());
        Assert.False(result.IsSuccess);
        Assert.Contains("auditId", result.ErrorMessage);
    }

    [Fact]
    public async Task NumericStringArgumentsAreAccepted()
    {
        var host = new McpTestHost(_stack);
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var result = await host.Tools.InvokeToolAsync("get-audit-progress",
            Args(("auditId", auditId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task LifecyclePortFailureBecomesErrorEnvelopeNotThrow()
    {
        var host = new McpTestHost(_stack);
        _stack.Lifecycle.Snapshot = null!; // force NullReferenceException inside the port
        var result = await host.Tools.InvokeToolAsync("pause-audit", Args(("auditId", 1L)));
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }
}
