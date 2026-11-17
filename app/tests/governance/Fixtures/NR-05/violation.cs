using LaunchDarkly.Sdk;

namespace Fixture.Nr05;

public sealed class RemoteFlagResolver
{
    public bool ResolveRemoteFlags(string flagKey) => false;

    public async Task FetchFlagsAsync(HttpClient client)
    {
        await client.GetStringAsync("https://flags.vendor.example.com/feature-flags");
    }
}
