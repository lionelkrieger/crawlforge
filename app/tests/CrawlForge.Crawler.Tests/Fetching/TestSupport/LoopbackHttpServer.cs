using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.Crawler.Tests.Fetching.TestSupport;

/// <summary>
/// Real loopback HTTP test server (127.0.0.1 HttpListener). Also serves as a
/// stand-in forward proxy: proxied requests arrive with absolute-form URIs,
/// which are recorded for boundary assertions. No external network is used.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LoopbackHttpServer()
    {
        int port = FindFreeLoopbackPort();
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(ProcessLoopAsync);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public Func<RecordedRequest, ServerResponse>? Handler { get; set; }

    public void Dispose()
    {
        _cts.Cancel();
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
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Listener teardown races are expected on disposal.
        }

        _cts.Dispose();
    }

    private async Task ProcessLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            try
            {
                await HandleAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException)
            {
                // Client disconnected mid-response; keep serving.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        string rawUrl = context.Request.RawUrl ?? "/";
        var recorded = new RecordedRequest(rawUrl, context.Request.HttpMethod, context.Request.Url?.AbsoluteUri);
        Requests.Enqueue(recorded);

        ServerResponse response = Handler?.Invoke(recorded) ?? new ServerResponse(200, "text/plain", Encoding.UTF8.GetBytes("ok"), null, TimeSpan.Zero);

        if (response.DelayBeforeHeaders > TimeSpan.Zero)
        {
            await Task.Delay(response.DelayBeforeHeaders, _cts.Token).ConfigureAwait(false);
        }

        context.Response.StatusCode = response.StatusCode;
        if (response.ContentType is not null)
        {
            context.Response.ContentType = response.ContentType;
        }

        if (response.Location is not null)
        {
            context.Response.RedirectLocation = response.Location;
        }

        context.Response.ContentLength64 = response.Body.Length;
        await context.Response.OutputStream.WriteAsync(response.Body, _cts.Token).ConfigureAwait(false);
        context.Response.Close();
    }

    private static int FindFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    internal sealed record RecordedRequest(string RawUrl, string Method, string? AbsoluteUrl);

    internal sealed record ServerResponse(
        int StatusCode,
        string? ContentType,
        byte[] Body,
        string? Location,
        TimeSpan DelayBeforeHeaders)
    {
        public static ServerResponse Text(string body, int statusCode = 200) =>
            new(statusCode, "text/plain", Encoding.UTF8.GetBytes(body), null, TimeSpan.Zero);

        public static ServerResponse Redirect(string location, int statusCode = 302) =>
            new(statusCode, null, Array.Empty<byte>(), location, TimeSpan.Zero);
    }
}