using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// The pinned Chrome for Testing bundle manifest (FS-CRAWL-001 §5.6.3,
/// D-003/CR-007). Lives at <c>assets/browser/browser-pin.json</c> beside the
/// bundled browser binary. The pinned version is recorded for the UI-21
/// browser section and updated with each product release (owner packaging
/// item O-006 ships the binary; runtime download is PROHIBITED).
/// </summary>
public sealed record BrowserPinManifest
{
    /// <summary>Manifest file name inside the packaged browser asset root.</summary>
    public const string FileName = "browser-pin.json";

    /// <summary>Bundled product identity.</summary>
    [JsonPropertyName("product")]
    public string Product { get; init; } = "chrome-for-testing";

    /// <summary>The pinned Chrome for Testing version (recorded for UI-21).</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>Release channel of the pinned build.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>When the pin was recorded (UTC).</summary>
    [JsonPropertyName("pinnedAtUtc")]
    public DateTimeOffset? PinnedAtUtc { get; init; }

    /// <summary>Per-platform bundle layouts (relative executable paths).</summary>
    [JsonPropertyName("platforms")]
    public IReadOnlyDictionary<string, BundledBrowserLayout> Platforms { get; init; } =
        new Dictionary<string, BundledBrowserLayout>();

    /// <summary>
    /// Loads the manifest from a packaged asset root, when present.
    /// </summary>
    /// <param name="packagedAssetsRoot">The <c>assets/browser</c> directory.</param>
    /// <returns>The manifest, or null when the file is absent.</returns>
    /// <exception cref="BrowserResolutionException">The manifest exists but is invalid (local configuration error).</exception>
    public static BrowserPinManifest? TryLoad(string? packagedAssetsRoot)
    {
        if (string.IsNullOrWhiteSpace(packagedAssetsRoot))
        {
            return null;
        }

        var path = Path.Combine(packagedAssetsRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<BrowserPinManifest>(File.ReadAllText(path));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                throw new BrowserResolutionException($"The browser pin manifest '{path}' is missing its pinned version.");
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            throw new BrowserResolutionException($"The browser pin manifest '{path}' is invalid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new BrowserResolutionException($"The browser pin manifest '{path}' could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Selects this platform's bundle layout, when the manifest carries one.
    /// </summary>
    /// <returns>The relative executable path for the current OS/architecture.</returns>
    public string? ResolvePlatformExecutablePath()
    {
        var key = CurrentPlatformKey();
        return Platforms.TryGetValue(key, out var layout) ? layout.ExecutablePath : null;
    }

    /// <summary>The manifest platform key for the current OS/architecture.</summary>
    /// <returns>win64 / linux64 / mac-x64 / mac-arm64.</returns>
    public static string CurrentPlatformKey()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mac-arm64" : "mac-x64";
        }

        return "linux64";
    }
}

/// <summary>One platform's bundled browser layout.</summary>
public sealed record BundledBrowserLayout
{
    /// <summary>Executable path relative to the packaged asset root.</summary>
    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>
    /// Optional SHA-256 of the packaged archive/binary, recorded by the
    /// packaging step (O-006) for offline integrity verification.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
}
