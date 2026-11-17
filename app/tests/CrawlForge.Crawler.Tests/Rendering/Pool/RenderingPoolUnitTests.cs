using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Pool behavior that needs no browser: validation, bounded-concurrency
/// configuration, capture drain without a proxy, and disposal.
/// </summary>
public sealed class RenderingPoolUnitTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/page")]
    public async Task RenderAsync_RejectsNonHttpTargets(string url)
    {
        await using var pool = new HeadlessRenderingPool();
        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest(url, BrowserGuard.DesktopDevice)));
        Assert.Equal(RenderFailureKind.Navigation, failure.Kind);
    }

    [Fact]
    public void Constructor_RejectsUnboundedConcurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeadlessRenderingPool(new RenderingPoolOptions { MaxConcurrency = 0 }));
    }

    [Fact]
    public async Task DrainNetworkCapture_WithoutProxy_IsEmpty()
    {
        await using var pool = new HeadlessRenderingPool();
        Assert.Empty(await pool.DrainNetworkCaptureAsync());
    }

    [Fact]
    public async Task IdlePool_WithProxyConfigured_GeneratesNoTraffic()
    {
        // Criterion #3 (idle-state zero outbound): constructing and idling a
        // pool launches no browser and starts no proxy — nothing is captured.
        await using var proxy = new LoopbackInterceptingProxy();
        await using var pool = new HeadlessRenderingPool(
            new RenderingPoolOptions { RouteThroughProxy = true },
            proxy: proxy);

        await Task.Delay(200);
        Assert.Empty(await pool.DrainNetworkCaptureAsync());
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var pool = new HeadlessRenderingPool();
        await pool.DisposeAsync();
        await pool.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pool.RenderAsync(new RenderRequest("http://127.0.0.1/", BrowserGuard.DesktopDevice)));
    }

    [Fact]
    public async Task RenderAsync_WithProxyButBrowserMissing_ClassifiesBrowserUnavailable()
    {
        // A resolver pointing at an empty packaged root with a configured path
        // that does not exist fails locally before any browser launch.
        var options = new RenderingPoolOptions
        {
            BrowserResolution = new BrowserResolutionOptions
            {
                UserConfiguredExecutablePath = Path.Combine(Path.GetTempPath(), "cf-missing-browser-" + Guid.NewGuid().ToString("N") + ".exe"),
            },
        };
        await using var pool = new HeadlessRenderingPool(options);
        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest("http://127.0.0.1:1/", BrowserGuard.DesktopDevice)));
        Assert.Equal(RenderFailureKind.BrowserUnavailable, failure.Kind);
    }
}
