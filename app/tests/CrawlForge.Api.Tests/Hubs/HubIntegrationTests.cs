using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// TestServer integration tests for the SignalR adapter surface: real
/// hub route, JSON-protocol handshake, subscribe/unsubscribe channel,
/// per-audit delivery, broadcast events, and the live 2-second heartbeat.
/// Speaks the SignalR JSON protocol over the in-memory WebSocket pipeline
/// (no client package required).
/// </summary>
public sealed class HubIntegrationTests
{
    private sealed class HubServer : IAsyncDisposable
    {
        public WebApplication App { get; }

        public TestServer Server { get; }

        public RealtimeHubService Service { get; }

        public HubServer()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSignalR();
            builder.Services.AddTransient<CrawlForgeHub>();
            builder.Services.AddSingleton<ISystemHealthSampler>(new SystemHealthSampler());
            builder.Services.AddSingleton<IHubClientChannel>(provider =>
                new SignalRHubChannel(provider.GetRequiredService<IHubContext<CrawlForgeHub>>()));
            builder.Services.AddSingleton(provider => new RealtimeHubService(
                provider.GetRequiredService<IHubClientChannel>(),
                provider.GetRequiredService<ISystemHealthSampler>()));
            builder.Services.AddSingleton<IRealtimeHub>(provider => provider.GetRequiredService<RealtimeHubService>());

            App = builder.Build();
            App.UseRouting();
            App.MapHub<CrawlForgeHub>(CrawlForgeHub.HubRoutePath);
            App.StartAsync().GetAwaiter().GetResult();
            Server = App.GetTestServer();
            Service = Server.Services.GetRequiredService<RealtimeHubService>();
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class JsonProtocolClient : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly List<byte> _buffer = [];

        private JsonProtocolClient(WebSocket socket) => _socket = socket;

        public static async Task<JsonProtocolClient> ConnectAsync(TestServer server, CancellationToken cancellationToken)
        {
            var socket = await server.CreateWebSocketClient()
                .ConnectAsync(new Uri("ws://localhost" + CrawlForgeHub.HubRoutePath), cancellationToken);
            var client = new JsonProtocolClient(socket);
            await client.SendAsync("""{"protocol":"json","version":1}""", cancellationToken);
            var handshake = await client.ReceiveAsync(cancellationToken);
            Assert.Equal(JsonValueKind.Object, handshake.RootElement.ValueKind);
            return client;
        }

        public Task SendAsync(string json, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\u001e");
            return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        public async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var separator = _buffer.IndexOf(0x1E);
                if (separator >= 0)
                {
                    var frame = Encoding.UTF8.GetString(_buffer.GetRange(0, separator).ToArray());
                    _buffer.RemoveRange(0, separator + 1);
                    if (frame.Length > 0)
                    {
                        return JsonDocument.Parse(frame);
                    }

                    continue;
                }

                var chunk = new byte[4096];
                var result = await _socket.ReceiveAsync(chunk, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Hub closed the connection.");
                }

                _buffer.AddRange(chunk.AsSpan(0, result.Count).ToArray());
            }
        }

        public async Task<JsonDocument> ReceiveEventAsync(string target, CancellationToken cancellationToken)
        {
            while (true)
            {
                using var frame = await ReceiveAsync(cancellationToken);
                if (frame.RootElement.TryGetProperty("type", out var type) && type.GetInt32() == 1
                    && frame.RootElement.TryGetProperty("target", out var name)
                    && name.GetString() == target)
                {
                    return JsonDocument.Parse(frame.RootElement.GetRawText());
                }
                // Skip pings, handshake leftovers, and other events.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-done", timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Server may close first; the test outcome is unaffected.
                }
            }

