using System.Diagnostics;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Local-only browser binary resolution options (§5.6.3, D-003/CR-007).
/// Resolution order: pinned bundled Chrome for Testing (packaged assets) →
/// user-configured path → local Chrome → local Edge. Runtime browser/driver
/// download is PROHIBITED (offline-first); no remote driver is ever fetched.
/// </summary>
public sealed class BrowserResolutionOptions
{
    /// <summary>
    /// Optional packaged-assets root containing the bundled browser (for
    /// example <c>assets/browser</c>). Probed first. When null, the resolver
    /// discovers <c>assets/browser</c> by walking up from the application
    /// directory (the same local-only posture as the Host asset locator).
    /// </summary>
    public string? PackagedAssetsRoot { get; init; }

    /// <summary>
    /// Optional user-configured browser executable path. When configured it
    /// must exist; a missing configured path is a local configuration error.
    /// </summary>
    public string? UserConfiguredExecutablePath { get; init; }

    /// <summary>
    /// Whether locally installed Chrome/Edge installs are probed as the final
    /// fallback (default true). Tests disable this to prove the no-browser
    /// failure path deterministically.
    /// </summary>
    public bool ProbeLocalInstalls { get; init; } = true;
}

/// <summary>
/// A local browser resolution failure. Diagnostics are local only; nothing is
/// reported to any remote service. Rendering-dependent capture modes fail at
/// pre-crawl validation with this error (§5.6.3); non-JS capture stays available.
/// </summary>
public sealed class BrowserResolutionException : Exception
{
    /// <summary>Creates the failure with a local diagnostic message.</summary>
    /// <param name="message">Local diagnostic description.</param>
    public BrowserResolutionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Resolves a headless Chromium-class browser binary from LOCAL sources only
/// (§5.6.3): the pinned bundled Chrome for Testing, then a user-configured
/// path, then local Chrome, then Edge. Never downloads a browser or driver at
/// runtime (D-003, offline-first); probes the filesystem only — no registry,
/// WMI, or machine identity reads.
/// </summary>
public sealed class LocalBrowserResolver
{
    private readonly BrowserResolutionOptions _options;

    /// <summary>Creates the resolver.</summary>
    /// <param name="options">Resolution options (baseline defaults when null).</param>
    public LocalBrowserResolver(BrowserResolutionOptions? options = null)
    {
        _options = options ?? new BrowserResolutionOptions();
    }

    /// <summary>
    /// Resolves the browser executable path (compatibility surface).
    /// </summary>
    /// <returns>An existing local executable path.</returns>
    /// <exception cref="BrowserResolutionException">
    /// A configured path is missing, or no local browser was found. Runtime
    /// download is prohibited; install a browser or configure a path instead.
    /// </exception>
    public string Resolve() => ResolveDetailed().ExecutablePath;

