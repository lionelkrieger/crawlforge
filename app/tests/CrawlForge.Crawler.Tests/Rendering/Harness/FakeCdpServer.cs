using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Scripted loopback CDP browser double (D-002 component tests): a real
/// WebSocket server on 127.0.0.1 speaking the Chrome DevTools Protocol with
/// Target/Page/Network/Runtime/ServiceWorker/Fetch domains. Tests drive the
/// REAL CdpConnection/CdpBrowser/CdpPageSession/HeadlessRenderingPool against
/// it — no real browser, no downloads, fully deterministic event scripts.
/// </summary>
public sealed class FakeCdpServer : IAsyncDisposable
{
    /// <summary>Minimal valid 1×1 PNG (screenshot double).</summary>
    public static byte[] MinimalPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<FakeCommand> _commands = [];
    private readonly object _commandLock = new();
    private int _concurrentNavigates;
    private int _maxConcurrentNavigates;
    private int _connections;

    /// <summary>Script options (mutable before/during renders).</summary>
    public FakeCdpOptions Options { get; } = new();

    /// <summary>The loopback DevTools endpoint the pool connects to.</summary>
    public Uri Endpoint { get; }

    /// <summary>Number of WebSocket connections accepted (browser launches).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Peak concurrent Page.navigate handlers observed.</summary>
    public int MaxConcurrentNavigates => Volatile.Read(ref _maxConcurrentNavigates);

    /// <summary>All commands received across connections, in order.</summary>
    public IReadOnlyList<FakeCommand> Commands
    {
        get
        {
            lock (_commandLock)
            {
                return [.. _commands];
            }
        }
    }

    private FakeCdpServer(HttpListener listener, Uri endpoint)
    {
        _listener = listener;
        Endpoint = endpoint;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>Starts the scripted CDP browser on a free loopback port.</summary>
    /// <param name="configure">Optional script configuration.</param>
    /// <returns>The running fake browser.</returns>
    public static FakeCdpServer Start(Action<FakeCdpOptions>? configure = null)
    {
        var port = LoopbackPageServer.GetFreeLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = new FakeCdpServer(listener, new Uri($"ws://127.0.0.1:{port}/devtools/browser/fake-browser"));
        configure?.Invoke(server.Options);
        return server;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // Expected during shutdown.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or WebSocketException)
            {
                continue;
            }

            Interlocked.Increment(ref _connections);
            _ = HandleConnectionAsync(wsContext.WebSocket, _cts.Token);
        }
    }

    private async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var session = new FakeConnection(this, socket);
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                var id = root.GetProperty("id").GetInt64();
                var method = root.GetProperty("method").GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                var sessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                lock (_commandLock)
                {
                    _commands.Add(new FakeCommand(method, sessionId, parameters));
                }

