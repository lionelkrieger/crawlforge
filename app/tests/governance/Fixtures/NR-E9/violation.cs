namespace Fixture.E9;

public sealed class GeoRegistryPublisher
{
    public async Task PublishLlmsTxtAsync(HttpClient client, string content)
    {
        await client.PostAsync("https://registry.vendor.example.com/llms.txt/registry", new StringContent(content));
    }

    public void SubmitToAiVendor(string llmsTxt) { }
}