    /// <summary>
    /// Resolves the browser with its product/version identity (§5.6.3: every
    /// rendered URL record records the browser product and version used).
    /// </summary>
    /// <returns>The resolved local browser.</returns>
    /// <exception cref="BrowserResolutionException">No local browser resolves.</exception>
    public ResolvedBrowser ResolveDetailed()
    {
        var packagedRoot = !string.IsNullOrWhiteSpace(_options.PackagedAssetsRoot)
            ? _options.PackagedAssetsRoot
            : DiscoverPackagedAssetsRoot();

        if (!string.IsNullOrWhiteSpace(packagedRoot))
        {
            var manifest = BrowserPinManifest.TryLoad(packagedRoot);

            // The manifest's platform layout is the canonical bundled path.
            if (manifest is not null && manifest.ResolvePlatformExecutablePath() is { } platformPath)
            {
                var candidate = Path.Combine(packagedRoot, platformPath);
                if (File.Exists(candidate))
                {
                    return new ResolvedBrowser(
                        Path.GetFullPath(candidate),
                        BrowserSource.Bundled,
                        "Chrome for Testing",
                        manifest.Version,
                        manifest.Version);
                }
            }

            foreach (var candidate in PackagedCandidates(packagedRoot))
            {
                if (File.Exists(candidate))
                {
                    var version = manifest?.Version ?? ReadFileVersion(candidate) ?? "unknown";
                    return new ResolvedBrowser(
                        Path.GetFullPath(candidate),
                        BrowserSource.Bundled,
                        "Chrome for Testing",
                        version,
                        manifest?.Version);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.UserConfiguredExecutablePath))
        {
            if (!File.Exists(_options.UserConfiguredExecutablePath))
            {
                throw new BrowserResolutionException(
                    $"The configured browser executable '{_options.UserConfiguredExecutablePath}' does not exist. "
                    + "Correct the local configuration; runtime download is prohibited.");
            }

            return new ResolvedBrowser(
                Path.GetFullPath(_options.UserConfiguredExecutablePath),
                BrowserSource.UserConfigured,
                "User-configured browser",
                ReadFileVersion(_options.UserConfiguredExecutablePath) ?? "unknown",
                PinnedVersion: null);
        }

        if (_options.ProbeLocalInstalls)
        {
            foreach (var (candidate, source, product) in LocalInstallCandidates())
            {
                if (File.Exists(candidate))
                {
                    return new ResolvedBrowser(
                        Path.GetFullPath(candidate),
                        source,
                        product,
                        ReadFileVersion(candidate) ?? "unknown",
                        PinnedVersion: null);
                }
            }
        }

        throw new BrowserResolutionException(
            "No local browser binary found (pinned bundled Chrome for Testing, configured path, or local Chrome/Edge install). "
            + "Rendering-dependent capture modes cannot start; non-JS capture remains available. "
            + "Install a Chromium-class browser locally or configure a path; runtime download is prohibited.");
    }

    /// <summary>
    /// Discovers the packaged <c>assets/browser</c> root by walking up from the
    /// application directory (local filesystem only; mirrors the Host asset
    /// locator posture without a Host dependency).
    /// </summary>
    /// <returns>The asset root, or null when absent.</returns>
    public static string? DiscoverPackagedAssetsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "browser");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(directory.FullName, "app", "assets", "browser");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ReadFileVersion(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            return null;
        }
    }

    private static IEnumerable<string> PackagedCandidates(string root)
    {
        yield return Path.Combine(root, "chrome.exe");
        yield return Path.Combine(root, "chrome-win64", "chrome.exe");
        yield return Path.Combine(root, "chrome-linux64", "chrome");
        yield return Path.Combine(root, "chrome-mac-x64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
        yield return Path.Combine(root, "chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
        yield return Path.Combine(root, "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
        yield return Path.Combine(root, "Chromium.app", "Contents", "MacOS", "Chromium");
    }

    private static IEnumerable<(string Path, BrowserSource Source, string Product)> LocalInstallCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Chrome first (§5.6.3: "a locally installed Chrome, then Edge").
            yield return (Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), BrowserSource.LocalChrome, "Chrome");
            yield return (Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"), BrowserSource.LocalChrome, "Chrome");
            yield return (Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"), BrowserSource.LocalChrome, "Chrome");
            yield return (Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), BrowserSource.LocalEdge, "Edge");
            yield return (Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"), BrowserSource.LocalEdge, "Edge");
            yield return (Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"), BrowserSource.LocalEdge, "Edge");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return ("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", BrowserSource.LocalChrome, "Chrome");
            yield return ("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge", BrowserSource.LocalEdge, "Edge");
            yield return ("/Applications/Chromium.app/Contents/MacOS/Chromium", BrowserSource.LocalChrome, "Chromium");
            yield break;
        }

        yield return ("/usr/bin/google-chrome", BrowserSource.LocalChrome, "Chrome");
        yield return ("/usr/bin/google-chrome-stable", BrowserSource.LocalChrome, "Chrome");
        yield return ("/usr/bin/chromium", BrowserSource.LocalChrome, "Chromium");
        yield return ("/usr/bin/chromium-browser", BrowserSource.LocalChrome, "Chromium");
        yield return ("/usr/bin/microsoft-edge", BrowserSource.LocalEdge, "Edge");
    }
}