                await session.HandleCommandAsync(id, method, parameters, sessionId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or JsonException)
        {
            // Connection ended.
        }
        finally
        {
            socket.Dispose();
        }
    }

    internal void RecordNavigateStart()
    {
        var now = Interlocked.Increment(ref _concurrentNavigates);
        lock (_commandLock)
        {
            _maxConcurrentNavigates = Math.Max(_maxConcurrentNavigates, now);
        }
    }

    internal void RecordNavigateEnd() => Interlocked.Decrement(ref _concurrentNavigates);

    /// <summary>One command observed by the fake browser.</summary>
    /// <param name="Method">CDP method.</param>
    /// <param name="SessionId">Flat session id, when page-scoped.</param>
    /// <param name="Params">Command parameters.</param>
    public sealed record FakeCommand(string Method, string? SessionId, JsonElement Params);

    /// <summary>One WebSocket control connection (a fake browser instance).</summary>
    public sealed class FakeConnection
    {
        private readonly FakeCdpServer _server;
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string? _lastSessionId;
        private string _navigatedUrl = "about:blank";
        private long _domMutations;
        private bool _fetchEnabled;
        private int _contextCounter;
        private int _targetCounter;

        public FakeConnection(FakeCdpServer server, WebSocket socket)
        {
            _server = server;
            _socket = socket;
        }

        public async Task HandleCommandAsync(long id, string method, JsonElement parameters, string? sessionId, CancellationToken cancellationToken)
        {
            var options = _server.Options;
            switch (method)
            {
                case "Browser.getVersion":
                    await RespondAsync(id, new Dictionary<string, object?>
                    {
                        ["product"] = "HeadlessChrome/131.0.6778.85",
                        ["userAgent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/131.0.0.0 Safari/537.36",
                        ["protocolVersion"] = "1.3",
                    }, cancellationToken);
                    return;
                case "Target.createBrowserContext":
                    await RespondAsync(id, new Dictionary<string, object?>
                    {
                        ["browserContextId"] = $"ctx-{Interlocked.Increment(ref _contextCounter)}",
                    }, cancellationToken);
                    return;
                case "Target.createTarget":
                    await RespondAsync(id, new Dictionary<string, object?>
                    {
                        ["targetId"] = $"target-{Interlocked.Increment(ref _targetCounter)}",
                    }, cancellationToken);
                    return;
                case "Target.attachToTarget":
                    _lastSessionId = $"session-{_targetCounter}";
                    await RespondAsync(id, new Dictionary<string, object?>
                    {
                        ["sessionId"] = _lastSessionId,
                    }, cancellationToken);
                    return;
                case "Target.closeTarget":
                    await RespondAsync(id, new Dictionary<string, object?> { ["success"] = true }, cancellationToken);
                    return;
                case "Target.disposeBrowserContext":
                    await RespondAsync(id, new Dictionary<string, object?>(), cancellationToken);
                    return;
                case "Fetch.enable":
                    _fetchEnabled = true;
                    await RespondAsync(id, new Dictionary<string, object?>(), cancellationToken);
                    return;
                case "Page.navigate":
                    await HandleNavigateAsync(id, parameters, cancellationToken);
                    return;
                case "Runtime.evaluate":
                    await HandleEvaluateAsync(id, parameters, cancellationToken);
                    return;
                case "Page.captureScreenshot":
                    await RespondAsync(id, new Dictionary<string, object?>
                    {
                        ["data"] = Convert.ToBase64String(options.Screenshot),
                    }, cancellationToken);
                    return;
                default:
                    // Page/Network/Runtime/ServiceWorker/Fetch/Emulation enables
                    // and setters all succeed with an empty result.
                    await RespondAsync(id, new Dictionary<string, object?>(), cancellationToken);
                    return;
            }
        }

        private async Task HandleNavigateAsync(long id, JsonElement parameters, CancellationToken cancellationToken)
        {
            var options = _server.Options;
            _navigatedUrl = parameters.TryGetProperty("url", out var url) ? url.GetString() ?? "about:blank" : "about:blank";

            if (options.StallNavigate)
            {
                return; // Never respond: the render timeout must fire.
            }

            if (options.NavigateError is not null)
            {
                await RespondAsync(id, new Dictionary<string, object?>
                {
                    ["frameId"] = "frame-1",
                    ["loaderId"] = "loader-1",
                    ["errorText"] = options.NavigateError,
                }, cancellationToken);
                return;
            }

            _server.RecordNavigateStart();
            await RespondAsync(id, new Dictionary<string, object?>
            {
                ["frameId"] = "frame-1",
                ["loaderId"] = "loader-1",
            }, cancellationToken);

            _ = RunNavigateScriptAsync(cancellationToken);
        }

        private async Task RunNavigateScriptAsync(CancellationToken cancellationToken)
        {
            try
            {
                var options = _server.Options;
                if (options.NavigateDelayMs > 0)
                {
                    await Task.Delay(options.NavigateDelayMs, cancellationToken);
                }

                if (options.OnNavigate is not null)
                {
                    await options.OnNavigate(this);
                }
                else
                {
                    await DefaultNavigateScriptAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
            {
                // Connection ended mid-script.
            }
            finally
            {
                _server.RecordNavigateEnd();
            }
        }

        private async Task DefaultNavigateScriptAsync(CancellationToken cancellationToken)
        {
            var options = _server.Options;
            var htmlBytes = Encoding.UTF8.GetByteCount(options.RenderedHtml);

            await EmitAsync("Network.requestWillBeSent", new Dictionary<string, object?>
            {
                ["requestId"] = "req-doc",
                ["type"] = "Document",
                ["request"] = new Dictionary<string, object?> { ["url"] = _navigatedUrl, ["method"] = "GET" },
                ["timestamp"] = 1.0,
            }, cancellationToken);
            await EmitAsync("Network.responseReceived", new Dictionary<string, object?>
            {
                ["requestId"] = "req-doc",
                ["type"] = "Document",
                ["response"] = new Dictionary<string, object?>
                {
                    ["url"] = _navigatedUrl,
                    ["status"] = 200,
                    ["mimeType"] = "text/html",
                    ["headers"] = new Dictionary<string, object?> { ["Content-Type"] = "text/html; charset=utf-8" },
                    ["headersText"] = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n\r\n",
                    ["timing"] = new Dictionary<string, object?> { ["requestTime"] = 1.0, ["sendStart"] = 1.01, ["receiveHeadersEnd"] = 1.05 },
                },
            }, cancellationToken);
            await EmitAsync("Network.loadingFinished", new Dictionary<string, object?>
            {
                ["requestId"] = "req-doc",
                ["encodedDataLength"] = htmlBytes,
            }, cancellationToken);
            await EmitAsync("Page.frameNavigated", new Dictionary<string, object?>
            {
                ["frame"] = new Dictionary<string, object?> { ["id"] = "frame-1", ["url"] = _navigatedUrl },
            }, cancellationToken);

            var index = 0;
            foreach (var subresource in options.Subresources)
            {
                index++;
                var requestId = $"req-{index}";
                await EmitAsync("Network.requestWillBeSent", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["type"] = subresource.ResourceType,
                    ["request"] = new Dictionary<string, object?> { ["url"] = subresource.Url, ["method"] = "GET" },
                    ["timestamp"] = 1.0 + index,
                }, cancellationToken);
                await EmitAsync("Network.responseReceived", new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["type"] = subresource.ResourceType,
                    ["response"] = new Dictionary<string, object?>
                    {
                        ["url"] = subresource.Url,
                        ["status"] = subresource.StatusCode,
                        ["mimeType"] = subresource.MimeType,
                        ["headers"] = new Dictionary<string, object?> { ["Content-Type"] = subresource.MimeType },
                        ["fromDiskCache"] = subresource.FromCache,
                        ["fromServiceWorker"] = subresource.FromServiceWorker,
                    },
                }, cancellationToken);
                if (!subresource.LeaveOpen)
                {
                    await EmitAsync("Network.loadingFinished", new Dictionary<string, object?>
                    {
                        ["requestId"] = requestId,
                        ["encodedDataLength"] = subresource.EncodedBytes,
                    }, cancellationToken);
                }
            }

            foreach (var registration in options.ServiceWorkerRegistrations)
            {
                await EmitAsync("ServiceWorker.workerRegistrationUpdated", new Dictionary<string, object?>
                {
                    ["registrations"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["registrationId"] = registration.RegistrationId,
                            ["scopeURL"] = registration.ScopeUrl,
                            ["isDeleted"] = false,
                        },
                    },
                }, cancellationToken);
                await EmitAsync("ServiceWorker.workerVersionUpdated", new Dictionary<string, object?>
                {
                    ["versions"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["versionId"] = "v1",
                            ["registrationId"] = registration.RegistrationId,
                            ["scriptURL"] = registration.ScriptUrl,
                            ["runningStatus"] = "running",
                        },
                    },
                }, cancellationToken);
            }

            if (_fetchEnabled)
            {
                foreach (var paused in options.FetchPausedRequests)
                {
                    await EmitAsync("Fetch.requestPaused", new Dictionary<string, object?>
                    {
                        ["requestId"] = paused.RequestId,
                        ["resourceType"] = paused.ResourceType,
                        ["request"] = new Dictionary<string, object?>
                        {
                            ["url"] = paused.Url,
                            ["method"] = "GET",
                            ["headers"] = new Dictionary<string, object?> { ["Accept-Language"] = "en-US" },
                        },
                    }, cancellationToken);
                }
            }

            await EmitAsync("Runtime.consoleAPICalled", new Dictionary<string, object?>
            {
                ["type"] = "warning",
                ["args"] = new object[] { new Dictionary<string, object?> { ["type"] = "string", ["value"] = "warn-message" } },
            }, cancellationToken);
            await EmitAsync("Runtime.consoleAPICalled", new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["args"] = new object[] { new Dictionary<string, object?> { ["type"] = "string", ["value"] = "error-message" } },
            }, cancellationToken);
            await EmitAsync("Runtime.exceptionThrown", new Dictionary<string, object?>
            {
                ["exceptionDetails"] = new Dictionary<string, object?>
                {
                    ["text"] = "Uncaught TypeError",
                    ["exception"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["description"] = "TypeError: boom is not a function",
                    },
                },
            }, cancellationToken);
        }

        private async Task HandleEvaluateAsync(long id, JsonElement parameters, CancellationToken cancellationToken)
        {
            var options = _server.Options;
            var expression = parameters.TryGetProperty("expression", out var e) ? e.GetString() ?? string.Empty : string.Empty;

            object? value;
            if (expression.Contains("__cfQuiet", StringComparison.Ordinal))
            {
                value = new Dictionary<string, object?>
                {
                    ["ready"] = options.ReadyState,
                    ["domQuietMs"] = options.DomQuietMs,
                    ["count"] = Interlocked.Read(ref _domMutations),
                };
            }
            else if (expression.Contains("outerHTML", StringComparison.Ordinal))
            {
                value = options.RenderedHtml;
            }
            else if (expression.Contains("location.href", StringComparison.Ordinal))
            {
                value = options.FinalUrl ?? _navigatedUrl;
            }
            else if (expression.Contains("scrollHeight", StringComparison.Ordinal))
            {
                value = options.NextScrollHeight();
            }
            else if (expression.Contains("getRegistrations", StringComparison.Ordinal))
            {
                value = options.PageServiceWorkerRegistrations;
            }
            else if (expression.StartsWith("window.scrollTo", StringComparison.Ordinal))
            {
                value = null;
                // Simulate scroll-triggered lazy content mutating the DOM.
                Interlocked.Increment(ref _domMutations);
                if (options.OnScroll is not null)
                {
                    _ = RunScrollScriptAsync(options.OnScroll);
                }
            }
            else
            {
                value = null;
            }

            await RespondAsync(id, new Dictionary<string, object?>
            {
                ["result"] = new Dictionary<string, object?> { ["type"] = value is null ? "undefined" : "object", ["value"] = value },
            }, cancellationToken);
        }

        private async Task RunScrollScriptAsync(Func<FakeConnection, Task> script)
        {
            try
            {
                await script(this);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
            {
                // Connection ended mid-script.
            }
        }

        /// <summary>Emits a session-scoped CDP event to the control layer.</summary>
        /// <param name="method">Event name.</param>
        /// <param name="parameters">Event payload.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public Task EmitAsync(string method, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken = default) =>
            SendRawAsync(new Dictionary<string, object?>
            {
                ["method"] = method,
                ["params"] = parameters,
                ["sessionId"] = _lastSessionId,
            }, cancellationToken);

        private Task RespondAsync(long id, IReadOnlyDictionary<string, object?> result, CancellationToken cancellationToken) =>
            SendRawAsync(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["result"] = result,
            }, cancellationToken);

        private async Task SendRawAsync(IReadOnlyDictionary<string, object?> message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}

