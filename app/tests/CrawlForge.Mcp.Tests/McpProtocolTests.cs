using System.Text.Json;
using CrawlForge.Contracts.Enums;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// JSON-RPC 2.0 / MCP protocol conformance of the hand-rolled dispatcher:
/// handshake, capability negotiation, envelopes, error codes, notifications,
/// and action logging of every request (FS-CRAWL-001 §11.1, §11.5).
/// </summary>
public class McpProtocolTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpProtocolTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private static JsonElement ResultOf(string response) =>
        JsonDocument.Parse(response).RootElement.GetProperty("result");

    [Fact]
    public async Task InitializeNegotiatesCapabilitiesAndAssignsSession()
    {
        var host = new McpTestHost(_stack);
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""";
        var result = await host.Dispatcher.DispatchAsync(request, AutomationTransportKind.Http);

        Assert.NotNull(result.SessionId);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        var payload = root.GetProperty("result");
        Assert.Equal("2025-06-18", payload.GetProperty("protocolVersion").GetString());
        Assert.Equal("crawlforge", payload.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(payload.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.True(payload.GetProperty("capabilities").TryGetProperty("resources", out _));
        Assert.True(payload.GetProperty("capabilities").TryGetProperty("prompts", out _));
    }

    [Theory]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("1999-01-01", "2025-06-18")]
    public async Task VersionNegotiationEchoesSupportedVersions(string requested, string expected)
    {
        var host = new McpTestHost(_stack);
        var request = $$$"""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"{{{requested}}}"}}""";
        var result = await host.Dispatcher.DispatchAsync(request, AutomationTransportKind.Http);
        Assert.Equal(expected, ResultOf(result.ResponseJson!).GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task PingReturnsEmptyResult()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""", AutomationTransportKind.Http);
        Assert.Equal(JsonValueKind.Object, ResultOf(result.ResponseJson!).ValueKind);
    }

    [Fact]
    public async Task UnknownMethodReturnsMethodNotFound()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":3,"method":"no/such-method"}""", AutomationTransportKind.Http);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MalformedJsonReturnsParseError()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync("{not json", AutomationTransportKind.Http);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task MissingMethodReturnsInvalidRequest()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":4}""", AutomationTransportKind.Http);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        Assert.Equal(-32600, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NotificationsProduceNoResponse()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", AutomationTransportKind.Http);
        Assert.Null(result.ResponseJson);
    }

    [Fact]
    public async Task ToolsListReturnsAllEightLifecycleTools()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/list"}""", AutomationTransportKind.Http);
        var tools = ResultOf(result.ResponseJson!).GetProperty("tools");
        Assert.Equal(8, tools.GetArrayLength());
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Equal(
            new[] { "start-audit", "pause-audit", "resume-audit", "cancel-audit", "re-audit", "analyze-single-page", "run-schedule-now", "get-audit-progress" },
            names);
        Assert.All(tools.EnumerateArray(), t =>
            Assert.Equal(JsonValueKind.Object, t.GetProperty("inputSchema").ValueKind));
    }

    [Fact]
    public async Task ResourcesListReturnsAllFifteenResources()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":6,"method":"resources/list"}""", AutomationTransportKind.Http);
        var resources = ResultOf(result.ResponseJson!).GetProperty("resources");
        Assert.Equal(15, resources.GetArrayLength());
    }

    [Fact]
    public async Task PromptsListReturnsAllFivePrompts()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":7,"method":"prompts/list"}""", AutomationTransportKind.Http);
        var prompts = ResultOf(result.ResponseJson!).GetProperty("prompts");
        Assert.Equal(5, prompts.GetArrayLength());
    }

    [Fact]
    public async Task ToolsCallWrapsResultInMcpContentEnvelope()
    {
        var host = new McpTestHost(_stack);
        var (auditId, _, _) = await _stack.CreateAuditAsync();
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 8,
            method = "tools/call",
            @params = new { name = "get-audit-progress", arguments = new { auditId } },
        });
        var result = await host.Dispatcher.DispatchAsync(request, AutomationTransportKind.Stdio);
        var payload = ResultOf(result.ResponseJson!);
        Assert.False(payload.GetProperty("isError").GetBoolean());
        var content = payload.GetProperty("content")[0];
        Assert.Equal("text", content.GetProperty("type").GetString());
        using var inner = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        Assert.Equal(auditId, inner.RootElement.GetProperty("auditId").GetInt64());
    }

    [Fact]
    public async Task ToolsCallMissingArgumentReturnsIsErrorEnvelope()
    {
        // MCP semantics: tool execution errors (including bad arguments) are
        // reported inside the result with isError=true, not as protocol errors.
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"pause-audit","arguments":{}}}""",
            AutomationTransportKind.Http);
        var payload = ResultOf(result.ResponseJson!);
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Contains("auditId", payload.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ResourcesReadUnknownUriReturnsInvalidParams()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":10,"method":"resources/read","params":{"uri":"crawlforge://nope"}}""",
            AutomationTransportKind.Http);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        Assert.Equal(-32602, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task EveryRequestIsLoggedWithTransportAndOutcome()
    {
        var host = new McpTestHost(_stack);
        await host.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", AutomationTransportKind.Stdio);
        await host.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"no/such"}""", AutomationTransportKind.Http);
        await host.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", AutomationTransportKind.Http);

        var page = await host.ActionLog.ListAsync(new Contracts.Common.ListQuery(Limit: 1000));
        Assert.True(page.TotalCount >= 2); // notification not logged
        Assert.Contains(page.Items, r => r.TargetName == "ping" && r.Transport == AutomationTransportKind.Stdio && r.Outcome == AutomationOutcome.Success);
        Assert.Contains(page.Items, r => r.TargetName == "no/such" && r.Outcome == AutomationOutcome.Failed);
        Assert.DoesNotContain(page.Items, r => r.TargetName == "notifications/initialized");
    }

    [Fact]
    public async Task StringIdsAreEchoedVerbatim()
    {
        var host = new McpTestHost(_stack);
        var result = await host.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":"abc-123","method":"ping"}""", AutomationTransportKind.Http);
        using var document = JsonDocument.Parse(result.ResponseJson!);
        Assert.Equal("abc-123", document.RootElement.GetProperty("id").GetString());
    }
}
