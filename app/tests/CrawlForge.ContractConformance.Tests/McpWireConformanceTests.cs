using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrawlForge.ContractConformance.Tests.Harness;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// MCP wire conformance (FS-CRAWL-001 §11 + Appendix D) against the LIVE
/// loopback MCP server: JSON-RPC 2.0 over streamable HTTP with the local
/// bearer token + Mcp-Session-Id handshake. The locked surface is 8 tools /
/// 15 resources / 5 prompts; every entry is driven live and asserted for
/// result shape. The CR-011 paging drift (default 200 rows, no cursor vs
/// §11.7 default 50 / max 500 / cursor) is recorded + skipped.
/// </summary>
[Collection(LiveBackendCollection.Name)]
public sealed class McpWireConformanceTests
{
    private readonly LiveBackendFixture _fixture;

    /// <summary>Creates the suite over the shared live backend.</summary>
    /// <param name="fixture">Shared live-backend fixture.</param>
    public McpWireConformanceTests(LiveBackendFixture fixture) => _fixture = fixture;

    /// <summary>initialize negotiates the protocol and advertises the three capabilities.</summary>
    [Fact]
    public async Task Initialize_CapabilitiesAndServerInfo()
    {
        var capture = await File.ReadAllTextAsync(Path.Combine(RepoPaths.CapturedDir, "mcp-initialize.json"));
        using var document = JsonDocument.Parse(capture);
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var result = root.GetProperty("result");
        Assert.False(string.IsNullOrEmpty(result.GetProperty("protocolVersion").GetString()));
        var capabilities = result.GetProperty("capabilities");
        Assert.True(capabilities.TryGetProperty("tools", out _));
        Assert.True(capabilities.TryGetProperty("resources", out _));
        Assert.True(capabilities.TryGetProperty("prompts", out _));
        var serverInfo = result.GetProperty("serverInfo");
        Assert.Equal("crawlforge", serverInfo.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(serverInfo.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrEmpty(_fixture.Mcp.SessionId));
    }

    /// <summary>tools/list returns exactly the eight locked lifecycle tools with input schemas.</summary>
    [Fact]
    public async Task ToolsList_MatchesLockedSurface()
    {
        var exchange = await _fixture.Mcp.ListToolsAsync();
        Assert.False(exchange.IsError);
        var tools = exchange.Result.GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
        var expected = PayloadInventory.Load().Mcp.Tools.Select(t => t.Name).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var tool in tools.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(tool.GetProperty("description").GetString()));
            Assert.Equal(JsonValueKind.Object, tool.GetProperty("inputSchema").ValueKind);
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }

