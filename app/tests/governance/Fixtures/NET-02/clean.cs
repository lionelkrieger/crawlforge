namespace Fixture.Net02;

public sealed class LoopbackHealthClient
{
    public async Task<string> CheckAsync(int port)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        return await client.GetStringAsync("/api/health");
    }
}