/// <summary>Script configuration for the fake CDP browser.</summary>
public sealed class FakeCdpOptions
{
    private int _scrollHeightIndex;

    /// <summary>Post-render HTML returned by the DOM capture evaluation.</summary>
    public string RenderedHtml { get; set; } =
        "<!DOCTYPE html><html><head><title>fake-render</title></head><body><div id=\"root\">rendered-by-js</div></body></html>";

    /// <summary>Final URL override (null = the navigated URL).</summary>
    public string? FinalUrl { get; set; }

    /// <summary>DOM quiet age reported to the wait rule (large = immediately quiet).</summary>
    public long DomQuietMs { get; set; } = 10_000;

    /// <summary>document.readyState reported to the wait rule.</summary>
    public string ReadyState { get; set; } = "complete";

    /// <summary>Screenshot bytes returned by Page.captureScreenshot.</summary>
    public byte[] Screenshot { get; set; } = FakeCdpServer.MinimalPng;

    /// <summary>When true, Page.navigate never responds (render-timeout proof).</summary>
    public bool StallNavigate { get; set; }

    /// <summary>When set, Page.navigate returns this net error (failure isolation).</summary>
    public string? NavigateError { get; set; }

    /// <summary>Artificial delay inside the navigate event script (concurrency proofs).</summary>
    public int NavigateDelayMs { get; set; }

