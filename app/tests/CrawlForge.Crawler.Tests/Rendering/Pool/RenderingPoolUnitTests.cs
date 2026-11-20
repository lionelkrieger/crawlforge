using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Pool behavior that needs no browser: validation, bounded-concurrency
/// configuration, capture drain without a proxy, disposal, and the locked
/// CaptureSettings → pool-options mapping.
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
    public void DefaultPoolSize_IsHalfLogicalProcessors_MinimumTwo()
    {
        // §5.6.2 process model default (locked CaptureSettings constant).
        Assert.Equal(CaptureSettings.DefaultRenderingPoolSize, new RenderingPoolOptions().MaxConcurrency);
        Assert.Equal(Math.Max(2, Environment.ProcessorCount / 2), new RenderingPoolOptions().MaxConcurrency);
    }

    [Fact]
    public void DefaultWaitRule_Is500msQuiet_20sHardCap()
    {
        var options = new RenderingPoolOptions();
        Assert.Equal(500, options.RenderWaitQuietMs);
        Assert.Equal(20_000, options.RenderWaitHardCapMs);
    }

    [Fact]
    public void FromCaptureSettings_MapsTheLockedKnobs()
    {
        var settings = new CaptureSettings(
            RenderWaitQuietMs: 250,
            RenderWaitHardCapMs: 8000,
            RenderingPoolSize: 3);
        var options = RenderingPoolOptions.FromCaptureSettings(settings);
        Assert.Equal(250, options.RenderWaitQuietMs);
        Assert.Equal(8000, options.RenderWaitHardCapMs);
        Assert.Equal(3, options.MaxConcurrency);

        var defaults = RenderingPoolOptions.FromCaptureSettings(null);
        Assert.Equal(500, defaults.RenderWaitQuietMs);
        Assert.Equal(20_000, defaults.RenderWaitHardCapMs);
        Assert.Equal(CaptureSettings.DefaultRenderingPoolSize, defaults.MaxConcurrency);

        var nullPoolSize = RenderingPoolOptions.FromCaptureSettings(new CaptureSettings(RenderingPoolSize: null));
        Assert.Equal(CaptureSettings.DefaultRenderingPoolSize, nullPoolSize.MaxConcurrency);
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
    public async Task RenderAsync_WithBrowserMissing_ClassifiesBrowserUnavailable()
    {
        // A resolver pointing at an empty packaged root with a configured path
        // that does not exist fails locally before any browser launch.
        var options = new RenderingPoolOptions
        {
            BrowserResolution = new BrowserResolutionOptions
            {
                UserConfiguredExecutablePath = Path.Combine(Path.GetTempPath(), "cf-missing-browser-" + Guid.NewGuid().ToString("N") + ".exe"),
                ProbeLocalInstalls = false,
            },
        };
        await using var pool = new HeadlessRenderingPool(options);
        var failure = await Assert.ThrowsAsync<RenderFailureException>(() =>
            pool.RenderAsync(new RenderRequest("http://127.0.0.1:1/", BrowserGuard.DesktopDevice)));
        Assert.Equal(RenderFailureKind.BrowserUnavailable, failure.Kind);
        Assert.Contains("download is prohibited", failure.Message);
    }
}
