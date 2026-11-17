using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Fetching;
using CrawlForge.Crawler.Tests.Fetching.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.Fetching;

/// <summary>
/// Fetcher over real loopback sockets: TTFB measurement, redirect chains,
/// size guard, timeouts, and proxy routing delegated to the
/// ILoopbackInterceptingProxy contract (test double; wave-2B implements it).
/// All traffic stays on 127.0.0.1.
/// </summary>
public sealed class HttpFetcherLoopbackTests
{
    [Fact]
    public async Task Loopback_TtfbMeasuredAgainstDelayedFirstByte()
    {
        using var server = new LoopbackHttpServer
        {
            Handler = _ => new LoopbackHttpServer.ServerResponse(
                200, "text/plain", Encoding.UTF8.GetBytes("delayed"), null, TimeSpan.FromMilliseconds(250)),
        };
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client);

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest(server.BaseUrl));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Ttfb >= TimeSpan.FromMilliseconds(150), $"TTFB was {result.Ttfb}.");
        Assert.True(result.TotalDuration >= result.Ttfb);
    }

    [Fact]
    public async Task Loopback_RedirectChain_FollowedHopByHop()
    {
        using var server = new LoopbackHttpServer
        {
            Handler = request => request.RawUrl switch
            {
                "/r1" => LoopbackHttpServer.ServerResponse.Redirect("/r2", 301),
                "/r2" => LoopbackHttpServer.ServerResponse.Redirect("/final", 302),
                _ => LoopbackHttpServer.ServerResponse.Text("arrived"),
            },
        };
        var recorder = new RedirectChainRecorder();
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client, redirectRecorder: recorder);

        FetchContextScope.Current = new FetchContext(11, 22);
        HttpFetchResult result;
        try
        {
            result = await fetcher.FetchAsync(new HttpFetchRequest(server.BaseUrl + "r1"));
        }
        finally
        {
            FetchContextScope.Current = null;
        }

        Assert.Equal(server.BaseUrl + "final", result.FinalUrl);
        Assert.Equal("arrived", Encoding.UTF8.GetString(result.Body.Span));
        IReadOnlyList<RedirectHop> chain = await recorder.GetChainAsync(11, 22);
        Assert.Equal(2, chain.Count);
        Assert.Equal(301, chain[0].StatusCode);
        Assert.Equal(302, chain[1].StatusCode);
    }

    [Fact]
    public async Task Loopback_ProxyRouting_DelegatesToInterceptingProxyContract()
    {
        // The test server stands in for the loopback intercepting proxy: a
        // proxied request arrives in absolute form without the target being
        // resolved or contacted directly.
        using var server = new LoopbackHttpServer
        {
            Handler = _ => LoopbackHttpServer.ServerResponse.Text("via-proxy"),
        };
        var proxy = new FakeLoopbackInterceptingProxy(new ProxyEndpoint("127.0.0.1", server.Port));
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(
            new HttpFetcherOptions(),
            client,
            interceptingProxy: proxy,
            proxyOptions: new ProxyOptions("http://upstream.user-proxy.example:3128"));

        HttpFetchResult result = await fetcher.FetchAsync(new HttpFetchRequest("http://crawl-target.example/page"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("via-proxy", Encoding.UTF8.GetString(result.Body.Span));
        Assert.Equal(1, proxy.StartCalls);
        Assert.Equal("http://upstream.user-proxy.example:3128", proxy.LastOptions?.UpstreamProxyUrl);

        LoopbackHttpServer.RecordedRequest observed = Assert.Single(server.Requests);
        // Absolute-form request reached the loopback proxy targeting the crawl
        // host (http.sys may normalize the authority with the local port).
        Assert.NotNull(observed.AbsoluteUrl);
        Assert.StartsWith("http://crawl-target.example", observed.AbsoluteUrl, StringComparison.Ordinal);
        Assert.EndsWith("/page", observed.AbsoluteUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loopback_ProxyStartedOnce_AcrossMultipleFetches()
    {
        using var server = new LoopbackHttpServer
        {
            Handler = _ => LoopbackHttpServer.ServerResponse.Text("ok"),
        };
        var proxy = new FakeLoopbackInterceptingProxy(new ProxyEndpoint("127.0.0.1", server.Port));
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client, interceptingProxy: proxy);

        await fetcher.FetchAsync(new HttpFetchRequest("http://crawl-target.example/one"));
        await fetcher.FetchAsync(new HttpFetchRequest("http://crawl-target.example/two"));

        Assert.Equal(1, proxy.StartCalls);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Proxy_NonLoopbackEndpoint_Rejected()
    {
        var proxy = new FakeLoopbackInterceptingProxy(new ProxyEndpoint("10.0.0.5", 8080));
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(), client, interceptingProxy: proxy);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fetcher.FetchAsync(new HttpFetchRequest("http://crawl-target.example/")));
    }

    [Fact]
    public async Task Loopback_OversizeBody_ThrowsOversize()
    {
        using var server = new LoopbackHttpServer
        {
            Handler = _ => LoopbackHttpServer.ServerResponse.Text(new string('x', 4096)),
        };
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(maxResponseBytes: 1024, maxRetryAttempts: 0), client);

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest(server.BaseUrl)));

        Assert.Equal(FetchFailureKind.Oversize, ex.Kind);
    }

    [Fact]
    public async Task Loopback_SlowServer_TimesOut()
    {
        using var server = new LoopbackHttpServer
        {
            Handler = _ => new LoopbackHttpServer.ServerResponse(
                200, "text/plain", Encoding.UTF8.GetBytes("late"), null, TimeSpan.FromSeconds(5)),
        };
        using var client = CreateClient();
        using var fetcher = new HttpFetcher(new HttpFetcherOptions(defaultTimeoutSeconds: 1, maxRetryAttempts: 0), client);

        FetchFailedException ex = await Assert.ThrowsAsync<FetchFailedException>(
            () => fetcher.FetchAsync(new HttpFetchRequest(server.BaseUrl)));

        Assert.Equal(FetchFailureKind.Timeout, ex.Kind);
    }

    /// <summary>
    /// The fetcher follows redirects manually; the injected client must have
    /// automatic redirects disabled (composition-root requirement).
    /// </summary>
    private static HttpClient CreateClient() =>
        new(new HttpClientHandler { AllowAutoRedirect = false });
}