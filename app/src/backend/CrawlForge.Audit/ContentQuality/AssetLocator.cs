namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Locates bundled asset directories (dictionaries, font metrics). Assets ship
/// with the product and are NEVER fetched at runtime (FS-CRAWL-001 §7.8,
/// FS-CRAWL-002 §15.3). Resolution probes the application base directory and
/// then walks ancestors looking for the repository <c>app/assets</c> layout,
/// so the same code works in published builds and in-source tests.
/// </summary>
public static class AssetLocator
{
    /// <summary>Resolves the bundled dictionaries directory.</summary>
    /// <returns>Absolute path to <c>assets/dictionaries</c>.</returns>
    /// <exception cref="DirectoryNotFoundException">No bundled directory found.</exception>
    public static string ResolveDictionariesDirectory() => ResolveAssetDirectory("dictionaries");

    /// <summary>Resolves the bundled font-metrics directory.</summary>
    /// <returns>Absolute path to <c>assets/font-metrics</c>.</returns>
    /// <exception cref="DirectoryNotFoundException">No bundled directory found.</exception>
    public static string ResolveFontMetricsDirectory() => ResolveAssetDirectory("font-metrics");

    private static string ResolveAssetDirectory(string assetName)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDirectory, "assets", assetName);
        if (Directory.Exists(direct))
        {
            return Path.GetFullPath(direct);
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", assetName);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            var appCandidate = Path.Combine(directory.FullName, "app", "assets", assetName);
            if (Directory.Exists(appCandidate))
            {
                return Path.GetFullPath(appCandidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Bundled asset directory 'assets/{assetName}' was not found from '{baseDirectory}'. " +
            "Assets ship with the product; runtime fetching is not permitted.");
    }
}