        await _fixture.SaveCaptureAsync("mcp-tools-list.json", exchange.Result);
    }

    /// <summary>resources/list returns exactly the fifteen locked read-only resources.</summary>
    [Fact]
    public async Task ResourcesList_MatchesLockedSurface()
    {
        var exchange = await _fixture.Mcp.ListResourcesAsync();
        Assert.False(exchange.IsError);
        var resources = exchange.Result.GetProperty("resources");
        var uris = resources.EnumerateArray().Select(r => r.GetProperty("uri").GetString()!).ToArray();
        var expected = PayloadInventory.Load().Mcp.Resources.Select(r => r.Uri).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), uris.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var resource in resources.EnumerateArray())
        {
            Assert.Equal("application/json", resource.GetProperty("mimeType").GetString());
        }

        await _fixture.SaveCaptureAsync("mcp-resources-list.json", exchange.Result);
    }

    /// <summary>prompts/list returns exactly the five locked briefing templates.</summary>
    [Fact]
    public async Task PromptsList_MatchesLockedSurface()
    {
        var exchange = await _fixture.Mcp.ListPromptsAsync();
        Assert.False(exchange.IsError);
        var prompts = exchange.Result.GetProperty("prompts");
        var names = prompts.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
        var expected = PayloadInventory.Load().Mcp.Prompts.Select(p => p.Name).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names.OrderBy(n => n, StringComparer.Ordinal));
        await _fixture.SaveCaptureAsync("mcp-prompts-list.json", exchange.Result);
    }

    /// <summary>start-audit maps one-to-one onto REST start and returns the new audit id.</summary>
    [Fact]
    public async Task ToolCall_StartAudit_ReturnsConformantContent()
    {
        var exchange = await _fixture.Mcp.CallToolAsync("start-audit", new
        {
            projectId = _fixture.ProjectId,
            configurationId = _fixture.BoundedConfigurationId,
            limitOverrides = new { maxUrls = 2, maxDepth = 1, maxDurationSeconds = 30 }
        });
        var payload = AssertToolSuccess(exchange);
        Assert.True(payload.TryGetProperty("auditId", out var auditId));
        Assert.Equal(JsonValueKind.Number, auditId.ValueKind);
        await _fixture.SaveCaptureAsync("mcp-tool-start-audit.json", exchange.Result);
    }

    /// <summary>get-audit-progress returns the live snapshot (engine counter dimensions).</summary>
    [Fact]
    public async Task ToolCall_GetAuditProgress_ReturnsCounters()
    {
        var exchange = await _fixture.Mcp.CallToolAsync("get-audit-progress", new { auditId = _fixture.AuditId });
        var payload = AssertToolSuccess(exchange);
        Assert.Equal(_fixture.AuditId, payload.GetProperty("auditId").GetInt64());
        // Producer lock: AuditProgressSnapshot (Mcp/AuditLifecyclePorts.cs:67).
        foreach (var field in new[] { "status", "phase", "discovered", "fetched", "analyzed", "queued" })
        {
            Assert.True(payload.TryGetProperty(field, out _), $"progress snapshot missing '{field}'");
        }

        Assert.Equal(JsonValueKind.String, payload.GetProperty("status").ValueKind);
        Assert.True(payload.GetProperty("fetched").GetInt64() > 0);
        await _fixture.SaveCaptureAsync("mcp-tool-get-audit-progress.json", exchange.Result);
    }

    /// <summary>
    /// pause/resume/cancel on a terminal audit surface the tool-error shape:
    /// JSON-RPC success envelope with result.isError=true + text content.
    /// </summary>
    [Fact]
    public async Task ToolCall_PauseResumeCancel_ErrorShapeConformant()
    {
        foreach (var tool in new[] { "pause-audit", "resume-audit", "cancel-audit" })
        {
            var exchange = await _fixture.Mcp.CallToolAsync(tool, new { auditId = _fixture.AuditId });
            Assert.Equal(200, exchange.Status);
            Assert.False(exchange.IsError, "tool errors ride inside a JSON-RPC success envelope");
            var result = exchange.Result;
            Assert.True(result.GetProperty("isError").GetBoolean(), $"{tool} on a completed audit must be a tool error");
            var content = result.GetProperty("content");
            Assert.Equal(1, content.GetArrayLength());
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(content[0].GetProperty("text").GetString()));
        }
    }

    /// <summary>re-audit (refresh:false) returns a new audit id over stored
    /// artifacts. Targets the MCP-started offline source: G-REAUDIT-SNAPSHOT
    /// breaks re-audit of REST-started audits (wire-shaped snapshot), while
    /// MCP-started audits keep the engine DTO snapshot and re-audit cleanly.</summary>
    [Fact]
    public async Task ToolCall_ReAudit_ReturnsNewAuditId()
    {
        var exchange = await _fixture.Mcp.CallToolAsync("re-audit", new { auditId = _fixture.OfflineSourceAuditId, refresh = false });
        var payload = AssertToolSuccess(exchange);
        var newAuditId = payload.GetProperty("auditId").GetInt64();
        Assert.NotEqual(_fixture.OfflineSourceAuditId, newAuditId);
        Assert.Equal(_fixture.OfflineSourceAuditId, payload.GetProperty("sourceAuditId").GetInt64());
        await _fixture.WaitForTerminalAsync(newAuditId, TimeSpan.FromSeconds(240));
        await _fixture.SaveCaptureAsync("mcp-tool-re-audit.json", exchange.Result);
    }

    /// <summary>analyze-single-page analyzes one fixture URL without starting an audit.</summary>
    [Fact]
    public async Task ToolCall_AnalyzeSinglePage_ConformantContent()
    {
        var exchange = await _fixture.Mcp.CallToolAsync("analyze-single-page", new
        {
            url = FixtureSite.BaseUrl + "/pages/noindex.html",
            render = false
        });
        var payload = AssertToolSuccess(exchange);
        Assert.True(payload.TryGetProperty("findings", out var findings));
        Assert.Equal(JsonValueKind.Array, findings.ValueKind);
        await _fixture.SaveCaptureAsync("mcp-tool-analyze-single-page.json", exchange.Result);
    }

    /// <summary>run-schedule-now triggers the bounded schedule and returns both ids.</summary>
    [Fact]
    public async Task ToolCall_RunScheduleNow_ConformantContent()
    {
        // The REST sweep / hub suite may fire run-now first; the scheduler's
        // documented no-overlap guard then reports an active run — retry until
        // that bounded run finishes (same pattern as TriggerScheduleRunAsync).
        McpExchange exchange;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            exchange = await _fixture.Mcp.CallToolAsync("run-schedule-now", new { scheduleId = _fixture.ScheduleId });
            var overlap = exchange.Status == 200 && !exchange.IsError
                && exchange.Result.GetProperty("isError").GetBoolean()
                && (exchange.Result.GetProperty("content")[0].GetProperty("text").GetString() ?? "")
                    .Contains("already has an active run", StringComparison.Ordinal);
            if (!overlap || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(1000);
        }

        var payload = AssertToolSuccess(exchange);
        Assert.Equal(_fixture.ScheduleId, payload.GetProperty("scheduleId").GetInt64());
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("auditId").ValueKind);
        // Wait for terminal so later run-now drives (REST sweep) never hit the
        // scheduler's documented no-overlap guard.
        await _fixture.WaitForTerminalAsync(payload.GetProperty("auditId").GetInt64(), TimeSpan.FromSeconds(120));
        await _fixture.SaveCaptureAsync("mcp-tool-run-schedule-now.json", exchange.Result);
    }

    /// <summary>audit-summary resource reads conformant JSON content
    /// (McpResourceSurface.cs:214-239: {audit, scores, metrics,
    /// findingCountsBySeverity, waf}).</summary>
    [Fact]
    public Task ResourceRead_AuditSummary_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/summary", "findingCountsBySeverity");

    /// <summary>high-severity-findings resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_HighSeverityFindings_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/high-severity-findings", null);

    /// <summary>url-detail resource reads conformant JSON content
    /// (McpResourceSurface.cs:267-303: {url, findings, wafDetection, ...};
    /// the url dimension carries the DTO's canonicalUrl nested).</summary>
    [Fact]
    public Task ResourceRead_UrlDetail_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/urls/{_fixture.NoindexUrlId}", "url");

    /// <summary>audit-list resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_AuditList_ConformantJson() =>
        AssertResourceAsync("crawlforge://audits", null);

    /// <summary>schedule-status resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_ScheduleStatus_ConformantJson() =>
        AssertResourceAsync("crawlforge://schedules", null);

    /// <summary>crawl-progress resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_Progress_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/progress", null);

    /// <summary>extracted-content resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_ExtractedContent_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/urls/{_fixture.NoindexUrlId}/content", null);

    /// <summary>ngram-analysis resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_Ngrams_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/ngrams", null);

    /// <summary>waf-blocks resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_WafBlocks_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/waf-blocks", null);

    /// <summary>host-throttle resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_HostThrottle_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/host-throttle", null);

    /// <summary>redirect-matrix-results resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_RedirectMatrix_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/redirect-matrix", null);

    /// <summary>log-correlation resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_LogCorrelation_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/log-correlation?import={_fixture.LogImportId}", null);

    /// <summary>semantic-link-opportunities resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_SemanticLinks_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/semantic-links", null);

    /// <summary>geo-readiness resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_GeoReadiness_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/geo-readiness", null);

    /// <summary>analytics-coverage resource reads conformant JSON content.</summary>
    [Fact]
    public Task ResourceRead_AnalyticsCoverage_ConformantJson() =>
        AssertResourceAsync($"crawlforge://audits/{_fixture.AuditId}/analytics-coverage", null);

    /// <summary>Unknown resource URI patterns produce a JSON-RPC error
    /// (McpResourceNotFoundException), not a crash. A well-formed URI with a
    /// missing audit id returns an empty success payload by design
    /// (McpResourceSurface.cs:214-239), so the error path is driven with an
    /// unknown subresource.</summary>
    [Fact]
    public async Task ResourceRead_UnknownUri_JsonRpcError()
    {
        var exchange = await _fixture.Mcp.ReadResourceAsync($"crawlforge://audits/{_fixture.AuditId}/no-such-subresource");
        Assert.True(exchange.IsError, "unknown resource must produce a JSON-RPC error");
        var error = exchange.Root.GetProperty("error");
        Assert.Equal(JsonValueKind.Number, error.GetProperty("code").ValueKind);
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
    }

    /// <summary>audit-health-briefing materializes a user message with the summary embedded.</summary>
    [Fact]
    public Task PromptGet_AuditHealthBriefing_ConformantMessages() =>
        AssertPromptAsync("audit-health-briefing", new { auditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>high-severity-triage materializes a user message with findings embedded.</summary>
    [Fact]
    public Task PromptGet_HighSeverityTriage_ConformantMessages() =>
        AssertPromptAsync("high-severity-triage", new { auditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>audit-delta-briefing materializes a comparison briefing.</summary>
    [Fact]
    public Task PromptGet_AuditDeltaBriefing_ConformantMessages() =>
        AssertPromptAsync("audit-delta-briefing", new
        {
            baseAuditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            targetAuditId = _fixture.ReauditId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

    /// <summary>ngram-topical-briefing materializes a topical briefing.</summary>
    [Fact]
    public Task PromptGet_NgramTopicalBriefing_ConformantMessages() =>
        AssertPromptAsync("ngram-topical-briefing", new { auditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>semantic-topical-briefing materializes a semantic briefing.</summary>
    [Fact]
    public Task PromptGet_SemanticTopicalBriefing_ConformantMessages() =>
        AssertPromptAsync("semantic-topical-briefing", new { auditId = _fixture.AuditId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>Unknown methods produce the standard -32601 JSON-RPC error.</summary>
    [Fact]
    public async Task UnknownMethod_JsonRpcError()
    {
        var exchange = await _fixture.Mcp.RawAsync("""{"jsonrpc":"2.0","id":9001,"method":"bogus/method","params":{}}""");
        Assert.True(exchange.IsError);
        Assert.Equal(-32601, exchange.Root.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>resources/templates/list is not implemented (-32601); recorded drift.</summary>
    [Fact]
    public async Task ResourcesTemplatesList_MethodNotFound()
    {
        var exchange = await _fixture.Mcp.RawAsync("""{"jsonrpc":"2.0","id":9002,"method":"resources/templates/list","params":{}}""");
        Assert.True(exchange.IsError);
        Assert.Equal(-32601, exchange.Root.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>An invalid bearer token is rejected with 401 before dispatch.</summary>
    [Fact]
    public async Task InvalidToken_Rejected401()
    {
        var descriptor = await File.ReadAllTextAsync(Path.Combine(_fixture.DataDirectory, "mcp-endpoint.json"));
        using var document = JsonDocument.Parse(descriptor);
        var endpoint = document.RootElement.GetProperty("endpoint").GetString()!;
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content);
        Assert.Equal(401, (int)response.StatusCode);
    }

    /// <summary>After initialize, requests without Mcp-Session-Id are rejected with 400.</summary>
    [Fact]
    public async Task MissingSessionId_Rejected400()
    {
        var descriptor = await File.ReadAllTextAsync(Path.Combine(_fixture.DataDirectory, "mcp-endpoint.json"));
        using var document = JsonDocument.Parse(descriptor);
        var endpoint = document.RootElement.GetProperty("endpoint").GetString()!;
        var token = document.RootElement.GetProperty("token").GetString()!;
        using var sessionless = new McpClient(endpoint, token); // never initialized → no session header
        var exchange = await sessionless.RawAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        Assert.Equal(400, exchange.Status);
    }

    /// <summary>JSON-RPC notifications receive 202 with no response body.</summary>
    [Fact]
    public async Task Notification_Returns202()
    {
        var exchange = await _fixture.Mcp.RawAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(202, exchange.Status);
        Assert.True(string.IsNullOrEmpty(exchange.ResponseJson));
    }

    /// <summary>
    /// G-MCP-PAGING (CR-011): §11.7 requires every data-returning resource to
    /// return at most one page (default 50, maximum 500) with a cursor; the
    /// live surface reads up to 200 rows with no cursor
    /// (McpResourceSurface.cs:92,165-181). Owner: phase-13 MCP surface rebuild.
    /// </summary>
    [Fact(Skip = "G-MCP-PAGING: default 200 rows / no cursor vs spec §11.7 (CR-011) default 50 / max 500 / cursor; owner phase-13 — see GAPS.md")]
    public async Task ResourceRead_PagingDrift()
    {
        var exchange = await _fixture.Mcp.ReadResourceAsync($"crawlforge://audits/{_fixture.AuditId}/high-severity-findings");
        var text = exchange.Result.GetProperty("contents")[0].GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.TryGetProperty("nextCursor", out _), "spec §11.7 requires a cursor");
    }

    private static JsonElement AssertToolSuccess(McpExchange exchange)
    {
        Assert.Equal(200, exchange.Status);
        Assert.False(exchange.IsError, "JSON-RPC envelope must be a success: " + exchange.ResponseJson);
        var result = exchange.Result;
        Assert.False(result.GetProperty("isError").GetBoolean(), "tool reported an error: " + result.GetProperty("content")[0].GetProperty("text").GetString());
        var content = result.GetProperty("content");
        Assert.True(content.GetArrayLength() >= 1);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        var text = content[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task AssertResourceAsync(string uri, string? expectedField)
    {
        var exchange = await _fixture.Mcp.ReadResourceAsync(uri);
        Assert.False(exchange.IsError, $"resource read failed: {exchange.ResponseJson}");
        var contents = exchange.Result.GetProperty("contents");
        Assert.Equal(1, contents.GetArrayLength());
        var entry = contents[0];
        Assert.Equal(uri, entry.GetProperty("uri").GetString());
        Assert.Equal("application/json", entry.GetProperty("mimeType").GetString());
        var text = entry.GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);
        if (expectedField is not null)
        {
            Assert.True(document.RootElement.TryGetProperty(expectedField, out _), $"resource payload missing '{expectedField}'");
        }

        var fileName = "mcp-resource-" + uri.Replace("crawlforge://", "", StringComparison.Ordinal)
            .Replace('/', '-').Replace('?', '-').Replace('=', '-') + ".json";
        await _fixture.SaveCaptureAsync(fileName, exchange.Result);
    }

    private async Task AssertPromptAsync(string name, object arguments)
    {
        var exchange = await _fixture.Mcp.GetPromptAsync(name, arguments);
        Assert.False(exchange.IsError, $"prompt get failed: {exchange.ResponseJson}");
        var messages = exchange.Result.GetProperty("messages");
        Assert.True(messages.GetArrayLength() >= 1);
        var message = messages[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content");
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(content.GetProperty("text").GetString()));
        await _fixture.SaveCaptureAsync($"mcp-prompt-{name}.json", exchange.Result);
    }
}
