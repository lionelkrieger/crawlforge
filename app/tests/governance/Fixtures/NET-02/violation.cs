namespace Fixture.Net02;

public sealed class UnapprovedFetcher
{
    public async Task<string> FetchVendorAsync()
    {
        using var client = new HttpClient();
        return await client.GetStringAsync("https://api.vendor.example.com/data");
    }
}
