using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Mcp;

/// <summary>
/// Loopback-only MCP transport endpoint (FS-CRAWL-001 §11.2): streamable HTTP
/// POST for JSON-RPC requests plus an SSE GET channel, bound exclusively to
/// the loopback interface. Every request requires the local bearer token;
/// rotation invalidates prior clients immediately (criterion #16). Non-loopback
/// bind addresses are rejected at construction, and non-loopback remote
/// endpoints are refused at runtime. No remote bridging exists (exclusion row 4).
/// </summary>
public sealed class McpLoopbackServer : IDisposable
{
    private readonly McpJsonRpcDispatcher _dispatcher;
    private readonly McpTokenService _tokens;
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpListener _listener = new();
    private Task? _loop;

    /// <summary>Creates the server bound to loopback.</summary>
    /// <param name="dispatcher">JSON-RPC dispatcher.</param>
    /// <param name="tokens">Local token service.</param>
    /// <param name="bindAddress">Bind address; must be loopback.</param>
    /// <param name="port">TCP port; 0 selects a free ephemeral port.</param>
    /// <exception cref="ArgumentException">The bind address is not loopback.</exception>
    public McpLoopbackServer(McpJsonRpcDispatcher dispatcher, McpTokenService tokens, IPAddress? bindAddress = null, int port = 0)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        var address = bindAddress ?? IPAddress.Loopback;
        if (!IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "The MCP endpoint may only bind loopback addresses (FS-CRAWL-001 §3 row 4, §11.2).", nameof(bindAddress));
        }

        var effectivePort = port == 0 ? GetFreeLoopbackPort() : port;
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        EndpointPrefix = $"http://{host}:{effectivePort}/";
        _listener.Prefixes.Add(EndpointPrefix);
    }

    /// <summary>The loopback endpoint prefix the server listens on.</summary>
    public string EndpointPrefix { get; }

    /// <summary>The MCP message path under the endpoint prefix.</summary>
    public string MessageEndpoint => EndpointPrefix + "mcp";

    /// <summary>Starts listening and serving requests.</summary>
    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>Stops the server and releases the socket.</summary>
    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutdown races are expected.
        }

        _shutdown.Dispose();
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Defense in depth: the listener is loopback-bound, but refuse any
            // non-loopback remote endpoint regardless.
            if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
            {
                response.StatusCode = 403;
                await WriteTextAsync(response, "Forbidden: loopback only.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var authorization = request.Headers["Authorization"];
            var token = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization["Bearer ".Length..].Trim()
                : null;
            if (!_tokens.Validate(token))
            {
                response.StatusCode = 401;
                response.Headers["WWW-Authenticate"] = "Bearer";
                await WriteTextAsync(response, "Unauthorized: invalid local token.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";
            if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 404;
                await WriteTextAsync(response, "Not found.", cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (request.HttpMethod)
            {
                case "POST":
                    await HandlePostAsync(request, response, cancellationToken).ConfigureAwait(false);
                    break;
                case "GET":
                    await HandleSseAsync(request, response, cancellationToken).ConfigureAwait(false);
                    break;
                case "DELETE":
                    HandleDelete(request, response);
                    break;
                default:
                    response.StatusCode = 405;
                    await WriteTextAsync(response, "Method not allowed.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (IOException)
        {
            // Client disconnected.
        }
        catch (HttpListenerException)
        {
            // Listener closed mid-response.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }

    private async Task HandlePostAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var transport = string.Equals(request.Headers["X-MCP-Transport"], "stdio", StringComparison.OrdinalIgnoreCase)
            ? AutomationTransportKind.Stdio
            : AutomationTransportKind.Http;

        var result = await _dispatcher.DispatchAsync(body, transport, cancellationToken).ConfigureAwait(false);

        if (result.SessionId is { } newSession)
        {
            _sessions[newSession] = 0;
            response.Headers["Mcp-Session-Id"] = newSession;
        }
        else
        {
            // After initialize, clients must carry the assigned session id.
            var presented = request.Headers["Mcp-Session-Id"];
            if (presented is null || !_sessions.ContainsKey(presented))
            {
                response.StatusCode = 400;
                await WriteTextAsync(response, "Missing or unknown Mcp-Session-Id.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (result.ResponseJson is null)
        {
            response.StatusCode = 202; // Notification accepted.
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(result.ResponseJson);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSseAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var presented = request.Headers["Mcp-Session-Id"];
        if (presented is not null && !_sessions.ContainsKey(presented))
        {
            response.StatusCode = 400;
            await WriteTextAsync(response, "Unknown Mcp-Session-Id.", cancellationToken).ConfigureAwait(false);
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;

        // The server emits no spontaneous notifications (capabilities declare
        // listChanged=false); the SSE channel stays open with keep-alives so
        // resident local agents can listen for future additive events.
        var keepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
        await response.OutputStream.WriteAsync(keepAlive, cancellationToken).ConfigureAwait(false);
        await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            await response.OutputStream.WriteAsync(keepAlive, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleDelete(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.Headers["Mcp-Session-Id"] is { } session)
        {
            _sessions.TryRemove(session, out _);
        }

        response.StatusCode = 204;
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, CancellationToken cancellationToken)
    {
        response.ContentType = "text/plain";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
