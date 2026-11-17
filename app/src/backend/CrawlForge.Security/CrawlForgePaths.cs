namespace CrawlForge.Security;

/// <summary>
/// Resolves the per-user CrawlForge application-data directory used for local
/// persistence (handshake file, entropy key, diagnostics). Storage locations
/// only — nothing here reads or derives machine identity (exclusion row 1).
/// </summary>
internal static class CrawlForgePaths
{
    /// <summary>
    /// Per-user data directory: <c>%APPDATA%\CrawlForge</c> on Windows,
    /// <c>~/Library/Application Support/CrawlForge</c> on macOS, and
    /// <c>~/.config/CrawlForge</c> elsewhere (process-model spec §6.2 pattern).
    /// </summary>
    internal static string GetPerUserDataDirectory()
    {
        string baseDirectory;
        if (OperatingSystem.IsWindows())
        {
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else if (OperatingSystem.IsMacOS())
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }
        else
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        if (string.IsNullOrEmpty(baseDirectory))
        {
            baseDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDirectory, "CrawlForge");
    }
}