namespace Fixture.Nr07;

public sealed class ServerGatedReports
{
    public bool ReportAuthorizationRequired { get; set; }

    public bool CanViewReport(string reportId, string accountId) => false;

    public async Task FetchServerAuditConfigAsync(HttpClient client)
    {
        await client.GetStringAsync("https://config.vendor.example.com/audit-config");
    }

    public void RegisterMarketingHook(string email) { }
}
