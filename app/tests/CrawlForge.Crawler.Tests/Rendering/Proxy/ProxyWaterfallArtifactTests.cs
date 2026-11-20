using System.Net;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Proxy;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// CR-031 http-mode (non-rendered) transferred-byte artifact path: the loopback
/// proxy's per-subresource captures are persisted as a locked
/// <c>ContentArtifactKind.NetworkWaterfall</c> artifact in the SAME
/// <see cref="RenderedNetworkEntry"/> camelCase-array shape as the CDP-direct
/// rendered waterfall, so WP-10-05's ImageRuleAnalyzer reads both uniformly.
/// </summary>
public sealed class ProxyWaterfallArtifactTests
{
    // Mirrors WP-10-05 ImageRuleAnalyzer.WaterfallJson (case-insensitive).
    private static readonly JsonSerializerOptions ReaderJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly byte[] PixelGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [Fact]
    public void Mapper_ClassifiesImage_AndCarriesCountedBytes()
    {
        var detail = new ProxyCapturedRequestDetail(
            "http://127.0.0.1:9/pixel.gif", "GET", 200, "image/gif", DateTimeOffset.UtcNow,
            TransferredBytes: 43 + 60, ResponseBodyBytes: 43);

        var entry = detail.ToRenderedNetworkEntry();
        Assert.Equal("http://127.0.0.1:9/pixel.gif", entry.Url);
        Assert.Equal("image", entry.ResourceType);
        Assert.Equal("GET", entry.Method);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal("image/gif", entry.MimeType);
        Assert.Equal(103, entry.TransferredBytes);
        Assert.Equal(43, entry.EncodedDataLength);
        Assert.False(entry.FromCache);
        Assert.False(entry.FromServiceWorker);
    }

    [Theory]
    [InlineData("image/png", "image")]
    [InlineData("text/html; charset=utf-8", "document")]
    [InlineData("text/css", "stylesheet")]
    [InlineData("application/javascript", "script")]
    [InlineData("font/woff2", "font")]
    [InlineData("video/mp4", "media")]
    [InlineData("text/event-stream", "eventsource")]
    [InlineData("application/octet-stream", "other")]
    [InlineData(null, "other")]
    public void Mapper_InfersResourceType_FromContentType(string? contentType, string expected)
    {
        var detail = new ProxyCapturedRequestDetail("http://127.0.0.1:9/x", "GET", 200, contentType, DateTimeOffset.UtcNow, 10, 10);
        Assert.Equal(expected, detail.ToRenderedNetworkEntry().ResourceType);
    }

    [Fact]
    public void ConnectTunnel_ZeroBytes_IsNotMeasured()
    {
        var detail = new ProxyCapturedRequestDetail("https://www.google.com:443", "CONNECT", 200, "", DateTimeOffset.UtcNow, 0, 0);
        var entry = detail.ToRenderedNetworkEntry();
        // 0 → the analyzer reports "not measured", never "pass" (CR-031).
        Assert.Equal(0, entry.TransferredBytes);
    }

    [Fact]
    public async Task PersistProxyCaptures_RoundTrips_ThroughRealStore_Like05Reader()
    {
        await using var database = await RenderingTestDatabase.CreateAsync();
        var captures = new List<ProxyCapturedRequestDetail>
        {
            new("http://127.0.0.1:9/page", "GET", 200, "text/html", DateTimeOffset.UtcNow, 500, 400),
            new("http://127.0.0.1:9/pixel.gif", "GET", 200, "image/gif", DateTimeOffset.UtcNow, 103, 43),
        };

        var row = await NetworkWaterfallArtifactWriter.PersistProxyCapturesAsync(database.Store, 1, 1, captures);
        Assert.Equal(ContentArtifactKind.NetworkWaterfall, row.Kind);

        // Read back exactly as WP-10-05's reader does: case-insensitive array.
        await using var stream = await database.Store.OpenReadAsync(row.StorageKey);
        var entries = await JsonSerializer.DeserializeAsync<List<RenderedNetworkEntry>>(stream!, ReaderJson);
        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);

        var bytesByUrl = entries.Where(e => e.TransferredBytes > 0).ToDictionary(e => e.Url, e => e.TransferredBytes);
        Assert.Equal(103, bytesByUrl["http://127.0.0.1:9/pixel.gif"]);
        Assert.Equal(500, bytesByUrl["http://127.0.0.1:9/page"]);
    }

    [Fact]
    public async Task HttpMode_EndToEnd_ProxyCountsImageBytes_PersistedForCr031()
    {
        await using var database = await RenderingTestDatabase.CreateAsync();
        await using var page = LoopbackPageServer.Start(async context =>
        {
            if (context.Request.Url?.AbsolutePath == "/pixel.gif")
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "image/gif";
                context.Response.ContentLength64 = PixelGif.Length;
                await context.Response.OutputStream.WriteAsync(PixelGif);
                context.Response.Close();
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes("<!doctype html><html><body><img src='/pixel.gif'></body></html>");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        await using var proxy = new LoopbackInterceptingProxy();
        var endpoint = await proxy.StartAsync(new ProxyOptions(null));

        // http-mode fetch of the document + image subresource through the proxy.
        using var client = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://{endpoint.Host}:{endpoint.Port}"),
            UseProxy = true,
        });
        await client.GetStringAsync(page.BaseUrl);
        await client.GetByteArrayAsync(page.BaseUrl + "pixel.gif");

        var captures = await proxy.DrainDetailedCapturedAsync();
        var row = await NetworkWaterfallArtifactWriter.PersistProxyCapturesAsync(database.Store, 1, 1, captures);

        await using var stream = await database.Store.OpenReadAsync(row.StorageKey);
        var entries = await JsonSerializer.DeserializeAsync<List<RenderedNetworkEntry>>(stream!, ReaderJson);
        var image = Assert.Single(entries!, e => e.ResourceType == "image");
        Assert.EndsWith("/pixel.gif", image.Url);
        // CR-031: the http-mode image payload is measurable from the proxy count.
        Assert.True(image.TransferredBytes >= PixelGif.Length);
        Assert.Equal(PixelGif.Length, image.EncodedDataLength);
    }

    [Fact]
    public void Writer_ShapeMatchesRenderedPath_CamelCaseArray()
    {
        var entries = new List<RenderedNetworkEntry>
        {
            new("http://127.0.0.1:9/pixel.gif", "image", "GET", 200, "image/gif", TransferredBytes: 43, EncodedDataLength: 43),
        };

        var bytes = NetworkWaterfallArtifactWriter.Serialize(entries);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("[", json);
        Assert.Contains("\"transferredBytes\"", json);
        Assert.Contains("\"resourceType\"", json);
        Assert.DoesNotContain("\"TransferredBytes\"", json);

        // Both case-insensitive (05) and camelCase readers round-trip identically.
        var viaReader = JsonSerializer.Deserialize<List<RenderedNetworkEntry>>(json, ReaderJson);
        Assert.Equal(entries[0], viaReader![0]);
    }
}
