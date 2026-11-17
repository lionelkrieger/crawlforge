using Microsoft.ApplicationInsights;

namespace Fixture.Nr06;

public sealed class UsageAnalytics
{
    private readonly TelemetryClient _client = new();

    public void TrackPageView(string page) =>
        _client.TrackEvent("page-view", new Dictionary<string, string> { ["page"] = page });

    public async Task SendCrashReportAsync(HttpClient client, string dump)
    {
        await client.PostAsync("https://crash.vendor.example.com/collect", new StringContent(dump));
    }
}
