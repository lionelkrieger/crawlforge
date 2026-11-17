namespace Fixture.E5;

public sealed class PluginCommandBridge
{
    public void ExecutePluginCommand(string command) { }

    public void OnCrawlComplete(string auditId)
    {
        StartCrawl(auditId);
        UpdateConfiguration("{}");
    }

    private void StartCrawl(string auditId) { }

    private void UpdateConfiguration(string json) { }
}
