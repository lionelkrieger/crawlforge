using System.Net;
using System.Text;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// Minimal loopback crawl-target site for audit-time traffic classification:
/// a small page graph plus robots.txt, served exclusively on 127.0.0.1. Every
/// request is recorded so tests can correlate observed connections with the
/// crawl target.
/// </summary>
public sealed class LoopbackTargetSite : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<string> _requests = [];
    private readonly object _gate = new();

    private LoopbackTargetSite(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(() => ServeAsync(_stop.Token));
    }

    /// <summary>Loopback base URL of the target site.</summary>
    public string BaseUrl { get; }

    /// <summary>Recorded request paths in arrival order.</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    /// <summary>Starts the site on a free loopback port.</summary>
    /// <returns>The running site.</returns>
    public static LoopbackTargetSite Start()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new LoopbackTargetSite(port);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
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
            await _loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
        {
            // Shutdown races are expected.
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            lock (_gate) { _requests.Add(path); }

            string body;
            var contentType = "text/html; charset=utf-8";
            switch (path)
            {
                case "/robots.txt":
                    body = "User-agent: *\nAllow: /\n";
                    contentType = "text/plain";
                    break;
                case "/page-a":
                    body = Page("Page A", "<a href=\"/page-b\">b</a>");
                    break;
                case "/page-b":
                    body = Page("Page B", "<a href=\"/\">home</a>");
                    break;
                default:
                    body = Page("Home", "<a href=\"/page-a\">a</a> <a href=\"/page-b\">b</a>");
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException)
            {
                // Client went away.
            }
        }
    }

    private static string Page(string title, string links) =>
        $"<!doctype html><html><head><title>{title}</title></head><body><h1>{title}</h1><p>Boundary target content.</p>{links}</body></html>";
}
