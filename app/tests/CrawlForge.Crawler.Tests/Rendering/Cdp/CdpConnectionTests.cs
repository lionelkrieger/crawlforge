using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Direct CDP WebSocket client (D-002): loopback-only boundary guard and
/// command/response correlation against the scripted CDP server.
/// </summary>
public sealed class CdpConnectionTests
{
    [Theory]
    [InlineData("ws://example.test/devtools/browser/abc")]
    [InlineData("ws://192.168.1.5:9222/devtools/browser/abc")]
    [InlineData("wss://127.0.0.1:9222/devtools/browser/abc")]
    [InlineData("http://127.0.0.1:9222/devtools/browser/abc")]
    public async Task ConnectAsync_RejectsNonLoopbackOrNonWsEndpoints(string endpoint)
    {
        await using var connection = new CdpConnection();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            connection.ConnectAsync(new Uri(endpoint)));
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task CommandResponse_IsCorrelatedById()
    {
        await using var server = FakeCdpServer.Start();
        await using var connection = new CdpConnection();
        await connection.ConnectAsync(server.Endpoint);

        var result = await connection.SendCommandAsync("Browser.getVersion");
        Assert.Contains("HeadlessChrome", result.GetProperty("product").GetString());
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task Events_AreDispatched_WithSessionId()
    {
        await using var server = FakeCdpServer.Start(options =>
        {
            options.OnNavigate = async connection =>
            {
                await connection.EmitAsync("Network.requestWillBeSent", new Dictionary<string, object?>
                {
                    ["requestId"] = "evt-1",
                    ["type"] = "Document",
                    ["request"] = new Dictionary<string, object?> { ["url"] = "http://127.0.0.1:9/", ["method"] = "GET" },
                });
                await connection.EmitAsync("Page.frameNavigated", new Dictionary<string, object?>
                {
                    ["frame"] = new Dictionary<string, object?> { ["id"] = "f", ["url"] = "http://127.0.0.1:9/" },
                });
            };
        });

        await using var connection = new CdpConnection();
        var events = new List<CdpEvent>();
        connection.EventReceived += e => { lock (events) { events.Add(e); } };
        await connection.ConnectAsync(server.Endpoint);

        var attach = await connection.SendCommandAsync("Target.attachToTarget", new Dictionary<string, object?>
        {
            ["targetId"] = "t1",
            ["flatten"] = true,
        });
        var sessionId = attach.GetProperty("sessionId").GetString();

        await connection.SendCommandAsync("Page.navigate", new Dictionary<string, object?>
        {
            ["url"] = "http://127.0.0.1:9/",
        }, sessionId);

        // Wait for the scripted events to arrive.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                if (events.Count >= 2)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (events)
        {
            Assert.Contains(events, e => e.Method == "Network.requestWillBeSent" && e.SessionId == sessionId);
            Assert.Contains(events, e => e.Method == "Page.frameNavigated" && e.SessionId == sessionId);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var server = FakeCdpServer.Start();
        var connection = new CdpConnection();
        await connection.ConnectAsync(server.Endpoint);
        await connection.DisposeAsync();
        await connection.DisposeAsync();
        Assert.False(connection.IsConnected);
    }
}
