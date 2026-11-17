using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CrawlForge.Mcp;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Loopback transport enforcement over real HTTP (FS-CRAWL-001 §11.2,
/// criterion #16): loopback-only binding, bearer-token authentication,
/// rotation invalidation, session enforcement, and the SSE channel.
/// </summary>
public class McpLoopbackServerTests : IClassFixture<McpSqliteStackFixture>
{
    private readonly McpSqliteStackFixture _stack;

    /// <summary>Creates the suite.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpLoopbackServerTests(McpSqliteStackFixture stack)
    {
        _stack = stack;
    }

    private static readonly string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"wire-test","version":"0"}}}""";

    private async Task<(McpLoopbackServer Server, McpTokenService Tokens, HttpClient Client)> StartAsync()
    {
        var host = new McpTestHost(_stack);
        await host.Tokens.InitializeAsync();
        var server = new McpLoopbackServer(host.Dispatcher, host.Tokens);
        server.Start();
        var client = new HttpClient { BaseAddress = new Uri(server.EndpointPrefix), Timeout = TimeSpan.FromSeconds(15) };
        return (server, host.Tokens, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    public void NonLoopbackBindAddressesAreRejected(string address)
    {
        var host = new McpTestHost(_stack);
        Assert.Throws<ArgumentException>(() =>
            new McpLoopbackServer(host.Dispatcher, host.Tokens, IPAddress.Parse(address)));
    }

    [Fact]
    public void LoopbackBindAddressesAreAccepted()
    {
        var host = new McpTestHost(_stack);
        using var v4 = new McpLoopbackServer(host.Dispatcher, host.Tokens, IPAddress.Loopback);
        using var v6 = new McpLoopbackServer(host.Dispatcher, host.Tokens, IPAddress.IPv6Loopback);
        Assert.StartsWith("http://127.0.0.1:", v4.EndpointPrefix);
        Assert.StartsWith("http://[::1]:", v6.EndpointPrefix);
    }

    [Fact]
    public async Task ValidTokenCompletesHandshakeOverHttp()
    {
        var (server, tokens, client) = await StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues));
            Assert.NotEmpty(sessionValues!);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("crawlforge", body);
            Assert.Contains("2025-06-18", body);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Theory]
    [InlineData("invalid-token")]
    [InlineData(null)]
    public async Task InvalidOrMissingTokenIsRejected(string? token)
    {
        var (server, _, client) = await StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task TokenRotationInvalidatesPriorClientsOverTheWire()
    {
        var (server, tokens, client) = await StartAsync();
        try
        {
            var oldToken = tokens.CurrentToken;
            using (var ok = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) })
            {
                ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
                using var okResponse = await client.SendAsync(ok);
                Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
            }

            var newToken = await tokens.RotateAsync();

            using (var stale = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) })
            {
                stale.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
                using var staleResponse = await client.SendAsync(stale);
                Assert.Equal(HttpStatusCode.Unauthorized, staleResponse.StatusCode); // criterion #16
            }

            using (var fresh = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) })
            {
                fresh.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                using var freshResponse = await client.SendAsync(fresh);
                Assert.Equal(HttpStatusCode.OK, freshResponse.StatusCode);
            }
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task SessionIdIsRequiredAfterInitialize()
    {
        var (server, tokens, client) = await StartAsync();
        try
        {
            string sessionId;
            using (var init = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) })
            {
                init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                using var initResponse = await client.SendAsync(init);
                sessionId = initResponse.Headers.GetValues("Mcp-Session-Id").First();
            }

            using (var missing = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""),
            })
            {
                missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                using var missingResponse = await client.SendAsync(missing);
                Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
            }

            using (var withSession = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = Json("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}"""),
            })
            {
                withSession.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                withSession.Headers.Add("Mcp-Session-Id", sessionId);
                using var sessionResponse = await client.SendAsync(withSession);
                Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
                var body = await sessionResponse.Content.ReadAsStringAsync();
                Assert.Contains("start-audit", body);
            }
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task SseChannelOpensWithEventStreamContentType()
    {
        var (server, tokens, client) = await StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "mcp");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            Assert.Contains("keep-alive", Encoding.UTF8.GetString(buffer, 0, read));
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    [Fact]
    public async Task NotificationsReturn202AndUnknownPathsReturn404()
    {
        var (server, tokens, client) = await StartAsync();
        try
        {
            string sessionId;
            using (var init = new HttpRequestMessage(HttpMethod.Post, "mcp") { Content = Json(InitializeRequest) })
            {
                init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                using var initResponse = await client.SendAsync(init);
                sessionId = initResponse.Headers.GetValues("Mcp-Session-Id").First();
            }

            using (var notification = new HttpRequestMessage(HttpMethod.Post, "mcp")
            {
                Content = Json("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""),
            })
            {
                notification.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                notification.Headers.Add("Mcp-Session-Id", sessionId);
                using var response = await client.SendAsync(notification);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }

            using (var notFound = new HttpRequestMessage(HttpMethod.Get, "elsewhere"))
            {
                notFound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                using var response = await client.SendAsync(notFound);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var delete = new HttpRequestMessage(HttpMethod.Delete, "mcp"))
            {
                delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CurrentToken);
                delete.Headers.Add("Mcp-Session-Id", sessionId);
                using var response = await client.SendAsync(delete);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }
}
