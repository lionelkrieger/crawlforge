namespace Fixture.Nr03;

/// <summary>
/// Regression (wave-0C gate): per-user app-data path resolution. Environment.SpecialFolder
/// is a BCL PATH ENUM — it locates directories (%APPDATA%, ~/.config, ~/Library/Application
/// Support) and reads no username, account, or OS identity. Must pass ALL rules.
/// </summary>
public static class LocalAppDataPaths
{
    public static string ResolveDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "CrawlForge");
    }
}
