namespace Fixture.Nr04;

public sealed class DeviceEnrollmentClient
{
    public string EnrollmentId { get; set; } = string.Empty;

    public async Task ReportHeartbeatAsync(HttpClient client)
    {
        await client.PostAsync("https://relay.vendor.example.com/heartbeat", new StringContent("{}"));
    }
}
