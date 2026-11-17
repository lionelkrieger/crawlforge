using CrawlForge.Crawler.Rendering;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Graceful browser-availability guard: integration tests resolve a LOCAL
/// browser (packaged assets / configured path / local Edge-Chrome install) and
/// return early when none exists. Runtime download is never attempted.
/// </summary>
public static class BrowserGuard
{
    /// <summary>
    /// Resolves a local browser executable, or null when none is installed
    /// (graceful skip).
    /// </summary>
    /// <returns>The executable path, or null.</returns>
    public static string? TryResolve()
    {
        try
        {
            return new LocalBrowserResolver().Resolve();
        }
        catch (BrowserResolutionException)
        {
            return null;
        }
    }

    /// <summary>A desktop device profile for integration renders.</summary>
    public static Contracts.Crawl.DeviceProfile DesktopDevice { get; } = new(
        "desktop-test",
        1280,
        800,
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        false);
}
