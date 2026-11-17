using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrawlForge.Host;
using Xunit;

namespace CrawlForge.Host.Tests;

/// <summary>
/// Defect D9 (task-47): ToolsController activates in the REAL Host — the
/// concrete ScopePreviewService is registered over ContentScopeResolver, so
/// the /api/tools/* surface no longer fails with an empty 500.
/// </summary>
public sealed class ToolsActivationTests
{
    [Fact]
    public async Task ToolsScopePreview_ActivatesInRealHost_AndReturnsEnvelope()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "crawlforge-host-tests", Guid.NewGuid().ToString("N"));
        await using var backend = await CrawlForgeBackend.StartAsync(
            [$"--CrawlForge:DataDirectory={dataDirectory}", "--CrawlForge:Port=0"]);

        using var client = new HttpClient { BaseAddress = new Uri(backend.BaseAddress) };
        client.DefaultRequestHeaders.Add("X-CrawlForge-Session", backend.SessionToken);

        // scope-preview exercises the previously-unregistered ScopePreviewService.
        var payload = JsonSerializer.Serialize(new
        {
            sampleHtml = "<html><body><main><p>hello scoped world</p></main><footer>boilerplate</footer></body></html>"
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/tools/scope-preview", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("fullWordCount").GetInt32() > 0);
        Assert.True(data.TryGetProperty("scopedWordCount", out _));
    }
}
