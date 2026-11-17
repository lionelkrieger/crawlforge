using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Loopback-only test page server (HttpListener on 127.0.0.1), mirroring the
/// phase-02 rendering harness pattern. Records every received request so
/// tests can assert loopback-only traffic.
/// </summary>
public sealed class LoopbackPageServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<HttpListenerContext, Task> _handler;

    /// <summary>Requests received by the server, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>The loopback base URL of the server.</summary>
    public string BaseUrl { get; }

    private LoopbackPageServer(HttpListener listener, string baseUrl, Func<HttpListenerContext, Task> handler)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _handler = handler;
        _loop = RunAsync();
    }

    /// <summary>Creates and starts a loopback page server with a custom handler.</summary>
    /// <param name="handler">The request handler.</param>
    /// <returns>The running server.</returns>
    public static LoopbackPageServer Start(Func<HttpListenerContext, Task> handler)
    {
        var port = GetFreeLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        return new LoopbackPageServer(listener, baseUrl, handler);
    }

    /// <summary>
    /// Creates a server serving fixed HTML at / and configurable assets
    /// (path → (contentType, body bytes)) for coverage/composition fixtures.
    /// </summary>
    /// <param name="html">The document body.</param>
    /// <param name="assets">Optional asset map.</param>
    /// <returns>The running server.</returns>
    public static LoopbackPageServer StartWithHtml(
        string html,
        IReadOnlyDictionary<string, (string ContentType, byte[] Body)>? assets = null) =>
        Start(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            string contentType;
            byte[] bytes;
            if (path != "/" && assets is not null && assets.TryGetValue(path, out var asset))
            {
                contentType = asset.ContentType;
                bytes = asset.Body;
            }
            else
            {
                contentType = "text/html; charset=utf-8";
                bytes = Encoding.UTF8.GetBytes(html);
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    /// <summary>Allocates a free loopback port.</summary>
    /// <returns>The port number.</returns>
    public static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Asserts every recorded request targets loopback (strict egress
    /// guard): zero exceptions tolerated.
    /// </summary>
    public void AssertAllRequestsLoopback()
    {
        lock (Requests)
        {
            foreach (var request in Requests)
            {
                var host = new Uri(request.Url).Host;
                if (host != "127.0.0.1" && host != "localhost" && host != "::1")
                {
                    throw new InvalidOperationException($"Non-loopback request observed: {request.Url}");
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // Expected during shutdown.
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
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

            lock (Requests)
            {
                Requests.Add(new RecordedRequest(
                    context.Request.HttpMethod,
                    context.Request.Url?.ToString() ?? string.Empty));
            }

            try
            {
                await _handler(context);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                // Client disconnected; nothing to do locally.
            }
        }
    }
}

/// <summary>One request recorded by the test page server.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Url">Request URL.</param>
public sealed record RecordedRequest(string Method, string Url);
