using System.Reflection;

namespace Fixture.E1;

public sealed class PluginMarketplaceClient
{
    public async Task DownloadPluginAsync(HttpClient client, string pluginId)
    {
        var url = $"https://marketplace.vendor.example.com/plugins/download?id={pluginId}";
        await client.GetStringAsync(url);
    }

    public Assembly LoadRemoteCode(string url) => Assembly.LoadFrom(url);
}
