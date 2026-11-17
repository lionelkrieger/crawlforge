using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.E2E.Tests.Fixtures;
using CrawlForge.Host.Configuration;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.E2E.Tests;

/// <summary>
/// WP-07-01 criteria #16/#23 — delegated MCP flow over the REAL loopback
/// streamable-HTTP transport (FS-CRAWL-001 §11): bearer-token handshake,
/// initialize session negotiation, tools/list + tools/call against the live
/// engine, and the automation action log recording every invocation with
/// transport, target, bounded argument summary, and outcome — user-viewable
/// and purgeable, no identity dimensions.
/// </summary>
public sealed class McpDelegationE2ETests
{
    [Fact]
    public async Task DelegatedFlow_StartAndReAuditViaMcp_LogsEveryInvocation()
    {
        await using var origin = await LoopbackOrigin.StartAsync(routes =>
        {
            routes.Map("/", OriginResponse.Html(FixturePages.Page(
                "MCP delegate index",
                "<p>Delegated flow content.</p>" + FixturePages.Links(["/p/1"]))));
            routes.Map("/p/1", OriginResponse.Html(FixturePages.Page(
                "MCP delegate page", "<p>Delegated page content.</p>")));
        });
        await using var backend = await E2EBackend.StartAsync();
        var (projectId, configurationId) = await backend.SeedProjectAsync(origin.BaseUrl);

        using var client = new HttpClient();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        var session = await InitializeAsync(client, endpoint, token);

        // tools/list exposes exactly the eight lifecycle tools.
        var tools = await RpcAsync(client, endpoint, token, session, 2, "tools/list", null);
        var toolNames = tools.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { "analyze-single-page", "cancel-audit", "get-audit-progress", "pause-audit", "re-audit", "resume-audit", "run-schedule-now", "start-audit" },
            toolNames);

        // Delegated start: the agent starts the audit through MCP.
        var start = await RpcAsync(client, endpoint, token, session, 3, "tools/call", new
        {
            name = "start-audit",
            arguments = new { projectId, configurationId }
        });
        Assert.False(start.GetProperty("result").GetProperty("isError").GetBoolean());
        var startPayload = JsonDocument.Parse(start.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        var auditId = startPayload.RootElement.GetProperty("auditId").GetInt64();
        await backend.WaitForStatusAsync(auditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));