            _socket.Dispose();
        }
    }

    private static CancellationToken Timeout(int seconds = 10) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [Fact]
    public async Task HubRoute_SubscribeAndPerAuditDelivery_WorksEndToEnd()
    {
        await using var server = new HubServer();
        await using var client = await JsonProtocolClient.ConnectAsync(server.Server, Timeout());

        await client.SendAsync("""{"type":1,"invocationId":"1","target":"Subscribe","arguments":[["100"]]}""", Timeout());
        // Let the group join settle before publishing.
        await Task.Delay(200, Timeout());

        await server.Service.PublishAsync(new HubEvent(
            HubEventCatalog.CrawlProgress,
            """{"auditId":100,"counters":{"fetched":5},"phase":"crawling"}"""), Timeout());

        using var frame = await client.ReceiveEventAsync(HubEventCatalog.CrawlProgress, Timeout());
        var payload = frame.RootElement.GetProperty("arguments")[0];
        Assert.Equal(JsonValueKind.Object, payload.ValueKind); // Structured, not double-encoded.
        // WP-09-06 (G-G): entity ids are string EntityIds on the wire.
        Assert.Equal("100", payload.GetProperty("auditId").GetString());
        // WP-09-06 (G-HUB-COUNTERS): counters carry the REST wire keys.
        var counters = payload.GetProperty("counters");
        Assert.Equal(5, counters.GetProperty("fetched").GetInt32());
        Assert.Equal(0, counters.GetProperty("queued").GetInt32());
        Assert.Equal(0, counters.GetProperty("error").GetInt32());
        Assert.Equal(0, counters.GetProperty("blocked").GetInt32());
    }

    [Fact]
    public async Task BroadcastEvent_ReceivedWithoutSubscription()
    {
        await using var server = new HubServer();
        await using var client = await JsonProtocolClient.ConnectAsync(server.Server, Timeout());

        await server.Service.PublishAsync(new HubEvent(
            HubEventCatalog.BackendLifecycle,
            """{"state":"ready"}"""), Timeout());

        using var frame = await client.ReceiveEventAsync(HubEventCatalog.BackendLifecycle, Timeout());
        Assert.Equal("ready", frame.RootElement.GetProperty("arguments")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unsubscribe_StopsPerAuditDelivery()
    {
        await using var server = new HubServer();
        await using var client = await JsonProtocolClient.ConnectAsync(server.Server, Timeout());

        await client.SendAsync("""{"type":1,"invocationId":"1","target":"Subscribe","arguments":[["100"]]}""", Timeout());
        await Task.Delay(200, Timeout());
        await client.SendAsync("""{"type":1,"invocationId":"2","target":"Unsubscribe","arguments":[["100"]]}""", Timeout());
        await Task.Delay(200, Timeout());

        await server.Service.PublishAsync(new HubEvent(
            HubEventCatalog.CrawlProgress,
            """{"auditId":100,"counters":{"fetched":9},"phase":"crawling"}"""), Timeout());

        // No crawl-progress frame may arrive; the heartbeat keeps flowing.
        using var heartbeat = await client.ReceiveEventAsync(HubEventCatalog.SystemHealth, Timeout(6));
        Assert.Equal(HubEventCatalog.SystemHealth, heartbeat.RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task SystemHealthHeartbeat_ArrivesLiveEvery2Seconds()
    {
        await using var server = new HubServer();
        await using var client = await JsonProtocolClient.ConnectAsync(server.Server, Timeout());

        using var first = await client.ReceiveEventAsync(HubEventCatalog.SystemHealth, Timeout(6));
        var payload = first.RootElement.GetProperty("arguments")[0];
        Assert.True(payload.GetProperty("backendWorkingSetBytes").GetInt64() > 0);
        Assert.True(payload.GetProperty("managedHeapBytes").GetInt64() > 0);
        Assert.True(payload.TryGetProperty("gcGen0Count", out _));
        Assert.True(payload.TryGetProperty("timestamp", out _));

        using var second = await client.ReceiveEventAsync(HubEventCatalog.SystemHealth, Timeout(6));
        Assert.Equal(HubEventCatalog.SystemHealth, second.RootElement.GetProperty("target").GetString());
    }
}
