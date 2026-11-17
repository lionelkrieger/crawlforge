namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Local-only browser binary resolution options (WP-02-D1). Resolution order:
/// packaged assets → user-configured path → local install. Runtime browser
/// download is PROHIBITED (offline-first); no remote driver is ever fetched.
/// </summary>
public sealed class BrowserResolutionOptions
{
    /// <summary>
    /// Optional packaged-assets root containing a bundled browser (for example
    /// <c>assets/browser</c>). Probed first when configured.
    /// </summary>
    public string? PackagedAssetsRoot { get; init; }

    /// <summary>
    /// Optional user-configured browser executable path. When configured it
    /// must exist; a missing configured path is a local configuration error.
    /// </summary>
    public string? UserConfiguredExecutablePath { get; init; }
}

/// <summary>
/// A local browser resolution failure. Diagnostics are local only; nothing is
/// reported to any remote service.
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
/// Resolves a headless Chromium-class browser binary from LOCAL sources only:
/// packaged assets, then a user-configured path, then local Edge/Chrome
/// installs. Never downloads a browser or driver at runtime (WP-02-D1,
/// offline-first); probes the filesystem only — no registry, WMI, or machine
/// identity reads.
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
    /// Resolves the browser executable path.
    /// </summary>
    /// <returns>An existing local executable path.</returns>
    /// <exception cref="BrowserResolutionException">
    /// A configured path is missing, or no local browser was found. Runtime
    /// download is prohibited; install a browser or configure a path instead.
    /// </exception>
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_options.PackagedAssetsRoot))
        {
            foreach (var candidate in PackagedCandidates(_options.PackagedAssetsRoot))
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
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

            return Path.GetFullPath(_options.UserConfiguredExecutablePath);
        }

        foreach (var candidate in LocalInstallCandidates())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new BrowserResolutionException(
            "No local browser binary found (packaged assets, configured path, or local Edge/Chrome install). "
            + "Install a Chromium-class browser locally or configure a path; runtime download is prohibited.");
    }

    private static IEnumerable<string> PackagedCandidates(string root)
    {
        yield return Path.Combine(root, "chrome.exe");
        yield return Path.Combine(root, "chrome-win64", "chrome.exe");
        yield return Path.Combine(root, "chrome-linux64", "chrome");
        yield return Path.Combine(root, "msedge.exe");
        yield return Path.Combine(root, "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing");
        yield return Path.Combine(root, "Chromium.app", "Contents", "MacOS", "Chromium");
    }

    private static IEnumerable<string> LocalInstallCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Chrome first: with the pool's boundary-hardening flags it has the
            // cleanest offline posture (Edge adds vendor background services).
            yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield break;
        }

        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/usr/bin/microsoft-edge";
    }
}
