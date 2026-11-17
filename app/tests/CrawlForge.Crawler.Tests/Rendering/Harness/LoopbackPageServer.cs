using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Loopback-only test page server (HttpListener on 127.0.0.1) used by rendering
/// and proxy integration tests. Records received requests for header assertions.
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

    /// <summary>Creates a server that responds with fixed HTML to every request.</summary>
    /// <param name="html">The HTML body to serve.</param>
    /// <returns>The running server.</returns>
    public static LoopbackPageServer StartWithHtml(string html) =>
        Start(async context =>
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
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

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in context.Request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    headers[key] = context.Request.Headers[key] ?? string.Empty;
                }
            }

            lock (Requests)
            {
                Requests.Add(new RecordedRequest(
                    context.Request.HttpMethod,
                    context.Request.Url?.ToString() ?? string.Empty,
                    headers));
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
/// <param name="Headers">Request headers.</param>
public sealed record RecordedRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers);
