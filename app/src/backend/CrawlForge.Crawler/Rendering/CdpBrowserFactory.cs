using CrawlForge.Contracts.Crawl;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// One pooled browser: the CDP control connection plus the local process that
/// owns it (null for test doubles). Retiring a lease disposes both, so a hung
/// or crashed browser never poisons the pool (§5.6.2 failure isolation).
/// </summary>
internal sealed class BrowserLease : IAsyncDisposable
{
    public BrowserLease(CdpBrowser browser, ResolvedBrowser identity, CdpBrowserProcess? process)
    {
        Browser = browser;
        Identity = identity;
        Process = process;
    }

    public CdpBrowser Browser { get; }

    public ResolvedBrowser Identity { get; }

    public CdpBrowserProcess? Process { get; }

    public bool IsHealthy => Browser.IsConnected && (Process is null || Process.IsRunning);

    public async ValueTask DisposeAsync()
    {
        await Browser.DisposeAsync();
        if (Process is not null)
        {
            await Process.DisposeAsync();
        }
    }
}

/// <summary>
/// Launches pooled browsers. The production implementation starts a LOCAL
/// headless process and connects direct CDP; component tests inject a fake
/// that connects to a scripted loopback CDP server (no real browser needed).
/// </summary>
internal interface ICdpBrowserFactory
{
    Task<BrowserLease> LaunchAsync(ResolvedBrowser browser, RenderingPoolOptions options, ProxyEndpoint? proxyEndpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Production browser factory (D-002/D-003): local process launch + loopback
/// CDP connect. No runtime download; the executable arrives resolved.
/// </summary>
internal sealed class DefaultCdpBrowserFactory : ICdpBrowserFactory
{
    private readonly ILogger? _logger;

    public DefaultCdpBrowserFactory(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<BrowserLease> LaunchAsync(ResolvedBrowser browser, RenderingPoolOptions options, ProxyEndpoint? proxyEndpoint, CancellationToken cancellationToken)
    {
        var profileRoot = options.UserDataRootDirectory ?? Path.GetTempPath();
        var userDataDirectory = Path.Combine(profileRoot, "crawlforge-render-" + Guid.NewGuid().ToString("N"));
        var process = new CdpBrowserProcess(browser.ExecutablePath, userDataDirectory, ownsUserDataDirectory: true, _logger);
        try
        {
            var extraArguments = new List<string> { $"--lang={options.Locale}" };
            if (proxyEndpoint is not null)
            {
                // Explicit opt-in only (CR-006: the waterfall is CDP-direct).
                extraArguments.Add($"--proxy-server={proxyEndpoint.Host}:{proxyEndpoint.Port}");
                // Remove the browser's implicit loopback bypass so local crawl
                // targets also route through the intercepting proxy (§5.9).
                extraArguments.Add("--proxy-bypass-list=<-loopback>");
            }

            await process.LaunchAsync(extraArguments, TimeSpan.FromSeconds(options.LaunchTimeoutSeconds), cancellationToken);
            var connection = await CdpBrowser.ConnectAsync(process.DevToolsEndpoint!, cancellationToken);
            return new BrowserLease(connection, browser, process);
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }
}
