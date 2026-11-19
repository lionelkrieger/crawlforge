using System.Collections.Concurrent;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using CrawlForge.Crawler.Tests.Sitemaps.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Sibling integration surface of the fetcher (WP-09-01): per-hop hook
/// (cookie-jar / URL-rewrite patterns) and the optional client factory.
/// Null-hook/null-factory behavior must stay identical to the unhooked
/// fetcher (strict loop guard, injected client).
/// </summary>
public sealed class HttpFetcherHookTests
{
    private static HttpFetcherOptions Options() => new(maxRetryAttempts: 0, retryBackoffBaseMilliseconds: 1);

    private static HttpResponseMessage CookieGateRedirect(string url)
    {
        var response = ScriptedHttpMessageHandler.Response(302, location: url);
        response.Headers.TryAddWithoutValidation("Set-Cookie", "fixture_gate=1; Path=/");
        return response;
    }

    [Fact]
    public async Task NullHook_CookieGateSelfRedirect_StillStrictLoopGuard()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(CookieGateRedirect("http://site.example/gate"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler));

        FetchFailedException failure = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://site.example/gate")));

        Assert.Equal(FetchFailureKind.RedirectLoop, failure.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CookieJarHook_CookieGate_Returns200AfterOneRedirect()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(CookieGateRedirect("http://site.example/gate"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        var hook = new CookieJarFetchHook();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), fetchHook: hook);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/gate"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        // The revisit carried the jar cookie captured on the 3xx hop.
        Assert.True(handler.Requests[1].Headers.TryGetValues("Cookie", out var cookies));
        Assert.Contains("fixture_gate=1", string.Join("; ", cookies!));
        Assert.Contains(hook.Received, r => r.Url == "http://site.example/gate" && r.SetCookies.Count > 0);
    }

    [Fact]
    public async Task HookWithoutRevisitOverride_StillStrictLoopGuard()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Enqueue(CookieGateRedirect("http://site.example/gate"))
            .Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), fetchHook: new DefaultOnlyHook());

        FetchFailedException failure = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://site.example/gate")));

        Assert.Equal(FetchFailureKind.RedirectLoop, failure.Kind);
    }

    [Fact]
    public async Task BeforeSendHook_RewritesRequestUriAtFetchTime()
    {
        var handler = new RoutingHttpMessageHandler
        {
            Fallback = _ => ScriptedHttpMessageHandler.Response(200, "mapped"),
        };
        var hook = new RewritingFetchHook("original.example", "mapped.example");
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), fetchHook: hook);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://original.example/page"));

        // The rewrite is transport-level: the crawl record keeps the original
        // URL; the wire request went to the mapped host.
        Assert.Equal("http://original.example/page", result.FinalUrl);
        Assert.Equal("http://mapped.example/page", handler.Requests.Single().RequestUri!.AbsoluteUri);
        FetchHookContext context = hook.Contexts.Single();
        Assert.Equal("http://original.example/page", context.OriginalUrl);
        Assert.Equal("http://original.example/page", context.CurrentUrl);
        Assert.Equal(0, context.HopOrdinal);
        Assert.Null(context.PreviousResponse);
    }

    [Fact]
    public async Task HookContext_CarriesAmbientFetchContext()
    {
        var handler = new RoutingHttpMessageHandler
        {
            Fallback = _ => ScriptedHttpMessageHandler.Response(200, "ok"),
        };
        var hook = new RewritingFetchHook("no-match.example", "other.example");
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), fetchHook: hook);

        FetchContextScope.Current = new FetchContext(42, 77);
        try
        {
            await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/page"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/page"));

        Assert.Equal(42, hook.Contexts[0].AuditId);
        Assert.Equal(77, hook.Contexts[0].UrlId);
        Assert.Null(hook.Contexts[1].AuditId);
        Assert.Null(hook.Contexts[1].UrlId);
    }

    [Fact]
    public async Task ClientFactory_UsedOnceOwnedAndDisposed_InjectedClientUntouched()
    {
        var injectedHandler = new TrackingHandler(); // must never be used
        var factoryHandler = new TrackingHandler();
        var factoryCalls = 0;
        var fetcher = new HttpFetcher(
            Options(),
            new HttpClient(injectedHandler),
            httpClientFactory: _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult(new HttpClient(factoryHandler));
            });

        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/a"));
        await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/b"));

        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, factoryHandler.RequestCount);
        Assert.Equal(0, injectedHandler.RequestCount);

        fetcher.Dispose();
        Assert.True(factoryHandler.Disposed);
        Assert.False(injectedHandler.Disposed);
    }

    [Fact]
    public async Task NullFactory_InjectedClientUsed()
    {
        var handler = new ScriptedHttpMessageHandler().Enqueue(ScriptedHttpMessageHandler.Response(200, "ok"));
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler));

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://site.example/a"));

        Assert.Equal(200, result.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentCookieGateFetches_SharedJarHook_ThreadSafe()
    {
        var gateHits = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var handler = new RoutingHttpMessageHandler
        {
            Fallback = request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                var hits = gateHits.AddOrUpdate(url, 1, (_, count) => count + 1);
                return hits == 1 ? CookieGateRedirect(url) : ScriptedHttpMessageHandler.Response(200, "ok");
            },
        };
        var hook = new CookieJarFetchHook();
        using var fetcher = new HttpFetcher(Options(), new HttpClient(handler), fetchHook: hook);

        HttpFetchResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            fetcher.FetchAsync(new HttpFetchRequest($"http://site.example/gate/{i}"))));

        Assert.All(results, r => Assert.Equal(200, r.StatusCode));
        Assert.Equal(16, handler.Requests.Count);
    }

    private sealed class DefaultOnlyHook : IFetchHook
    {
        // All default implementations: no revisit allowance.
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int RequestCount;
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(ScriptedHttpMessageHandler.Response(200, "ok"));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
