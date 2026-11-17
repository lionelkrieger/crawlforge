using System.Net;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Loopback daemon provider (FS-CRAWL-002 §12.2): dialect support, loopback
/// enforcement before any request, no credentials, connection-test pattern.
/// </summary>
public class LoopbackDaemonProviderTests
{
    private const string EmbedResponse = """{"embeddings":[[0.6,0.8,0.0,0.0]]}""";

    [Fact]
    public async Task OllamaEmbedDialectProducesNormalizedVectors()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath == "/api/embed"
                ? FakeHttpHandler.Json(HttpStatusCode.OK, EmbedResponse)
                : FakeHttpHandler.NotFound(),
        };
        using var provider = new LoopbackDaemonEmbeddingProvider(
            "http://127.0.0.1:11434", "test-embed-model", new HttpClient(handler));

        var vector = await provider.EmbedAsync("hello world");

        Assert.Equal(4, vector.Values.Length);
        Assert.Equal("test-embed-model", vector.ModelTag);
        Assert.Equal(4, provider.Dimensions);
        var norm = Math.Sqrt(vector.Values.Span.ToArray().Sum(v => (double)v * v));
        Assert.Equal(1.0, norm, 3);
        Assert.All(handler.Requests, r => Assert.True(LoopbackEndpointValidator.IsLoopbackHost(r.Uri.Host)));
        Assert.All(handler.Requests, r => Assert.False(r.HasAuthorization));
    }

    [Fact]
    public async Task AutoStyleFallsBackToLegacyOllamaThenV1()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath switch
            {
                "/api/embed" => FakeHttpHandler.NotFound(),
                "/api/embeddings" => FakeHttpHandler.Json(HttpStatusCode.OK, """{"embedding":[1.0,0.0,0.0]}"""),
                _ => FakeHttpHandler.NotFound(),
            },
        };
        using var provider = new LoopbackDaemonEmbeddingProvider(
            "http://127.0.0.1:11434", null, new HttpClient(handler));

        var vector = await provider.EmbedAsync("text");
        Assert.Equal(3, vector.Values.Length);
        Assert.Equal(LoopbackDaemonEmbeddingProvider.DefaultDaemonModelTag, provider.ModelTag);

        // The resolved dialect is remembered: next call goes straight to legacy.
        handler.Requests.Clear();
        await provider.EmbedAsync("text again");
        Assert.Single(handler.Requests);
        Assert.Equal("/api/embeddings", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task V1EmbeddingsDialectIsSupported()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath == "/v1/embeddings"
                ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.0,1.0,0.0]}]}""")
                : FakeHttpHandler.NotFound(),
        };
        using var provider = new LoopbackDaemonEmbeddingProvider(
            "http://localhost:1234", "local-model", new HttpClient(handler),
            style: LocalDaemonApiStyle.V1Embeddings);

        var vector = await provider.EmbedAsync("text");
        Assert.Equal(3, vector.Values.Length);
        Assert.Equal("/v1/embeddings", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public void NonLoopbackConfigurationIsRejectedBeforeAnyRequest()
    {
        var handler = new FakeHttpHandler();
        Assert.Throws<EmbeddingConfigurationException>(() =>
            new LoopbackDaemonEmbeddingProvider("https://embeddings.example.test", null, new HttpClient(handler)));
        Assert.Throws<EmbeddingConfigurationException>(() =>
            new LoopbackDaemonEmbeddingProvider("http://192.168.1.5:11434", null, new HttpClient(handler)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConnectionTestSucceedsAgainstReachableDaemon()
    {
        var handler = new FakeHttpHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => FakeHttpHandler.Json(HttpStatusCode.OK, """{"models":[{"name":"test-embed-model:latest"}]}"""),
                "/api/embed" => FakeHttpHandler.Json(HttpStatusCode.OK, EmbedResponse),
                _ => FakeHttpHandler.NotFound(),
            },
        };
        using var provider = new LoopbackDaemonEmbeddingProvider(
            "http://127.0.0.1:11434", "test-embed-model", new HttpClient(handler));

        var result = await provider.TestConnectionAsync();

        Assert.True(result.IsReachable);
        Assert.True(result.ModelAvailable);
        Assert.Equal(4, provider.Dimensions);
    }

    [Fact]
    public async Task ConnectionTestFailsGracefullyWhenDaemonIsDown()
    {
        var handler = new FakeHttpHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused"),
        };
        using var provider = new LoopbackDaemonEmbeddingProvider(
            "http://127.0.0.1:11434", null, new HttpClient(handler));

        var result = await provider.TestConnectionAsync();

        Assert.False(result.IsReachable);
        Assert.False(result.ModelAvailable);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task LoopbackPinnedConnectionRefusesNonLoopbackHosts()
    {
        // The pinning layer refuses non-loopback hosts before any socket opens.
        await Assert.ThrowsAsync<EmbeddingConfigurationException>(() =>
            LoopbackDaemonEmbeddingProvider.OpenLoopbackConnectionAsync(
                new DnsEndPoint("embeddings.example.test", 443), CancellationToken.None));
        await Assert.ThrowsAsync<EmbeddingConfigurationException>(() =>
            LoopbackDaemonEmbeddingProvider.OpenLoopbackConnectionAsync(
                new DnsEndPoint("192.168.1.5", 11434), CancellationToken.None));
    }
}
