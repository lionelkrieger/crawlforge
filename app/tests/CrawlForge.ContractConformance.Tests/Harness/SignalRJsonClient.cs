using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>One captured server invocation (hub event) with its receive time.</summary>
/// <param name="Name">Event name (the SignalR client-method target).</param>
/// <param name="Payload">First argument, parsed as JSON.</param>
/// <param name="ReceivedAt">Local receive timestamp (UTC).</param>
public sealed record CapturedHubEvent(string Name, JsonElement Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// Minimal SignalR JSON-protocol client over the BCL <see cref="ClientWebSocket"/>
/// (WP-08-03; no new dependency — Microsoft.AspNetCore.SignalR.Client is not in
/// Directory.Packages.props). Implements the documented handshake
/// ({"protocol":"json","version":1} + 0x1E record separator), invocations with
/// completion tracking, server invocations (the hub delivers each event as a
/// client method named after the event with the payload as single argument —
/// Api/Hubs/SignalRHubChannel.cs), and ping keep-alives.
///
/// This is the same transport the production frontend uses (store/hubClient.ts:
/// WebSockets transport + access_token query + X-CrawlForge-Session header).
/// </summary>
public sealed class SignalRJsonClient : IAsyncDisposable
{
    private const byte RecordSeparator = 0x1E;

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentQueue<CapturedHubEvent> _events = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _completions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _diagnostics = new();
    private Task? _receiveLoop;
    private int _invocationCounter;
    private int _disposed;

    /// <summary>All captured events in receive order (snapshot).</summary>
    public IReadOnlyList<CapturedHubEvent> Events => _events.ToArray();

    /// <summary>Protocol diagnostics (handshake + close reasons), for evidence.</summary>
    public string Diagnostics
    {
        get
        {
            lock (_diagnostics)
            {
                return _diagnostics.ToString();
            }
        }
    }

    /// <summary>Connects to the loopback hub and completes the JSON handshake.</summary>
    /// <param name="baseAddress">Backend loopback origin (http://127.0.0.1:port).</param>
    /// <param name="sessionToken">Per-launch session token (access_token query + header).</param>
    /// <param name="cancellationToken">Connect timeout token.</param>
    public async Task ConnectAsync(string baseAddress, string sessionToken, CancellationToken cancellationToken = default)
    {
        var uri = new UriBuilder(baseAddress.Replace("http://", "ws://", StringComparison.Ordinal))
        {
            Path = "/hubs/realtime",
            Query = "access_token=" + Uri.EscapeDataString(sessionToken)
        }.Uri;
        _socket.Options.SetRequestHeader("X-CrawlForge-Session", sessionToken);
        await _socket.ConnectAsync(uri, cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));

        // Handshake: protocol negotiation, terminated by the record separator.
        await SendRawAsync("""{"protocol":"json","version":1}""" + (char)RecordSeparator, cancellationToken);
        var handshake = await ReceiveRecordAsync(cancellationToken);
        lock (_diagnostics)
        {
            _diagnostics.AppendLine("handshake-response: " + handshake);
        }

        using var document = JsonDocument.Parse(handshake);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("SignalR handshake rejected: " + error.GetString());
        }
    }

    /// <summary>Invokes subscribe/unsubscribe with string audit ids (locked hub method shape).</summary>
    /// <param name="method">subscribe or unsubscribe.</param>
    /// <param name="auditIds">Audit identifiers as strings (CrawlForgeHub.Subscribe(string[])).</param>
    /// <param name="cancellationToken">Invocation timeout token.</param>
    public async Task InvokeChannelAsync(string method, IReadOnlyList<string> auditIds, CancellationToken cancellationToken = default)
    {
        var invocationId = Interlocked.Increment(ref _invocationCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completions[invocationId] = completion;

        var request = JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId,
            target = method,
            arguments = new object[] { auditIds }
        });
        await SendRawAsync(request + (char)RecordSeparator, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await completion.Task.WaitAsync(timeout.Token);
    }

    /// <summary>Waits until an event matching the predicate was captured (or timeout).</summary>
    /// <param name="name">Event name.</param>
    /// <param name="predicate">Optional payload filter.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <returns>The matching events captured so far (empty on timeout).</returns>
    public async Task<IReadOnlyList<CapturedHubEvent>> WaitForEventsAsync(
        string name, Func<JsonElement, bool>? predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var matches = _events.Where(e => e.Name == name && (predicate is null || predicate(e.Payload))).ToArray();
            if (matches.Length > 0)
            {
                return matches;
            }

            await Task.Delay(100);
        }

        return Array.Empty<CapturedHubEvent>();
    }

    /// <summary>Events captured for one name (snapshot).</summary>
    /// <param name="name">Event name.</param>
    /// <returns>Matching events in receive order.</returns>
    public IReadOnlyList<CapturedHubEvent> EventsNamed(string name) =>
        _events.Where(e => e.Name == name).ToArray();

    private async Task SendRawAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private async Task<string> ReceiveRecordAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var record = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Hub closed the connection during handshake.");
            }

            var segment = buffer.AsSpan(0, result.Count);
            var separatorIndex = segment.IndexOf(RecordSeparator);
            if (separatorIndex >= 0)
            {
                record.Write(segment[..separatorIndex]);
                return Encoding.UTF8.GetString(record.ToArray());
            }

            record.Write(segment);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        using var pending = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    lock (_diagnostics)
                    {
                        _diagnostics.AppendLine("close: " + _socket.CloseStatusDescription);
                    }

                    return;
                }

                pending.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = pending.ToArray();
                pending.SetLength(0);
                ProcessFrame(payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Harness shutdown.
        }
        catch (WebSocketException ex)
        {
            lock (_diagnostics)
            {
                _diagnostics.AppendLine("receive-error: " + ex.Message);
            }
        }
    }

    private void ProcessFrame(byte[] frame)
    {
        // One frame may carry several 0x1E-terminated records.
        var start = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            if (frame[i] != RecordSeparator)
            {
                continue;
            }

            var record = Encoding.UTF8.GetString(frame, start, i - start);
            start = i + 1;
            if (record.Length > 0)
            {
                ProcessRecord(record);
            }
        }
    }

    private void ProcessRecord(string record)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(record);
        }
        catch (JsonException)
        {
            lock (_diagnostics)
            {
                _diagnostics.AppendLine("unparseable: " + record);
            }

            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetInt32() : -1;
            switch (type)
            {
                case 1: // Invocation: hub event delivered as a client method.
                    var name = root.GetProperty("target").GetString() ?? "";
                    var arguments = root.GetProperty("arguments");
                    var payload = arguments.GetArrayLength() > 0
                        ? arguments[0].Clone()
                        : default;
                    _events.Enqueue(new CapturedHubEvent(name, payload, DateTimeOffset.UtcNow));
                    break;
                case 3: // Completion of a client invocation.
                    if (root.TryGetProperty("invocationId", out var idElement)
                        && idElement.GetString() is { } id
                        && _completions.TryRemove(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            completion.TrySetException(new InvalidOperationException("Hub invocation failed: " + error.GetString()));
                        }
                        else
                        {
                            completion.TrySetResult(true);
                        }
                    }

                    break;
                case 6: // Ping → pong.
                    _ = SendRawAsync("""{"type":6}""" + (char)RecordSeparator, CancellationToken.None);
                    break;
                case 7: // Close.
                    lock (_diagnostics)
                    {
                        _diagnostics.AppendLine("server-close: " + record);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "harness done", timeout.Token);
            }
        }
        catch (WebSocketException)
        {
            // Backend already gone.
        }
        catch (OperationCanceledException)
        {
            // Close handshake timed out; the socket is disposed below.
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // Receive loop blocked on a dead socket; disposal aborts it.
            }
        }

        _socket.Dispose();
        _lifetime.Dispose();
    }
}
