namespace Fixture.E2;

public sealed class CatalogUpdater
{
    public async Task FetchSignaturesAsync(HttpClient client)
    {
        await client.GetStringAsync("https://cdn.vendor.example.com/assets/waf-signatures.json");
    }

    public async Task DownloadStopWordsAsync(HttpClient client)
    {
        await client.GetStringAsync("https://cdn.vendor.example.com/assets/stopwords.json");
    }
}