        // Delegated progress read.
        var progress = await RpcAsync(client, endpoint, token, session, 4, "tools/call", new
        {
            name = "get-audit-progress",
            arguments = new { auditId }
        });
        var progressPayload = JsonDocument.Parse(progress.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal("Completed", progressPayload.RootElement.GetProperty("status").GetString());

        // Delegated offline re-audit: zero additional network (criterion #15 via MCP).
        var requestsBefore = origin.RequestCount;
        var reaudit = await RpcAsync(client, endpoint, token, session, 5, "tools/call", new
        {
            name = "re-audit",
            arguments = new { auditId, refresh = false }
        });
        var reauditPayload = JsonDocument.Parse(reaudit.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        var newAuditId = reauditPayload.RootElement.GetProperty("auditId").GetInt64();
        Assert.Equal(auditId, reauditPayload.RootElement.GetProperty("sourceAuditId").GetInt64());
        await backend.WaitForStatusAsync(newAuditId, AuditStatus.Completed, TimeSpan.FromSeconds(60));
        Assert.Equal(requestsBefore, origin.RequestCount);

        // §11.5 action log: every invocation recorded with transport + outcome.
        var log = backend.Service<IAutomationActionLog>();
        var entries = (await log.ListAsync(new Contracts.Common.ListQuery(0, 100))).Items;
        Assert.Contains(entries, e => e.TargetName == "initialize" && e.Outcome == AutomationOutcome.Success);
        Assert.Contains(entries, e => e.TargetName == "tools/list" && e.Outcome == AutomationOutcome.Success);
        var toolCalls = entries.Where(e => e.TargetName == "tools/call").ToList();
        Assert.Equal(3, toolCalls.Count);
        Assert.All(toolCalls, e =>
        {
            Assert.Equal(AutomationOutcome.Success, e.Outcome);
            Assert.Equal(AutomationTransportKind.Http, e.Transport);
            Assert.Contains("\"tool\"", e.ArgumentsSummary);
            Assert.True(e.ArgumentsSummary.Length <= 512, "Argument summary exceeds the 512-char bound.");
        });
        Assert.Contains(toolCalls, e => e.ArgumentsSummary.Contains("start-audit"));
        Assert.Contains(toolCalls, e => e.ArgumentsSummary.Contains("re-audit"));

        // User-viewable and purgeable.
        var purged = await log.PurgeAsync();
        Assert.Equal(entries.Count, purged);
        Assert.Empty((await log.ListAsync(new Contracts.Common.ListQuery(0, 100))).Items);
    }

    [Fact]
    public async Task InvalidToken_IsRefusedWith401()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, _) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = RpcContent(1, "initialize", null)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "cf-wrong-token");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task TokenRotation_InvalidatesPriorClientsImmediately()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        var session = await InitializeAsync(client, endpoint, token);

        var rotated = await backend.Service<McpTokenService>().RotateAsync();
        Assert.NotEqual(token, rotated);

        // The prior token no longer authenticates, even with a live session.
        var stale = await client.SendAsync(SessionRequest(endpoint, RpcContent(9, "ping", null), token, session));
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        // The rotated token works.
        var fresh = await client.SendAsync(SessionRequest(endpoint, RpcContent(10, "ping", null), rotated, session));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task MissingSession_IsRejectedAfterInitialize()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        await InitializeAsync(client, endpoint, token);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = RpcContent(2, "tools/list", null)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DispatchLevelErrors_AreLoggedAsFailed()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        var session = await InitializeAsync(client, endpoint, token);

        // tools/call without the required "name" → JSON-RPC invalid params.
        var missingName = await RpcAsync(client, endpoint, token, session, 2, "tools/call", new
        {
            arguments = new { }
        });
        Assert.True(missingName.TryGetProperty("error", out _));

        // Unknown method → JSON-RPC method-not-found.
        var unknownMethod = await RpcAsync(client, endpoint, token, session, 3, "no/such-method", null);
        Assert.True(unknownMethod.TryGetProperty("error", out _));

        var entries = (await backend.Service<IAutomationActionLog>()
            .ListAsync(new Contracts.Common.ListQuery(0, 100))).Items;
        Assert.Contains(entries, e => e.TargetName == "tools/call" && e.Outcome == AutomationOutcome.Failed);
        Assert.Contains(entries, e => e.TargetName == "no/such-method" && e.Outcome == AutomationOutcome.Failed);
    }

    [Fact]
    public async Task ToolLevelFailures_AreLoggedAsFailed()
    {
        // §11.5: an invocation whose tool reports isError is a FAILED
        // invocation. Flips green when defect D7 (dispatcher marks only
        // JSON-RPC error envelopes as failed, ignoring result.isError) is fixed.
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        var session = await InitializeAsync(client, endpoint, token);

        // Unknown tool → isError payload.
        var unknown = await RpcAsync(client, endpoint, token, session, 2, "tools/call", new
        {
            name = "no-such-tool",
            arguments = new { }
        });
        Assert.True(unknown.GetProperty("result").GetProperty("isError").GetBoolean());

        // Missing required tool argument → isError payload.
        var missing = await RpcAsync(client, endpoint, token, session, 3, "tools/call", new
        {
            name = "start-audit",
            arguments = new { projectId = 1 }
        });
        Assert.True(missing.GetProperty("result").GetProperty("isError").GetBoolean());

        var entries = (await backend.Service<IAutomationActionLog>()
            .ListAsync(new Contracts.Common.ListQuery(0, 100))).Items;
        var failed = entries.Where(e => e.TargetName == "tools/call" && e.Outcome == AutomationOutcome.Failed).ToList();
        Assert.True(failed.Count == 2,
            $"Tool-level failures logged as Success (D7): expected 2 failed tools/call entries, got {failed.Count}.");
    }

    [Fact]
    public async Task Notification_IsAcceptedWithoutLogging()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        var session = await InitializeAsync(client, endpoint, token);
        var log = backend.Service<IAutomationActionLog>();
        var before = (await log.ListAsync(new Contracts.Common.ListQuery(0, 100))).Items.Count;

        // Notifications carry no id → 202, never an invocation.
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Mcp-Session-Id", session);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var after = (await log.ListAsync(new Contracts.Common.ListQuery(0, 100))).Items.Count;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task StdioTransportHeader_IsRecordedInTheActionLog()
    {
        await using var backend = await E2EBackend.StartAsync();
        var (endpoint, token) = await ReadDescriptorAsync(backend);
        using var client = new HttpClient();
        var session = await InitializeAsync(client, endpoint, token);

        var request = SessionRequest(endpoint, RpcContent(2, "ping", null), token, session);
        request.Headers.Add("X-MCP-Transport", "stdio");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = (await backend.Service<IAutomationActionLog>()
            .ListAsync(new Contracts.Common.ListQuery(0, 100))).Items;
        Assert.Contains(entries, e => e.TargetName == "ping" && e.Transport == AutomationTransportKind.Stdio);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<(string Endpoint, string Token)> ReadDescriptorAsync(E2EBackend backend)
    {
        var paths = backend.Service<CrawlForgePaths>();
        var descriptorPath = Path.Combine(paths.DataDirectory, "mcp-endpoint.json");
        Assert.True(File.Exists(descriptorPath), "MCP endpoint descriptor was not published at startup.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath));
        return (
            document.RootElement.GetProperty("endpoint").GetString()!,
            document.RootElement.GetProperty("token").GetString()!);
    }

    private static async Task<string> InitializeAsync(HttpClient client, string endpoint, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = RpcContent(1, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "cf-e2e", version = "1.0" }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var sessions), "initialize returned no session.");
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(body).RootElement.GetProperty("result");
        Assert.False(string.IsNullOrEmpty(result.GetProperty("protocolVersion").GetString()));
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        return sessions!.First();
    }

    private static async Task<JsonElement> RpcAsync(
        HttpClient client, string endpoint, string token, string session, int id, string method, object? parameters)
    {
        var response = await client.SendAsync(SessionRequest(endpoint, RpcContent(id, method, parameters), token, session));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"RPC {method} failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static StringContent RpcContent(int id, string method, object? parameters)
    {
        var payload = parameters is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(parameters, CamelCase)}}}""";
        return new StringContent(payload, Encoding.UTF8, "application/json");
    }

    private static HttpRequestMessage SessionRequest(string endpoint, StringContent content, string token, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Mcp-Session-Id", session);
        return request;
    }

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
