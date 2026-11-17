namespace Fixture.E7;

public sealed class CloudEmbeddingProvider
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1/embeddings";

    public async Task<float[]> EmbedAsync(HttpClient client, string text)
    {
        await client.PostAsJsonAsync("https://api.openai.com/v1/embeddings", new { input = text });
        return [];
    }
}
