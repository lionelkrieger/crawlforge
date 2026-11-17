using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>
/// Deterministic local crawl origin for the pipeline scale run: a real
/// loopback HTTP server with <c>pageCount</c> interlinked pages, so the product
/// frontier discovers URLs through genuine link extraction (no seeded frontier
/// shortcuts). A fraction of pages carry planted defects (missing h1, missing
/// meta description, over-long titles) so the analyzer pipeline has real work.
/// </summary>
public sealed class FixtureOrigin : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private long _requestsServed;

    private FixtureOrigin(HttpListener listener, string baseAddress, int pageCount)
    {
        _listener = listener;
        BaseAddress = baseAddress;
        PageCount = pageCount;
        _loop = Task.Run(() => ServeAsync(_lifetime.Token));
    }

    /// <summary>Loopback origin, e.g. http://127.0.0.1:54321.</summary>
    public string BaseAddress { get; }

    /// <summary>Number of distinct pages served.</summary>
    public int PageCount { get; }

    /// <summary>Total HTTP requests served (including robots.txt probes).</summary>
    public long RequestsServed => Interlocked.Read(ref _requestsServed);

    /// <summary>Starts the fixture origin on a free loopback port.</summary>
    /// <param name="pageCount">Distinct pages to serve.</param>
    /// <returns>A running origin.</returns>
    public static FixtureOrigin Start(int pageCount)
    {
        var port = GetFreeLoopbackPort();
        var listener = new HttpListener();
        var baseAddress = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(baseAddress + "/");
        listener.Start();
        return new FixtureOrigin(listener, baseAddress, pageCount);
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        using var concurrency = new SemaphoreSlim(64);
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

            await concurrency.WaitAsync(cancellationToken);
            _ = Task.Run(() =>
            {
                try
                {
                    Handle(context);
                }
                finally
                {
                    concurrency.Release();
                }
            }, cancellationToken);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        Interlocked.Increment(ref _requestsServed);
        var path = context.Request.Url?.AbsolutePath ?? "/";
        byte[] body;
        int status;
        if (path.StartsWith("/page-", StringComparison.Ordinal)
            && int.TryParse(path["/page-".Length..], out var page)
            && page >= 0 && page < PageCount)
        {
            body = Encoding.UTF8.GetBytes(RenderPage(page));
            status = 200;
        }
        else
        {
            // robots.txt and everything else: 404 (RFC 9309 — no restriction).
            body = Encoding.UTF8.GetBytes("not found");
            status = 404;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        try
        {
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // Client went away mid-response; the crawl records the fetch error.
        }
    }

    private string RenderPage(int page)
    {
        var links = new StringBuilder();
        for (var offset = 1; offset <= 4; offset++)
        {
            var target = (page + offset) % PageCount;
            links.Append($"<a href=\"/page-{target}\">link {target}</a> ");
        }

        var missingH1 = page % 7 == 0;
        var missingDescription = page % 10 == 0;
        var longTitle = page % 13 == 0;
        var title = longTitle
            ? $"Page {page} — " + new string('x', 120)
            : $"Page {page} — Scale Fixture";

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{title}</title>
            {(missingDescription ? "" : $"<meta name=\"description\" content=\"Deterministic scale fixture page {page} for the CrawlForge pipeline benchmark.\">")}
            </head>
            <body>
            <header><nav><a href="/page-0">home</a></nav></header>
            <main>
            {(missingH1 ? "" : $"<h1>Fixture page {page}</h1>")}
            <p>Section {page % 40} content block alpha. The quick brown fox jumps over the lazy dog. Search engine optimization audit crawl frontier politeness.</p>
            <p>Section {page % 40} content block beta. Canonical signals, indexability flags, and structured data snippets for benchmark analysis.</p>
            {links}
            </main>
            <footer><p>fixture footer {page}</p></footer>
            </body>
            </html>
            """;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Listener shutdown races the accept loop; harmless.
        }
        _lifetime.Dispose();
    }
}
