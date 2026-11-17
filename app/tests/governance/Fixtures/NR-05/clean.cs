namespace Fixture.Nr05;

/// <summary>Local toggles only; absent means enabled. Mirrors the Host surface.</summary>
public sealed class LocalFeatureAvailability
{
    private readonly Dictionary<string, bool> _toggles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string featureName) =>
        !_toggles.TryGetValue(featureName, out var enabled) || enabled;

    public object Describe() => new { remoteFlagResolution = false, allFeaturesEnabledByDefault = true };
}
