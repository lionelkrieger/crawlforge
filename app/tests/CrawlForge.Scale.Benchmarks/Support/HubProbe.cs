using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>
/// Minimal SignalR JSON-protocol client over a real loopback WebSocket — the
/// same wire the renderer's hub client speaks (handshake record separator 0x1E,
/// type-1 invocation frames). No client package required; used to measure the
/// live 2-second system-health heartbeat against the real Kestrel backend.
/// </summary>
public sealed class HubProbe : IAsyncDisposable
{
    private const char RecordSeparator = (char)0x1E;

    private readonly ClientWebSocket _socket;
    private readonly List<byte> _buffer = [];

    private HubProbe(ClientWebSocket socket) => _socket = socket;

    /// <summary>Connects to the realtime hub with the loopback session token.</summary>
    /// <param name="baseAddress">Backend loopback origin.</param>
    /// <param name="sessionToken">Per-launch session token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handshake-completed probe.</returns>
    public static async Task<HubProbe> ConnectAsync(string baseAddress, string sessionToken, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        var uri = new UriBuilder(baseAddress)
        {
            Scheme = "ws",
            Path = "/hubs/realtime",
            Query = "access_token=" + Uri.EscapeDataString(sessionToken)
        }.Uri;
        await socket.ConnectAsync(uri, cancellationToken);

        var probe = new HubProbe(socket);
        await probe.SendAsync("""{"protocol":"json","version":1}""", cancellationToken);
        using var handshake = await probe.ReceiveAsync(cancellationToken);
        if (handshake.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Hub handshake did not complete.");
        }
        return probe;
    }

    /// <summary>Sends one JSON-protocol frame.</summary>
    /// <param name="json">Frame payload (without the record separator).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SendAsync(string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json + RecordSeparator);
        return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>Receives the next complete JSON-protocol frame.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed frame.</returns>
    public async Task<JsonDocument> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var separator = _buffer.IndexOf((byte)RecordSeparator);
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

            var chunk = new byte[8192];
            var result = await _socket.ReceiveAsync(chunk, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Hub closed the connection.");
            }
            _buffer.AddRange(chunk.AsSpan(0, result.Count).ToArray());
        }
    }

    /// <summary>Waits for the next invocation frame with the given target name.</summary>
    /// <param name="target">Server event name (e.g. system-health).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The invocation frame (arguments carry the payload).</returns>
    public async Task<JsonDocument> ReceiveEventAsync(string target, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReceiveAsync(cancellationToken);
            if (frame.RootElement.TryGetProperty("type", out var type) && type.GetInt32() == 1
                && frame.RootElement.TryGetProperty("target", out var name)
                && name.GetString() == target)
            {
                return frame;
            }
            frame.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bench-done", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Server may close first; the measurement outcome is unaffected.
            }
        }
        _socket.Dispose();
    }
}
