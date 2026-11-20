using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CrawlForge.Contracts.Crawl;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// Built-in loopback intercepting forward proxy (FS-CRAWL-001 §5.9). Binds the
/// loopback interface ONLY — by construction: the single listener construction
/// site hard-codes <see cref="IPAddress.Loopback"/> and no API accepts a bind
/// address (criterion #22). It is a crawl-traffic tool, not a tunnel: it never
/// bridges local services to remote clients (only local clients can reach a
/// loopback listener), opens no non-loopback listener, and performs no
/// enrollment or liveness reporting.
/// <para>
/// Provides forward-proxy semantics (absolute-URI requests and CONNECT),
/// request/response capture with PER-SUBRESOURCE TRANSFERRED BYTES (CR-031;
/// phase-09 notation gap 7 — see <see cref="DrainDetailedCapturedAsync"/>),
/// user-defined local resource-blocking rules (local 403, zero outbound), and
/// upstream proxy chaining with per-host bypass and optional direct fallback.
/// With no upstream configured and no client requests, zero outbound
/// connections are made (criterion #3).
/// </para>
/// <para>
/// CR-006 scope note: this proxy routes plain-HTTP NON-rendered crawl traffic.
/// Rendered traffic is captured directly by the rendering control layer's CDP
/// Network domain (RenderedPage.NetworkWaterfall) — the proxy is not used for
/// rendered traffic (§5.6/§5.9 as amended).
/// </para>
/// </summary>
public sealed class LoopbackInterceptingProxy : ILoopbackInterceptingProxy, IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxHeaderLines = 100;
    private const int MaxRequestLineBytes = 8 * 1024;
    private const long MaxRequestBodyBytes = 64L * 1024 * 1024;
    private const int CaptureSoftCap = 50_000;
    private const int RelayBufferSize = 8 * 1024;

    private readonly ProxyRoutingSettings _baseSettings;
    private readonly IOutboundConnector? _injectedConnector;
    private readonly ILogger<LoopbackInterceptingProxy>? _logger;
    private readonly ConcurrentQueue<CaptureEntry> _captured = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private ProxyRoutingSettings _activeSettings = new();
    private IOutboundConnector _activeConnector;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private ProxyEndpoint? _endpoint;

    /// <summary>
    /// Creates the proxy. All configuration is local and user-supplied; no
    /// remote proxy auto-configuration or discovery channel exists.
    /// </summary>
    /// <param name="settings">Optional local routing settings (baseline defaults when null).</param>
    /// <param name="connector">Optional outbound connector (tests inject counting doubles).</param>
    /// <param name="logger">Optional local-only logger.</param>
    public LoopbackInterceptingProxy(
        ProxyRoutingSettings? settings = null,
        IOutboundConnector? connector = null,
        ILogger<LoopbackInterceptingProxy>? logger = null)
    {
        _baseSettings = settings ?? new ProxyRoutingSettings();
        _injectedConnector = connector;
        _activeConnector = connector ?? new TcpOutboundConnector(_baseSettings);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProxyEndpoint> StartAsync(ProxyOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_endpoint is not null)
            {
                return _endpoint;
            }

            _activeSettings = string.IsNullOrWhiteSpace(options.UpstreamProxyUrl)
                ? _baseSettings
                : new ProxyRoutingSettings
                {
                    UpstreamProxyUrl = options.UpstreamProxyUrl,
                    BypassHosts = _baseSettings.BypassHosts,
                    AllowDirectFallback = _baseSettings.AllowDirectFallback,
                    BlockingRules = _baseSettings.BlockingRules,
                };

            if (!string.IsNullOrWhiteSpace(_activeSettings.UpstreamProxyUrl))
            {
                // Reject invalid configuration locally at startup.
                _ = UpstreamProxyParser.Parse(_activeSettings.UpstreamProxyUrl);
            }

            _activeConnector = _injectedConnector ?? new TcpOutboundConnector(_activeSettings);
            _cts = new CancellationTokenSource();

            // The ONLY listener construction site: loopback by construction.
            // No code path accepts a bind address or a non-loopback interface.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _endpoint = new ProxyEndpoint("127.0.0.1", port);
            _acceptLoop = AcceptLoopAsync(_listener, _cts.Token);

            _logger?.LogInformation(
                "Intercepting proxy listening on loopback {ProxyHost}:{ProxyPort}.",
                _endpoint.Host,
                _endpoint.Port);
            return _endpoint;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_endpoint is null)
            {
                return;
            }

            _cts!.Cancel();
            _listener!.Stop();
            if (_acceptLoop is not null)
            {
                try
                {
                    await _acceptLoop.WaitAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // Expected during shutdown.
                }
            }

            _cts.Dispose();
            _cts = null;
            _listener = null;
            _acceptLoop = null;
            _endpoint = null;
            _logger?.LogInformation("Intercepting proxy stopped.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProxyCapturedRequest>> DrainCapturedAsync(CancellationToken cancellationToken = default)
    {
        var drained = new List<ProxyCapturedRequest>();
        while (_captured.TryDequeue(out var entry))
        {
            drained.Add(entry.Snapshot().ToLockedRecord());
        }

        return Task.FromResult<IReadOnlyList<ProxyCapturedRequest>>(drained);
    }

    /// <summary>
    /// Drains captured requests WITH per-subresource transferred bytes
    /// (CR-031): response head + body bytes actually relayed from the
    /// destination for each plain-HTTP subresource. Blocked requests (local
    /// 403) and upstream failures (502) transferred nothing (0); CONNECT
    /// tunnels are TLS end-to-end, so per-subresource sizes inside them are
    /// opaque (0 — the oversized-image rule reports "not measured", never
    /// "pass"). Either drain consumes the shared capture queue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured requests with sizes, in observation order.</returns>
    public Task<IReadOnlyList<ProxyCapturedRequestDetail>> DrainDetailedCapturedAsync(CancellationToken cancellationToken = default)
    {
        var drained = new List<ProxyCapturedRequestDetail>();
        while (_captured.TryDequeue(out var entry))
        {
            drained.Add(entry.Snapshot());
        }

        return Task.FromResult<IReadOnlyList<ProxyCapturedRequestDetail>>(drained);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleLock.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientSafeAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientSafeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ProxyRoutingException)
        {
            _logger?.LogDebug("Proxy connection ended: {Reason}.", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Proxy connection handler failed locally.");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var clientStream = client.GetStream();

        var requestLine = await ReadLineAsync(clientStream, MaxRequestLineBytes, cancellationToken);
        if (requestLine.Length == 0)
        {
            return;
        }

        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 2)
        {
            await WriteSimpleResponseAsync(clientStream, 400, "Malformed proxy request.", cancellationToken);
            return;
        }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var (headerLines, rawHead) = await ReadHeadAsync(clientStream, cancellationToken);

        if (string.Equals(method, "CONNECT", StringComparison.Ordinal))
        {
            await HandleConnectAsync(clientStream, target, cancellationToken);
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await WriteSimpleResponseAsync(clientStream, 400, "Forward proxy requires an absolute http(s) request URI.", cancellationToken);
            return;
        }

        await HandleForwardAsync(clientStream, method, uri, headerLines, rawHead, cancellationToken);
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken cancellationToken)
    {
        var (host, port) = ParseHostPort(target, 443);
        var captureUrl = $"https://{host}:{port}";

        if (IsBlocked(host, "/"))
        {
            Capture(captureUrl, "CONNECT", 403, "text/plain");
            await WriteSimpleResponseAsync(clientStream, 403, "Blocked by local resource rule.", cancellationToken);
            return;
        }

        Stream tunnel;
        try
        {
            tunnel = await _activeConnector.ConnectAsync(host, port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProxyRoutingException or TimeoutException)
        {
            _logger?.LogDebug("CONNECT {TargetHost}:{TargetPort} failed locally: {Reason}.", host, port, ex.GetType().Name);
            Capture(captureUrl, "CONNECT", 502, null);
            await WriteSimpleResponseAsync(clientStream, 502, "Upstream connection failed.", cancellationToken);
            return;
        }

        await using (tunnel)
        {
            var established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
            await clientStream.WriteAsync(established, cancellationToken);
            Capture(captureUrl, "CONNECT", 200, null);
            await RelayBidirectionalAsync(clientStream, tunnel, cancellationToken);
        }
    }

    private async Task HandleForwardAsync(
        NetworkStream clientStream,
        string method,
        Uri uri,
        List<string> headerLines,
        byte[] rawHead,
        CancellationToken cancellationToken)
    {
        var host = uri.Host;
        var port = uri.Port;
        var url = uri.ToString();

        if (IsBlocked(host, uri.PathAndQuery))
        {
            Capture(url, method, 403, "text/plain");
            await WriteSimpleResponseAsync(clientStream, 403, "Blocked by local resource rule.", cancellationToken);
            return;
        }

        Stream tunnel;
        try
        {
            tunnel = await _activeConnector.ConnectAsync(host, port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProxyRoutingException or TimeoutException)
        {
            _logger?.LogDebug("Forward {TargetHost}:{TargetPort} failed locally: {Reason}.", host, port, ex.GetType().Name);
            Capture(url, method, 502, null);
            await WriteSimpleResponseAsync(clientStream, 502, "Upstream connection failed.", cancellationToken);
            return;
        }

        await using (tunnel)
        {
            // Origin-form request through the byte tunnel (direct, SOCKS, or
            // upstream CONNECT). Connection: close keeps response framing simple.
            var forwardHead = BuildForwardHead(method, uri.PathAndQuery, headerLines);
            await tunnel.WriteAsync(forwardHead, cancellationToken);
            await ForwardRequestBodyAsync(clientStream, tunnel, headerLines, cancellationToken);

            var (responseHead, status, contentType) = await ReadResponseHeadAsync(tunnel, cancellationToken);
            var entry = Capture(url, method, status, contentType);
            entry.SetResponseHeadBytes(responseHead.Length);

            await clientStream.WriteAsync(responseHead, cancellationToken);

            // Counting relay: every body byte actually transferred from the
            // destination is recorded per subresource (CR-031). The entry is
            // already queued (capture-at-head semantics preserved); the size
            // accumulates atomically as the body relays.
            var buffer = new byte[RelayBufferSize];
            while (true)
            {
                var read = await tunnel.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await clientStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                entry.AddResponseBodyBytes(read);
            }
        }
    }

    private static byte[] BuildForwardHead(string method, string pathAndQuery, List<string> headerLines)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        foreach (var line in headerLines)
        {
            var name = line.Split(':', 2)[0].Trim();
            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Append(line).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static async Task ForwardRequestBodyAsync(
        NetworkStream clientStream,
        Stream tunnel,
        List<string> headerLines,
        CancellationToken cancellationToken)
    {
        var contentLength = FindHeader(headerLines, "Content-Length");
        if (contentLength is not null && long.TryParse(contentLength, out var length) && length > 0)
        {
            if (length > MaxRequestBodyBytes)
            {
                throw new ProxyRoutingException("Request body exceeded the local size bound.");
            }

            await CopyExactAsync(clientStream, tunnel, length, cancellationToken);
            return;
        }

        var transferEncoding = FindHeader(headerLines, "Transfer-Encoding");
        if (transferEncoding is not null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            await CopyChunkedBodyAsync(clientStream, tunnel, cancellationToken);
        }
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[RelayBufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static async Task CopyChunkedBodyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var sizeLine = await ReadLineAsync(source, MaxRequestLineBytes, cancellationToken);
            var sizeText = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var size)
                || size < 0)
            {
                throw new ProxyRoutingException("Malformed chunked request body.");
            }

            var rawSizeLine = Encoding.ASCII.GetBytes(sizeLine + "\r\n");
            await destination.WriteAsync(rawSizeLine, cancellationToken);
            if (size == 0)
            {
                // Trailers until the terminating empty line.
                while (true)
                {
                    var trailer = await ReadLineAsync(source, MaxRequestLineBytes, cancellationToken);
                    await destination.WriteAsync(Encoding.ASCII.GetBytes(trailer + "\r\n"), cancellationToken);
                    if (trailer.Length == 0)
                    {
                        break;
                    }
                }

                return;
            }

            await CopyExactAsync(source, destination, size, cancellationToken);
            var crlf = new byte[2];
            await ReadExactAsync(source, crlf, cancellationToken);
            await destination.WriteAsync(crlf, cancellationToken);
        }
    }

    private static async Task<(byte[] RawHead, int Status, string? ContentType)> ReadResponseHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var (lines, raw) = await ReadHeadAsync(stream, cancellationToken);
        if (lines.Count == 0)
        {
            throw new ProxyRoutingException("Empty response from destination.");
        }

        var statusParts = lines[0].Split(' ', 3);
        var status = statusParts.Length >= 2 && int.TryParse(statusParts[1], out var code) ? code : 502;
        var contentType = FindHeader(lines.Skip(1).ToList(), "Content-Type");
        return (raw, status, contentType);
    }

    private static async Task<(List<string> Lines, byte[] Raw)> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var raw = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream, MaxHeaderBytes, cancellationToken);
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            raw.Write(bytes, 0, bytes.Length);
            if (raw.Length > MaxHeaderBytes)
            {
                throw new ProxyRoutingException("Proxy header block exceeded the size bound.");
            }

            if (line.Length == 0)
            {
                break;
            }

            lines.Add(line);
            if (lines.Count > MaxHeaderLines)
            {
                throw new ProxyRoutingException("Proxy header block exceeded the line bound.");
            }
        }

        return (lines, raw.ToArray());
    }

    private static async Task RelayBidirectionalAsync(Stream client, Stream tunnel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var forward = CopySafeAsync(client, tunnel, linked.Token);
        var backward = CopySafeAsync(tunnel, client, linked.Token);
        await Task.WhenAny(forward, backward);
        linked.Cancel();
        await Task.WhenAll(forward, backward);
    }

    private static async Task CopySafeAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, RelayBufferSize, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // One direction ended; the tunnel is complete.
        }
    }

    private bool IsBlocked(string host, string path)
    {
        foreach (var rule in _activeSettings.BlockingRules)
        {
            if (ProxyRouteResolver.HostMatches(rule.HostPattern, host)
                && (rule.PathContains is null || path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private CaptureEntry Capture(string url, string method, int status, string? contentType)
    {
        while (_captured.Count > CaptureSoftCap && _captured.TryDequeue(out _))
        {
            // Bounded memory: the oldest captures are dropped when a caller
            // neglects to drain; capture consumers drain per page/run.
        }

        var entry = new CaptureEntry(url, method, status, contentType, DateTimeOffset.UtcNow);
        _captured.Enqueue(entry);
        return entry;
    }

    /// <summary>
    /// One queued capture: immutable identity plus atomically accumulating
    /// transferred-byte counters (the body relays after the head is captured).
    /// </summary>
    private sealed class CaptureEntry
    {
        private readonly string _url;
        private readonly string _method;
        private readonly int _statusCode;
        private readonly string? _contentType;
        private readonly DateTimeOffset _capturedAt;
        private long _responseHeadBytes;
        private long _responseBodyBytes;

        public CaptureEntry(string url, string method, int statusCode, string? contentType, DateTimeOffset capturedAt)
        {
            _url = url;
            _method = method;
            _statusCode = statusCode;
            _contentType = contentType;
            _capturedAt = capturedAt;
        }

        public void SetResponseHeadBytes(long count) => Interlocked.Exchange(ref _responseHeadBytes, count);

        public void AddResponseBodyBytes(long count) => Interlocked.Add(ref _responseBodyBytes, count);

        public ProxyCapturedRequestDetail Snapshot()
        {
            var head = Volatile.Read(ref _responseHeadBytes);
            var body = Volatile.Read(ref _responseBodyBytes);
            return new ProxyCapturedRequestDetail(
                _url,
                _method,
                _statusCode,
                _contentType,
                _capturedAt,
                TransferredBytes: head + body,
                ResponseBodyBytes: body);
        }
    }

    private static (string Host, int Port) ParseHostPort(string target, int defaultPort)
    {
        var separator = target.LastIndexOf(':');
        if (separator > 0 && int.TryParse(target[(separator + 1)..], out var port))
        {
            return (target[..separator], port);
        }

        return (target, defaultPort);
    }

    private static string? FindHeader(List<string> lines, string name)
    {
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && string.Equals(parts[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return parts[1].Trim();
            }
        }

        return null;
    }

    private static async Task WriteSimpleResponseAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        400 => "Bad Request",
        403 => "Forbidden",
        502 => "Bad Gateway",
        _ => "Status",
    };

    private static async Task<string> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                if (builder.Length == 0)
                {
                    return string.Empty;
                }

                throw new IOException("Connection closed mid-line.");
            }

            if (buffer[0] == (byte)'\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append((char)buffer[0]);
            if (builder.Length > maxBytes)
            {
                throw new ProxyRoutingException("Proxy line exceeded the size bound.");
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed mid-body.");
            }

            offset += read;
        }
    }
}
