using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.E2E.Tests.Fixtures;

/// <summary>
/// Loopback-only fixture origin (WP-07-01): a configurable HttpListener site
/// used as the crawl target for end-to-end proofs. Every response is scripted
/// (status, headers, body, time-to-first-byte delay) and every request is
/// counted so offline proofs can assert ZERO network access by request count.
/// Binds 127.0.0.1 exclusively — never a non-loopback interface.
/// </summary>
public sealed class LoopbackOrigin : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly OriginRoutes _routes;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _requestCount;

    private LoopbackOrigin(HttpListener listener, string baseUrl, OriginRoutes routes)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _routes = routes;
        _loop = Task.Run(ServeAsync);
    }

    /// <summary>Loopback origin, e.g. http://127.0.0.1:51234/.</summary>
    public string BaseUrl { get; }

    /// <summary>Total requests received (any path).</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>All recorded requests in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>Requests received for one absolute path.</summary>
    public int CountFor(string path) =>
        _requests.Count(r => string.Equals(r.Path, path, StringComparison.Ordinal));

    /// <summary>Starts a fixture origin; configure routes before traffic flows.</summary>
    public static async Task<LoopbackOrigin> StartAsync(Action<OriginRoutes>? configure = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        var baseUrl = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(baseUrl);
        listener.Start();
        await Task.Yield();

        var routes = new OriginRoutes();
        configure?.Invoke(routes);
        return new LoopbackOrigin(listener, baseUrl, routes);
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref _requestCount);
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        _requests.Enqueue(new RecordedRequest(
            context.Request.HttpMethod,
            path,
            context.Request.Url?.Query ?? string.Empty,
            DateTimeOffset.UtcNow));

        try
        {
            var response = _routes.Resolve(path, context.Request);
            if (response.DelayMs > 0)
            {
                await Task.Delay(response.DelayMs, _cts.Token);
            }

            var bytes = Encoding.UTF8.GetBytes(response.Body);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;
            foreach (var (name, value) in response.Headers)
            {
                context.Response.Headers[name] = value;
            }

            if (response.StatusCode is >= 300 and < 400 && response.Headers.ContainsKey("Location"))
            {
                context.Response.RedirectLocation = response.Headers["Location"];
            }

            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cts.Token);
        }
        catch (Exception)
        {
            // Connection aborted during shutdown — nothing to report.
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // Listener already closed.
        }

        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Shutdown race — the listener is closed regardless.
        }

        _cts.Dispose();
    }
}

/// <summary>One request observed by the fixture origin.</summary>
public sealed record RecordedRequest(string Method, string Path, string Query, DateTimeOffset At);

/// <summary>Scripted response of the fixture origin.</summary>
public sealed class OriginResponse
{
    public int StatusCode { get; init; } = 200;
    public string Body { get; init; } = string.Empty;
    public string ContentType { get; init; } = "text/html; charset=utf-8";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Delay before the first response byte (simulated TTFB).</summary>
    public int DelayMs { get; init; }

    public static OriginResponse Html(string body) => new() { Body = body };

    public static OriginResponse Text(string body) =>
        new() { Body = body, ContentType = "text/plain; charset=utf-8" };

    public static OriginResponse Status(int statusCode, string body = "") =>
        new() { StatusCode = statusCode, Body = body };

    public static OriginResponse Redirect(string location, int statusCode = 302) =>
        new() { StatusCode = statusCode, Headers = { ["Location"] = location } };

    public OriginResponse WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }

    public OriginResponse WithDelay(int milliseconds)
    {
        var copy = new OriginResponse
        {
            StatusCode = StatusCode,
            Body = Body,
            ContentType = ContentType,
            DelayMs = milliseconds
        };
        foreach (var (name, value) in Headers)
        {
            copy.Headers[name] = value;
        }
        return copy;
    }
}

/// <summary>Route table of the fixture origin (path → scripted response).</summary>
public sealed class OriginRoutes
{
    private readonly Dictionary<string, Func<HttpListenerRequest, OriginResponse>> _routes = new(StringComparer.Ordinal);

    /// <summary>Fallback for unmapped paths (default: 404).</summary>
    public Func<HttpListenerRequest, OriginResponse> Default { get; set; } =
        _ => OriginResponse.Status(404, "<html><body>Not found</body></html>");

    public OriginRoutes Map(string path, OriginResponse response) =>
        Map(path, _ => response);

    public OriginRoutes Map(string path, Func<HttpListenerRequest, OriginResponse> responder)
    {
        _routes[path] = responder;
        return this;
    }

    internal OriginResponse Resolve(string path, HttpListenerRequest request) =>
        _routes.TryGetValue(path, out var responder) ? responder(request) : Default(request);
}

/// <summary>HTML page helpers for fixture content.</summary>
public static class FixturePages
{
    public static string Page(string title, string body, string? lang = "en") =>
        $"<!doctype html><html lang=\"{lang}\"><head><title>{title}</title>" +
        $"<meta name=\"description\" content=\"Fixture page {title}\"></head><body>{body}</body></html>";

    public static string Links(params string[] hrefs) =>
        string.Join(" ", hrefs.Select(h => $"<a href=\"{h}\">{h}</a>"));
}
