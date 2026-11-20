using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// One Chrome DevTools Protocol event received over the browser WebSocket
/// (D-002: direct CDP, no third-party automation library).
/// </summary>
/// <param name="Method">CDP event name (for example <c>Network.requestWillBeSent</c>).</param>
/// <param name="Params">Event payload.</param>
/// <param name="SessionId">Flat-mode session the event belongs to, when any.</param>
public sealed record CdpEvent(string Method, JsonElement Params, string? SessionId);

/// <summary>
/// Direct CDP WebSocket client (FS-CRAWL-001 §5.6.2, D-002/CR-006). Speaks the
/// Chrome DevTools Protocol over a single loopback WebSocket: commands are
/// correlated by id, events are dispatched to subscribers, and flat-mode
/// sessions (<c>Target.attachToTarget</c> with <c>flatten: true</c>) multiplex
/// page targets over the one browser connection.
/// <para>
/// Boundary posture (§2.6): the endpoint MUST be loopback — the DevTools
/// endpoint of the locally launched browser. A non-loopback endpoint is
/// rejected before any connection attempt; this client never talks to a
/// remote service.
/// </para>
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private static readonly JsonSerializerOptions CommandJsonOptions = new()
    {
        // CDP parameter names are camelCase literals supplied by the caller;
        // null-valued optional parameters are omitted.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private long _nextId;
    private Task? _receiveLoop;
    private int _disposed;

    /// <summary>Raised for every CDP event (never for command responses).</summary>
    public event Action<CdpEvent>? EventReceived;

    /// <summary>Whether the WebSocket is currently open.</summary>
    public bool IsConnected => _socket.State == WebSocketState.Open;

    /// <summary>
    /// Connects to a local browser DevTools endpoint.
    /// </summary>
    /// <param name="endpoint">The <c>ws://127.0.0.1:port/devtools/browser/…</c> endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The endpoint is not a loopback ws:// address.</exception>
    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != "ws"
            || !IPAddress.TryParse(endpoint.Host, out var address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                $"CDP endpoint '{endpoint}' is not a loopback ws:// address. The rendering control layer only controls a locally launched browser (§2.6).",
                nameof(endpoint));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _socket.ConnectAsync(endpoint, linked.Token);
        _receiveLoop = ReceiveLoopAsync(_disposeCts.Token);
    }

    /// <summary>
    /// Sends one CDP command and awaits its response.
    /// </summary>
    /// <param name="method">CDP method (for example <c>Page.navigate</c>).</param>
    /// <param name="parameters">CamelCase parameter document, when any.</param>
    /// <param name="sessionId">Flat-mode session id for page-scoped commands, when any.</param>
    /// <param name="timeout">Per-command timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The command result payload.</returns>
    /// <exception cref="CdpCommandException">The browser returned a CDP error.</exception>
    /// <exception cref="OperationCanceledException">Timed out or cancelled.</exception>
    public async Task<JsonElement> SendCommandAsync(
        string method,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? sessionId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException("The CDP connection is not open.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var message = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is { Count: > 0 })
        {
            message["params"] = parameters;
        }

        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        await using var registration = timeoutCts.Token.Register(() => completion.TrySetCanceled(timeoutCts.Token));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, CommandJsonOptions);
        await _sendLock.WaitAsync(timeoutCts.Token);
        try
        {
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token);
        }
        finally
        {
            _sendLock.Release();
        }

        try
        {
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        _pending.Clear();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "render pool shutdown", closeCts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Browser already gone.
        }

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Expected during shutdown.
            }
        }

        _socket.Dispose();
        _disposeCts.Dispose();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailAllPending(new IOException("The browser closed the CDP connection."));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                DispatchMessage(message.ToArray());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            FailAllPending(ex);
        }
    }

    private void DispatchMessage(byte[] payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return; // A malformed frame never takes the connection down.
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
            {
                if (!_pending.TryRemove(id, out var completion))
                {
                    return;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var messageText = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown CDP error";
                    completion.TrySetException(new CdpCommandException(messageText ?? "unknown CDP error"));
                }
                else
                {
                    completion.TrySetResult(root.TryGetProperty("result", out var result)
                        ? result.Clone()
                        : default);
                }

                return;
            }

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                string? sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                EventReceived?.Invoke(new CdpEvent(method, parameters, sessionId));
            }
        }
    }

    private void FailAllPending(Exception cause)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(cause);
        }

        _pending.Clear();
    }
}

/// <summary>A CDP command error returned by the browser (local diagnostic only).</summary>
public sealed class CdpCommandException : Exception
{
    /// <summary>Creates the error.</summary>
    /// <param name="message">The browser's local error message.</param>
    public CdpCommandException(string message)
        : base(message)
    {
    }
}
