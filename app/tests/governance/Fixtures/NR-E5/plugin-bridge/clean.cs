namespace Fixture.E5;

/// <summary>Read-only data + UI contribution rights only.</summary>
public sealed class PluginDataBridge
{
    public string GetReportData(string auditId) => string.Empty;

    public void OnTabActivate(string auditId) { }

    public void OnDataUpdate(string payload) { }
}
