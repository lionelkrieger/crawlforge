namespace CrawlForge.Host.Features;

/// <summary>
/// Local-only feature availability (process-model spec §2.5). Every feature is enabled
/// by default; optional local toggles may disable functionality. No remote flag
/// resolution exists anywhere in the product (clean-room guardrail #5).
/// </summary>
public interface IFeatureAvailability
{
    bool IsEnabled(string featureName);
}
