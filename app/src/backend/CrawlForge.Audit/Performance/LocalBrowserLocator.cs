namespace CrawlForge.Audit.Performance;

/// <summary>
/// Local-only browser resolution options for the lab probe (mirrors the
/// WP-02-06 resolution contract): packaged assets → user-configured path →
/// local install. Runtime browser download is PROHIBITED (offline-first).
/// </summary>
public sealed class LabBrowserResolutionOptions
{
    /// <summary>
    /// Optional packaged-assets root containing a bundled browser (for
    /// example <c>assets/browser</c>). Probed first when configured.
    /// </summary>
    public string? PackagedAssetsRoot { get; init; }

    /// <summary>
    /// Optional user-configured browser executable path. When configured it
    /// must exist; a missing configured path is a local configuration error.
    /// </summary>
    public string? UserConfiguredExecutablePath { get; init; }
}

/// <summary>
/// Resolves a headless Chromium-class browser binary from LOCAL sources only
/// for the lab probe: packaged assets, then a user-configured path, then
/// local Chrome/Edge installs (Chrome preferred — cleanest offline posture).
/// Filesystem probes only: no registry, WMI, or machine-identity reads; no
/// download path exists. Mirrors the WP-02-06 LocalBrowserResolver order.
/// </summary>
public sealed class LocalBrowserLocator
{
    private readonly LabBrowserResolutionOptions _options;

    /// <summary>Creates the locator.</summary>
    /// <param name="options">Resolution options (defaults when null).</param>
    public LocalBrowserLocator(LabBrowserResolutionOptions? options = null)
    {
        _options = options ?? new LabBrowserResolutionOptions();
    }

    /// <summary>Resolves the browser executable path.</summary>
    /// <returns>An existing local executable path.</returns>
    /// <exception cref="LabProbeException">
    /// A configured path is missing, or no local browser was found
    /// (BrowserUnavailable). Runtime download is prohibited.
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
                throw new LabProbeException(
                    LabProbeFailureKind.BrowserUnavailable,
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

        throw new LabProbeException(
            LabProbeFailureKind.BrowserUnavailable,
            "No local browser binary found (packaged assets, configured path, or local Chrome/Edge install). "
            + "Install a Chromium-class browser locally or configure a path; runtime download is prohibited.");
    }

    /// <summary>
    /// Attempts resolution without throwing (graceful-skip support for tests
    /// and optional-category paths).
    /// </summary>
    /// <returns>The executable path, or null when no local browser exists.</returns>
    public string? TryResolve()
    {
        try
        {
            return Resolve();
        }
        catch (LabProbeException)
        {
            return null;
        }
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

            // Chrome first: with boundary-hardening flags it has the cleanest
            // offline posture (Edge adds vendor background services).
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
