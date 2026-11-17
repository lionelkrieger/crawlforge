using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrawlForge.Boundary.Tests.Harness;
using CrawlForge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Boundary.Tests.Mcp;

/// <summary>
/// Acceptance criterion #16: the MCP surface is loopback-only, token-gated on
/// both transports (SSE-capable HTTP and the stdio bridge), rotation
/// immediately invalidates the previous token, and non-loopback binds are
/// rejected by construction.
/// </summary>
public sealed class McpBoundaryTests
{
    private const string InitializePayload =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""";

    [Fact]
    public async Task Mcp_RequiresToken_OnBothTransports_AndRotatesImmediately()
    {
        await using var fixture = await BackendFixture.StartAsync();
        var (endpoint, descriptorToken) = ReadDescriptor(fixture.DataDirectory);
        var tokens = fixture.Backend.App.Services.GetRequiredService<McpTokenService>();
        Assert.Equal(tokens.CurrentToken, descriptorToken);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // No token → 401.
        using (var anonymous = await client.PostAsync(endpoint, Content(InitializePayload)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        // Wrong token → 401.
        using (var wrong = await PostAsync(client, endpoint, InitializePayload, "not-the-token"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Correct token → 200 on the HTTP transport.
        using (var ok = await PostAsync(client, endpoint, InitializePayload, descriptorToken))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            Assert.Equal("2.0", body.RootElement.GetProperty("jsonrpc").GetString());
        }

        // Correct token → 200 on the stdio-bridge transport marker.
        using (var stdioRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            stdioRequest.Content = Content(InitializePayload);
            stdioRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptorToken);
            stdioRequest.Headers.Add("X-MCP-Transport", "stdio");
            using var stdio = await client.SendAsync(stdioRequest);
            Assert.Equal(HttpStatusCode.OK, stdio.StatusCode);
        }

        // SSE channel with token → event stream.
        using (var sseRequest = new HttpRequestMessage(HttpMethod.Get, endpoint))
        {
            sseRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptorToken);
            using var sse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, sse.StatusCode);
            Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
        }

        // SSE channel without token → 401.
        using (var anonymousSse = await client.GetAsync(endpoint))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousSse.StatusCode);
        }

        // Rotation: previous token invalid immediately, new token works.
        var newToken = await tokens.RotateAsync();
        Assert.NotEqual(descriptorToken, newToken);
        using (var stale = await PostAsync(client, endpoint, InitializePayload, descriptorToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        using (var rotated = await PostAsync(client, endpoint, InitializePayload, newToken))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }

        // The persisted token is protected at rest (never plaintext).
        var tokenFile = Path.Combine(fixture.DataDirectory, "mcp-token.json");
        Assert.True(File.Exists(tokenFile));
        var persisted = await File.ReadAllTextAsync(tokenFile);
        Assert.DoesNotContain(newToken, persisted);
        Assert.Contains("cfsec1:", persisted);

        EvidenceWriter.WriteMarkdown(
            "mcp-boundary.md",
            $"""
            # Criterion #16 — MCP boundary

            - Endpoint: {endpoint} (loopback)
            - Anonymous POST: 401; wrong token: 401; valid token: 200 (HTTP + stdio transports)
            - SSE with token: 200 text/event-stream; anonymous SSE: 401
            - Rotation: old token 401 immediately; new token 200
            - Token at rest: protected (cfsec1: scheme), plaintext absent from mcp-token.json
            """);
    }

    [Fact]
    public async Task McpServer_RejectsNonLoopbackBind_ByConstruction()
    {
        await using var fixture = await BackendFixture.StartAsync();
        var dispatcher = fixture.Backend.App.Services.GetRequiredService<McpJsonRpcDispatcher>();
        var tokens = fixture.Backend.App.Services.GetRequiredService<McpTokenService>();

        foreach (var forbidden in new[] { IPAddress.Any, IPAddress.IPv6Any, IPAddress.Parse("0.0.0.0") })
        {
            Assert.Throws<ArgumentException>(() => new McpLoopbackServer(dispatcher, tokens, forbidden));
        }

        // A non-loopback literal address is rejected too.
        Assert.Throws<ArgumentException>(() =>
            new McpLoopbackServer(dispatcher, tokens, IPAddress.Parse("192.0.2.1")));

        await Task.CompletedTask;
    }

    private static (string Endpoint, string Token) ReadDescriptor(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "mcp-endpoint.json");
        Assert.True(File.Exists(path), "MCP descriptor must exist after backend start.");
        using var descriptor = JsonDocument.Parse(File.ReadAllText(path));
        var endpoint = descriptor.RootElement.GetProperty("endpoint").GetString()!;
        var token = descriptor.RootElement.GetProperty("token").GetString()!;
        Assert.True(new Uri(endpoint).IsLoopback, "MCP endpoint must be loopback.");
        return (endpoint, token);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string endpoint, string payload, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = Content(payload);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static StringContent Content(string payload) => new(payload, Encoding.UTF8, "application/json");
}
