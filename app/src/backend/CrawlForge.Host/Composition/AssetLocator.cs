namespace CrawlForge.Host.Composition;

/// <summary>
/// Locates bundled local assets (FS-CRAWL-001 §13 asset manifest). Assets ship
/// inside the application tree; resolution walks up from the executing
/// directory to the app root and never touches the network (clean-room E4/E8).
/// </summary>
public static class AssetLocator
{
    /// <summary>
    /// Resolves a bundled asset directory by name (e.g. "waf-signatures").
    /// </summary>
    /// <param name="assetName">Asset directory name under app/assets.</param>
    /// <returns>The absolute local asset directory path.</returns>
    /// <exception cref="DirectoryNotFoundException">The asset is not bundled.</exception>
    public static string ResolveDirectory(string assetName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", assetName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            // Test/bin layouts nest under app/tests/... or app/src/... — the app
            // root carries the assets directory directly.
            candidate = Path.Combine(directory.FullName, "app", "assets", assetName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Bundled asset '{assetName}' was not found locally.");
    }

    /// <summary>
    /// Resolves a bundled asset file by directory and file name.
    /// </summary>
    /// <param name="assetName">Asset directory name under app/assets.</param>
    /// <param name="fileName">File inside the asset directory.</param>
    /// <returns>The absolute local asset file path.</returns>
    public static string ResolveFile(string assetName, string fileName) =>
        Path.Combine(ResolveDirectory(assetName), fileName);
}
