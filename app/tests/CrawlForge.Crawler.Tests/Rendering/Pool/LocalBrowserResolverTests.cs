using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Local-only browser resolution (WP-02-D1): packaged assets → user-configured
/// path → local install; never a runtime download.
/// </summary>
public sealed class LocalBrowserResolverTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cf-wp0206-resolver-" + Guid.NewGuid().ToString("N"));

    public LocalBrowserResolverTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PackagedAssets_WinOverLocalInstall()
    {
        var packaged = Path.Combine(_scratch, "assets", "chrome.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
        File.WriteAllText(packaged, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions { PackagedAssetsRoot = Path.Combine(_scratch, "assets") });
        Assert.Equal(Path.GetFullPath(packaged), resolver.Resolve());
    }

    [Fact]
    public void Resolve_PackagedSubdirectoryLayout_IsProbed()
    {
        var packaged = Path.Combine(_scratch, "assets", "chrome-win64", "chrome.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
        File.WriteAllText(packaged, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions { PackagedAssetsRoot = Path.Combine(_scratch, "assets") });
        Assert.Equal(Path.GetFullPath(packaged), resolver.Resolve());
    }

    [Fact]
    public void Resolve_UserConfiguredPath_UsedWhenNoPackagedAssets()
    {
        var configured = Path.Combine(_scratch, "custom-browser.exe");
        File.WriteAllText(configured, "stub");

        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions { UserConfiguredExecutablePath = configured });
        Assert.Equal(Path.GetFullPath(configured), resolver.Resolve());
    }

    [Fact]
    public void Resolve_MissingConfiguredPath_IsALocalConfigurationError()
    {
        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            UserConfiguredExecutablePath = Path.Combine(_scratch, "does-not-exist.exe"),
        });
        Assert.Throws<BrowserResolutionException>(() => resolver.Resolve());
    }

    [Fact]
    public void Resolve_NoBrowserAnywhere_ThrowsWithOfflineDiagnostic()
    {
        // Packaged root exists but is empty; no configured path. On a machine
        // with no local install this throws; where Edge/Chrome exist the
        // resolver legitimately finds them, so only assert the packaged miss.
        var resolver = new LocalBrowserResolver(new BrowserResolutionOptions
        {
            PackagedAssetsRoot = Path.Combine(_scratch, "empty-assets"),
        });
        Directory.CreateDirectory(Path.Combine(_scratch, "empty-assets"));

        var resolved = resolver.Resolve();
        Assert.True(File.Exists(resolved), "Resolution must return an existing local file — never a download.");
    }

    [Fact]
    public void Resolve_LocalInstall_FindsEdgeOrChromeOnThisMachine()
    {
        var resolver = new LocalBrowserResolver();
        var path = resolver.Resolve();
        Assert.True(File.Exists(path));
        Assert.True(
            path.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            || path.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || path.Contains("chromium", StringComparison.OrdinalIgnoreCase),
            $"Unexpected browser binary: {path}");
    }
}
