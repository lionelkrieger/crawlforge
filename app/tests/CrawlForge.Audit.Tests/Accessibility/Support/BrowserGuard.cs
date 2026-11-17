using CrawlForge.Crawler.Rendering;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Graceful browser-availability guard (phase-02 pattern): resolves a LOCAL
/// browser via the accepted Crawler resolver and returns null when none
/// exists. Runtime download is never attempted.
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
}
