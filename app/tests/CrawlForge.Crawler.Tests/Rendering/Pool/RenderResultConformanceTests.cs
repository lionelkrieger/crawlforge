using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Render-result conformance against the LOCKED WP-10-A1 surfaces: the A1
/// waterfall field set on RenderedPage.NetworkWaterfall, the camelCase wire
/// shape, and artifact persistence (RenderedDom + Screenshot +
/// NetworkWaterfall) through the REAL CompressedContentStore on the migrated
/// v5 schema. The REST render-tester mapping consumes exactly this producer
/// output (coordinator wiring handoff).
/// </summary>
public sealed class RenderResultConformanceTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void RenderedPage_CarriesTheLockedA1WaterfallSurface()
    {
        // Compile-time + runtime proof of the locked additive member.
        var entry = new RenderedNetworkEntry(
            "http://127.0.0.1:9/pixel.gif",
            "image",
            "GET",
            200,
            "image/gif",
            TransferredBytes: 43,
            EncodedDataLength: 43,
            TimingJson: "{\"requestTime\":1.0}",
            FromCache: false,
            FromServiceWorker: false);
        var page = new RenderedPage(
            "http://127.0.0.1:9/page",
            "<html></html>",
            ReadOnlyMemory<byte>.Empty,
            [],
            PostRenderWafVerdict: null)
        {
            NetworkWaterfall = [entry],
        };

        Assert.NotNull(page.NetworkWaterfall);
        Assert.Equal(43, page.NetworkWaterfall![0].TransferredBytes);
    }

    [Fact]
    public async Task Waterfall_WireShape_MatchesLockedFieldNames()
    {
        await using var harness = FakeRenderHarness.Create(options =>
        {
            options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/pixel.gif", "Image", "image/gif", EncodedBytes: 43));
        });
        var rendered = await harness.Pool.RenderAsync(new RenderRequest("http://127.0.0.1:9/page", BrowserGuard.DesktopDevice));

        var json = JsonSerializer.Serialize(rendered.NetworkWaterfall, CamelCase);
        using var document = JsonDocument.Parse(json);
        var image = document.RootElement.EnumerateArray()
            .First(e => e.GetProperty("resourceType").GetString() == "image");

        // The locked A1 field set, verbatim camelCase.
        foreach (var field in new[]
        {
            "url", "resourceType", "method", "statusCode", "mimeType",
            "transferredBytes", "encodedDataLength", "timingJson", "fromCache", "fromServiceWorker",
        })
        {
            Assert.True(image.TryGetProperty(field, out _), $"Missing locked waterfall field '{field}'.");
        }

        Assert.Equal(43, image.GetProperty("transferredBytes").GetInt64());
        Assert.Equal(43, image.GetProperty("encodedDataLength").GetInt64());
        Assert.False(image.GetProperty("fromCache").GetBoolean());
        Assert.False(image.GetProperty("fromServiceWorker").GetBoolean());
    }

    [Fact]
    public async Task Render_PersistsAllThreeArtifacts_ThroughTheRealContentStore()
    {
        await using var database = await RenderingTestDatabase.CreateAsync();
        await using var harness = FakeRenderHarness.Create(
            options =>
            {
                options.Subresources.Add(new FakeSubresource("http://127.0.0.1:9/pixel.gif", "Image", "image/gif", EncodedBytes: 43));
            },
            contentStore: database.Store);

        var rendered = await harness.Pool.RenderAsync(
            new RenderRequest("http://127.0.0.1:9/page", BrowserGuard.DesktopDevice),
            new RenderArtifactContext(AuditId: 1, UrlId: 1));

        var artifacts = await database.Store.GetForUrlAsync(1, 1);
        Assert.Equal(3, artifacts.Count);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.RenderedDom);
        Assert.Contains(artifacts, a => a.Kind == ContentArtifactKind.Screenshot);
        var waterfallArtifact = Assert.Single(artifacts, a => a.Kind == ContentArtifactKind.NetworkWaterfall);

        // DOM artifact round-trips the rendered HTML.
        var domArtifact = artifacts.Single(a => a.Kind == ContentArtifactKind.RenderedDom);
        await using (var domStream = await database.Store.OpenReadAsync(domArtifact.StorageKey))
        using (var reader = new StreamReader(domStream!))
        {
            Assert.Equal(rendered.Html, await reader.ReadToEndAsync());
        }

        // Screenshot artifact round-trips the PNG bytes.
        var screenshotArtifact = artifacts.Single(a => a.Kind == ContentArtifactKind.Screenshot);
        await using (var pngStream = await database.Store.OpenReadAsync(screenshotArtifact.StorageKey))
        {
            using var buffer = new MemoryStream();
            await pngStream!.CopyToAsync(buffer);
            Assert.Equal(rendered.Screenshot.ToArray(), buffer.ToArray());
        }

        // Waterfall artifact round-trips the locked entries (camelCase JSON).
        await using var waterfallStream = await database.Store.OpenReadAsync(waterfallArtifact.StorageKey);
        var restored = await JsonSerializer.DeserializeAsync<List<RenderedNetworkEntry>>(waterfallStream!, CamelCase);
        Assert.NotNull(restored);
        Assert.Equal(rendered.NetworkWaterfall!.Count, restored!.Count);
        var image = Assert.Single(restored, e => e.ResourceType == "image");
        Assert.Equal(43, image.TransferredBytes);
        Assert.Equal(rendered.NetworkWaterfall!.Single(e => e.ResourceType == "image"), image);
    }

    [Fact]
    public async Task Render_WithoutArtifactContext_PersistsNothing()
    {
        await using var database = await RenderingTestDatabase.CreateAsync();
        await using var harness = FakeRenderHarness.Create(contentStore: database.Store);

        await harness.Pool.RenderAsync(new RenderRequest("http://127.0.0.1:9/page", BrowserGuard.DesktopDevice));
        Assert.Empty(await database.Store.GetForUrlAsync(1, 1));
    }

    [Fact]
    public async Task WaterfallArtifact_JsonShape_IsCamelCaseArray()
    {
        await using var database = await RenderingTestDatabase.CreateAsync();
        await using var harness = FakeRenderHarness.Create(contentStore: database.Store);

        await harness.Pool.RenderAsync(
            new RenderRequest("http://127.0.0.1:9/page", BrowserGuard.DesktopDevice),
            new RenderArtifactContext(1, 1));

        var artifact = Assert.Single(await database.Store.GetForUrlAsync(1, 1), a => a.Kind == ContentArtifactKind.NetworkWaterfall);
        await using var stream = await database.Store.OpenReadAsync(artifact.StorageKey);
        using var reader = new StreamReader(stream!);
        var json = await reader.ReadToEndAsync();

        Assert.StartsWith("[", json);
        Assert.Contains("\"resourceType\"", json);
        Assert.Contains("\"transferredBytes\"", json);
        Assert.DoesNotContain("\"ResourceType\"", json);
    }

    [Fact]
    public async Task Pool_ExposesBrowserInfo_ForTheUi21BrowserSection()
    {
        using var bundle = FakeBundleLayout.Create();
        await using var pool = new HeadlessRenderingPool(new RenderingPoolOptions
        {
            BrowserResolution = bundle.ResolutionOptions,
        });

        var info = pool.ResolveBrowserInfo();
        Assert.Equal(BrowserSource.Bundled, info.Source);
        Assert.Equal("131.0.6778.85", info.Version);

        var pin = pool.TryLoadPinManifest();
        Assert.NotNull(pin);
        Assert.Equal("131.0.6778.85", pin!.Version);
        Assert.Equal("chrome-for-testing", pin.Product);
    }
}
