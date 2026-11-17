namespace Fixture.E4;

public sealed class IpReputationService
{
    public async Task<string> CheckReputationAsync(HttpClient client, string ip)
    {
        return await client.GetStringAsync($"https://api.abuseipdb.com/api/v2/check?ipAddress={ip}");
    }
}
