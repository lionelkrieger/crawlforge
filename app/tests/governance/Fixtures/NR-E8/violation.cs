namespace Fixture.E8;

public sealed class RemoteLogEnrichment
{
    public async Task<string> EnrichAsync(HttpClient client, string ip)
    {
        return await client.GetStringAsync($"https://ipinfo.io/{ip}/json");
    }

    public void FetchBotList() { }
}