    /// <summary>Custom navigate event script (replaces the default one).</summary>
    public Func<FakeCdpServer.FakeConnection, Task>? OnNavigate { get; set; }

    /// <summary>Custom scroll-step script.</summary>
    public Func<FakeCdpServer.FakeConnection, Task>? OnScroll { get; set; }

    /// <summary>Subresources emitted after the document events.</summary>
    public List<FakeSubresource> Subresources { get; } = [];

    /// <summary>Service-worker registrations emitted as CDP events.</summary>
    public List<FakeServiceWorkerRegistration> ServiceWorkerRegistrations { get; } = [];

    /// <summary>Registrations returned by the page getRegistrations() evaluation.</summary>
    public object[] PageServiceWorkerRegistrations { get; set; } = [];

    /// <summary>Fetch.requestPaused events emitted when Fetch is enabled.</summary>
    public List<FakePausedRequest> FetchPausedRequests { get; } = [];

    /// <summary>Scroll-height sequence for infinite-scroll scripts (last value repeats).</summary>
    public List<long> ScrollHeights { get; } = [800];

    internal long NextScrollHeight()
    {
        var index = Math.Min(Interlocked.Increment(ref _scrollHeightIndex) - 1, ScrollHeights.Count - 1);
        return ScrollHeights[Math.Max(0, index)];
    }
}

