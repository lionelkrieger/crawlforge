using Microsoft.Extensions.Options;

namespace CrawlForge.Host.Configuration;

/// <summary>
/// Resolves local-only filesystem locations. No registry, WMI, or machine-identity reads:
/// the per-user data root comes from the application-data environment convention only
/// (clean-room guardrail #1).
/// </summary>
public sealed class CrawlForgePaths
{
    public CrawlForgePaths(IOptions<CrawlForgeOptions> options)
    {
        var configured = options.Value.DataDirectory;
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrawlForge")
            : Path.GetFullPath(configured);

        DiagnosticsDirectory = Path.Combine(DataDirectory, "diagnostics");
        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        PortDescriptorPath = Path.Combine(DataDirectory, "backend-port.json");
    }

    public string DataDirectory { get; }

    public string DiagnosticsDirectory { get; }

    public string SettingsFilePath { get; }

    public string PortDescriptorPath { get; }
}
