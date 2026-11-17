using System.Text.Json;
using CrawlForge.Host.Configuration;
using CrawlForge.Host.Initialization;

namespace CrawlForge.Host.Features;

/// <summary>
/// Reads local toggles from the seeded settings file. An absent toggle means enabled.
/// The settings file is local-only and is never fetched from any service.
/// </summary>
public sealed class LocalFeatureAvailability : IFeatureAvailability
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, bool> _toggles = new(StringComparer.OrdinalIgnoreCase);

    public LocalFeatureAvailability(CrawlForgePaths paths)
    {
        if (!File.Exists(paths.SettingsFilePath))
        {
            return;
        }

        var settings = JsonSerializer.Deserialize<LocalSettings>(
            File.ReadAllText(paths.SettingsFilePath),
            SerializerOptions);

        if (settings?.FeatureToggles is null)
        {
            return;
        }

        foreach (var (name, enabled) in settings.FeatureToggles)
        {
            _toggles[name] = enabled;
        }
    }

    public bool IsEnabled(string featureName) =>
        !_toggles.TryGetValue(featureName, out var enabled) || enabled;
}
