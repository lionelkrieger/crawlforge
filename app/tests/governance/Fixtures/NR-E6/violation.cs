namespace Fixture.E6;

public sealed class PluginLicensing
{
    public string PluginLicenseKey { get; set; } = string.Empty;

    public bool PluginFeatureGate(string feature) => false;

    public void ActivatePluginSubscription(string pluginId) { }
}