/// <summary>One scripted subresource.</summary>
/// <param name="Url">Request URL.</param>
/// <param name="ResourceType">CDP resource type (Document, Script, Image, …).</param>
/// <param name="MimeType">Response MIME type.</param>
/// <param name="EncodedBytes">Encoded body bytes reported by loadingFinished.</param>
/// <param name="StatusCode">Response status.</param>
/// <param name="FromCache">Whether the response is flagged fromDiskCache.</param>
/// <param name="FromServiceWorker">Whether the response is flagged fromServiceWorker.</param>
/// <param name="LeaveOpen">When true, no loadingFinished is emitted (streaming/in-flight).</param>
public sealed record FakeSubresource(
    string Url,
    string ResourceType,
    string MimeType,
    long EncodedBytes,
    int StatusCode = 200,
    bool FromCache = false,
    bool FromServiceWorker = false,
    bool LeaveOpen = false);

/// <summary>One scripted service-worker registration.</summary>
/// <param name="RegistrationId">CDP registration id.</param>
/// <param name="ScopeUrl">Registration scope.</param>
/// <param name="ScriptUrl">Registered script URL.</param>
public sealed record FakeServiceWorkerRegistration(string RegistrationId, string ScopeUrl, string ScriptUrl);

/// <summary>One scripted Fetch.requestPaused event.</summary>
/// <param name="RequestId">Interception id.</param>
/// <param name="Url">Request URL.</param>
/// <param name="ResourceType">CDP resource type.</param>
public sealed record FakePausedRequest(string RequestId, string Url, string ResourceType);
