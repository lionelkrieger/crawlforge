using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Automation action log (criterion #23): every invocation recorded,
/// user-viewable, and purgeable.
/// </summary>
public class AutomationActionLogTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public AutomationActionLogTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task EntriesArePersistedAndViewable()
    {
        var marker = "viewable-" + Guid.NewGuid().ToString("N");
        var log = new AutomationActionLog(_stack.ActionLog, _stack.ActionLog);
        await log.LogAsync(new AutomationActionEntry(AutomationTransportKind.Stdio, marker + "-a", "{\"name\":\"start-audit\"}", AutomationOutcome.Success));
        await log.LogAsync(new AutomationActionEntry(AutomationTransportKind.Sse, marker + "-b", "{\"uri\":\"crawlforge://audits\"}", AutomationOutcome.Failed));

        var page = await log.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["TargetName"] = marker + "-a" }));
        var entry = Assert.Single(page.Items);
        Assert.Equal(AutomationTransportKind.Stdio, entry.Transport);
        Assert.Equal(AutomationOutcome.Success, entry.Outcome);
        Assert.True(entry.Timestamp <= DateTimeOffset.UtcNow);

        var failed = await log.ListAsync(new Contracts.Common.ListQuery(
            Filters: new Dictionary<string, string> { ["TargetName"] = marker + "-b" }));
        Assert.Equal(AutomationOutcome.Failed, Assert.Single(failed.Items).Outcome);
    }

    [Fact]
    public async Task PurgeRemovesAllEntriesAndReturnsCount()
    {
        var log = new AutomationActionLog(_stack.ActionLog, _stack.ActionLog);
        for (var i = 0; i < 5; i++)
        {
            await log.LogAsync(new AutomationActionEntry(AutomationTransportKind.Http, "ping", "{}", AutomationOutcome.Success));
        }

        var purged = await log.PurgeAsync();
        Assert.True(purged >= 5);
        Assert.Equal(0, (await log.ListAsync(new Contracts.Common.ListQuery())).TotalCount);
        Assert.Equal(0, await log.PurgeAsync()); // idempotent
    }

    [Fact]
    public async Task DelegatedControlFlowLogsEveryStep()
    {
        // start → poll → completion → re-audit → high-severity read:
        // the full delegated AI control flow leaves a complete trail.
        var host = new McpTestHost(_stack);
        var (_, projectId, configurationId) = await _stack.CreateAuditAsync();
        var start = await host.Tools.InvokeToolAsync("start-audit",
            new Dictionary<string, object?> { ["projectId"] = projectId, ["configurationId"] = configurationId });
        var auditId = System.Text.Json.JsonDocument.Parse(start.ContentJson).RootElement.GetProperty("auditId").GetInt64();

        await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "get-audit-progress", arguments = new { auditId } },
        }), AutomationTransportKind.Stdio);
        await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "re-audit", arguments = new { auditId } },
        }), AutomationTransportKind.Stdio);
        await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "resources/read",
            @params = new { uri = $"crawlforge://audits/{auditId}/high-severity-findings" },
        }), AutomationTransportKind.Stdio);

        var page = await host.ActionLog.ListAsync(new Contracts.Common.ListQuery(Limit: 1000));
        var stdio = page.Items.Where(r => r.Transport == AutomationTransportKind.Stdio).ToArray();
        Assert.True(stdio.Length >= 3);
        Assert.Contains(stdio, r => r.TargetName == "tools/call" && r.ArgumentsSummary.Contains("get-audit-progress"));
        Assert.Contains(stdio, r => r.TargetName == "tools/call" && r.ArgumentsSummary.Contains("re-audit"));
        Assert.Contains(stdio, r => r.TargetName == "resources/read");
    }

    [Fact]
    public async Task ToolLevelFailures_AreLoggedAsFailed()
    {
        // §11.5 outcome semantics (D7): MCP tool failures arrive as
        // result.isError=true inside a SUCCESS JSON-RPC envelope — the action
        // log must classify them Failed, alongside dispatch-level errors.
        var host = new McpTestHost(_stack);
        var unknownTool = "no-such-tool-" + Guid.NewGuid().ToString("N");

        var unknown = await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 201,
            method = "tools/call",
            @params = new { name = unknownTool, arguments = new { } },
        }), AutomationTransportKind.Stdio);
        var missingArgument = await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 202,
            method = "tools/call",
            @params = new { name = "start-audit", arguments = new { } },
        }), AutomationTransportKind.Stdio);
        var dispatchError = await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 203,
            method = "no/such-method",
        }), AutomationTransportKind.Stdio);

        var (_, projectId, configurationId) = await _stack.CreateAuditAsync();
        var success = await host.Dispatcher.DispatchAsync(System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 204,
            method = "tools/call",
            @params = new { name = "start-audit", arguments = new { projectId, configurationId } },
        }), AutomationTransportKind.Stdio);

        // Tool-level failures ride a SUCCESS envelope with result.isError=true.
        foreach (var failed in new[] { unknown, missingArgument })
        {
            using var document = System.Text.Json.JsonDocument.Parse(failed.ResponseJson!);
            Assert.False(document.RootElement.TryGetProperty("error", out _));
            Assert.True(document.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        // Dispatch-level error rides an error envelope.
        using (var document = System.Text.Json.JsonDocument.Parse(dispatchError.ResponseJson!))
        {
            Assert.True(document.RootElement.TryGetProperty("error", out _));
        }

        // Successful invocation stays a success envelope.
        using (var document = System.Text.Json.JsonDocument.Parse(success.ResponseJson!))
        {
            Assert.False(document.RootElement.TryGetProperty("error", out _));
            Assert.False(document.RootElement.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean());
        }

        var page = await host.ActionLog.ListAsync(new Contracts.Common.ListQuery(Limit: 1000));
        var entries = page.Items.Where(r => r.Transport == AutomationTransportKind.Stdio).ToArray();

        // Tool-level isError → Failed (both invocations).
        Assert.Contains(entries, r => r.TargetName == "tools/call"
            && r.ArgumentsSummary.Contains(unknownTool) && r.Outcome == AutomationOutcome.Failed);
        Assert.Contains(entries, r => r.TargetName == "tools/call"
            && r.ArgumentsSummary.Contains("start-audit") && r.Outcome == AutomationOutcome.Failed);
        // Dispatch-level error → Failed.
        Assert.Contains(entries, r => r.TargetName == "no/such-method" && r.Outcome == AutomationOutcome.Failed);
        // Success → Success.
        Assert.Contains(entries, r => r.TargetName == "tools/call"
            && r.ArgumentsSummary.Contains("start-audit") && r.Outcome == AutomationOutcome.Success);
    }
}
