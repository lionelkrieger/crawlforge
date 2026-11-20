namespace CrawlForge.Crawler.Rendering;

/// <summary>Where a resolved browser binary came from (§5.6.3 resolution order).</summary>
public enum BrowserSource
{
    /// <summary>Pinned bundled Chrome for Testing (packaged assets).</summary>
    Bundled = 0,

    /// <summary>User-configured executable path (Settings).</summary>
    UserConfigured = 1,

    /// <summary>Locally installed Chrome.</summary>
    LocalChrome = 2,

    /// <summary>Locally installed Edge.</summary>
    LocalEdge = 3,
}

/// <summary>
/// A resolved LOCAL browser with its identity (§5.6.3: every audit and every
/// rendered URL record records the browser product and version used). The
/// pinned bundle version is carried separately for the UI-21 browser section.
/// </summary>
/// <param name="ExecutablePath">Absolute local executable path.</param>
/// <param name="Source">Resolution source in the §5.6.3 fallback chain.</param>
/// <param name="Product">Browser product label.</param>
/// <param name="Version">Browser version (pinned version for the bundle; file version otherwise; "unknown" when unreadable).</param>
/// <param name="PinnedVersion">The manifest pin when a bundled build resolved, else null.</param>
public sealed record ResolvedBrowser(
    string ExecutablePath,
    BrowserSource Source,
    string Product,
    string Version,
    string? PinnedVersion);

/// <summary>
/// Browser provisioning facade (FS-CRAWL-001 §5.6.3, D-003/CR-007): local-only
/// resolution with identity, plus the pin-manifest read used by the UI-21
/// browser section. NO runtime browser or driver download exists anywhere in
/// this path — resolution probes the local filesystem only.
/// </summary>
public static class BrowserProvisioning
{
    /// <summary>
    /// Resolves the browser fallback chain: pinned bundled Chrome for Testing
    /// → user-configured path → local Chrome → local Edge.
    /// </summary>
    /// <param name="options">Resolution options (baseline defaults when null).</param>
    /// <returns>The resolved browser, or null when none exists locally.</returns>
    public static ResolvedBrowser? TryResolve(BrowserResolutionOptions? options = null)
    {
        try
        {
            return new LocalBrowserResolver(options).ResolveDetailed();
        }
        catch (BrowserResolutionException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the bundled pin manifest when present (even before the binary is
    /// physically bundled — owner packaging item O-006).
    /// </summary>
    /// <param name="options">Resolution options carrying the packaged root.</param>
    /// <returns>The manifest, or null.</returns>
    public static BrowserPinManifest? TryLoadPinManifest(BrowserResolutionOptions? options = null)
    {
        var root = (options ?? new BrowserResolutionOptions()).PackagedAssetsRoot
            ?? LocalBrowserResolver.DiscoverPackagedAssetsRoot();
        return BrowserPinManifest.TryLoad(root);
    }
}
