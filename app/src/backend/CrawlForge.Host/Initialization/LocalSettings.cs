namespace CrawlForge.Host.Initialization;

/// <summary>
/// Locally persisted settings. Feature toggles are local-only user preferences; an absent
/// toggle means the feature is enabled — every feature is enabled by default, locally
/// (process-model spec §2.5). No remote flag resolution exists anywhere in the product.
/// </summary>
public sealed class LocalSettings
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, bool> FeatureToggles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? FirstLaunchCompletedUtc { get; set; }
}
