using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using CrawlForge.Crawler.Tests.CaptureModes.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes;

/// <summary>
/// Component behaviors of the capture-mode coordinator (WP-10-02): render
/// failure isolation (§5.6.2), JS-only link discovery (§5.6.1), the
/// service-worker registration fact (§7.19), the CR-006 rendered waterfall
/// artifact (from the render layer, never the proxy), and capture-time snippet
/// execution with source selection (§7.17).
/// </summary>
public sealed class RenderedCaptureComponentTests : IClassFixture<CaptureTestStack>
{
    private readonly CaptureTestStack _stack;

    public RenderedCaptureComponentTests(CaptureTestStack stack) => _stack = stack;

    [Fact]
    public async Task RenderFailure_IsIsolated_KeepsRawCapture()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/fail/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        var pool = new FakeRenderingPool();
        pool.FailUrls.Add(url.CanonicalUrl);
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, new RegisteredDomParser(), _stack.ContentStore, _stack.Urls, _stack.Urls);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.False(outcome.Rendered);
        Assert.Equal(CaptureSource.Raw, outcome.CaptureSource);
        Assert.NotNull(outcome.RenderFailureReason);
        Assert.Null(outcome.RenderedDocument);
        UrlRecordDto? stored = await _stack.Urls.GetByIdAsync(url.Id);
        Assert.Equal(CaptureSource.Raw, stored!.CaptureSource);
    }

    [Fact]
    public async Task JsOnlyLinks_AreDiscoveredAsRenderCandidates()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/pages/js-link.html");
        DomDocument raw = CaptureDomBuilder.Document(url.CanonicalUrl, CaptureDomBuilder.Page(
            "JS link source", null, new[] { CaptureDomBuilder.Text("h1", "JS link source") }));
        DomDocument rendered = CaptureDomBuilder.Document(url.CanonicalUrl, CaptureDomBuilder.Page(
            "JS link source", null, new[]
            {
                CaptureDomBuilder.Text("h1", "JS link source"),
                CaptureDomBuilder.Anchor("/pages/js-only-target.html", "Rendered only link"),
                CaptureDomBuilder.Anchor("/shared.html", "Shared link"),
            }));
        // The shared link exists in the raw document too → not a render candidate.
        DomDocument rawWithShared = CaptureDomBuilder.Document(url.CanonicalUrl, CaptureDomBuilder.Page(
            "JS link source", null, new[]
            {
                CaptureDomBuilder.Text("h1", "JS link source"),
                CaptureDomBuilder.Anchor("/shared.html", "Shared link"),
            }));

        var pool = new FakeRenderingPool();
        pool.Register(url.CanonicalUrl, "<rendered:js-link>");
        var parser = new RegisteredDomParser().Register("<rendered:js-link>", rendered);
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, parser, _stack.ContentStore);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, rawWithShared));

        RenderedLinkCandidate candidate = Assert.Single(outcome.RenderedOnlyLinks);
        Assert.Equal("https://fixture.test/pages/js-only-target.html", candidate.AbsoluteUrl);
        Assert.Equal("Rendered only link", candidate.AnchorText);
        Assert.False(candidate.IsNofollow);
    }

    [Fact]
    public async Task ServiceWorkerRegistration_IsDetectedAsFact()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/sw-app/");
        DomDocument raw = CaptureDomBuilder.Document(url.CanonicalUrl, CaptureDomBuilder.Page(
            "Service worker app", null, new[]
            {
                CaptureDomBuilder.Text("h1", "Service worker app"),
                CaptureDomBuilder.Script("if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw-app/sw.js');"),
            }));
        var pool = new FakeRenderingPool();
        pool.Register(url.CanonicalUrl, "<rendered:sw>");
        var parser = new RegisteredDomParser().Register("<rendered:sw>", raw);
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, parser, _stack.ContentStore);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.ServiceWorkerRegistered);
    }

    [Fact]
    public async Task RenderedWaterfallArtifact_ComesFromRenderLayer_CR006()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/waterfall/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        var waterfall = new[]
        {
            new RenderedNetworkEntry("https://fixture.test/img.png", "image", "GET", 200, "image/png",
                TransferredBytes: 4096, EncodedDataLength: 4000, FromCache: false, FromServiceWorker: false),
        };
        var pool = new FakeRenderingPool { Waterfall = waterfall };
        pool.Register(url.CanonicalUrl, "<rendered:waterfall>");
        var parser = new RegisteredDomParser().Register("<rendered:waterfall>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50));
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, parser, _stack.ContentStore);

        await coordinator.CaptureAsync(Request(url, raw));

        var artifacts = await _stack.Artifacts.ListAsync(new Contracts.Common.ListQuery(0, 50,
            new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = url.Id.ToString() }));
        ContentArtifactDto row = Assert.Single(artifacts.Items, a => a.Kind == ContentArtifactKind.NetworkWaterfall);
        await using Stream? stream = await _stack.ContentStore.OpenReadAsync(row.StorageKey);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        await stream!.CopyToAsync(memory);
        var entries = JsonSerializer.Deserialize<List<RenderedNetworkEntry>>(memory.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entries);
        Assert.Equal(4096, entries![0].TransferredBytes);
        Assert.Equal("image", entries[0].ResourceType);
    }

    [Fact]
    public async Task NoWaterfall_WhenRenderLayerCapturedNone()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/nowaterfall/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        var pool = new FakeRenderingPool(); // Waterfall null
        pool.Register(url.CanonicalUrl, "<rendered:nowf>");
        var parser = new RegisteredDomParser().Register("<rendered:nowf>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50));
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, parser, _stack.ContentStore);

        await coordinator.CaptureAsync(Request(url, raw));

        var artifacts = await _stack.Artifacts.ListAsync(new Contracts.Common.ListQuery(0, 50,
            new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = url.Id.ToString() }));
        Assert.DoesNotContain(artifacts.Items, a => a.Kind == ContentArtifactKind.NetworkWaterfall);
        Assert.Contains(artifacts.Items, a => a.Kind == ContentArtifactKind.RenderedDom);
    }

    [Fact]
    public async Task CaptureSnippets_RunDuringCapture_WithRenderedSource()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/snippet/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        DomDocument rendered = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 80);
        var pool = new FakeRenderingPool();
        pool.Register(url.CanonicalUrl, "<rendered:snippet>");
        var parser = new RegisteredDomParser().Register("<rendered:snippet>", rendered);
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        var coordinator = new CaptureModeCoordinator(CaptureMode.Js, null, pool, parser, _stack.ContentStore, _stack.Urls, _stack.Urls, executor);

        var snippets = new[]
        {
            new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Rendered),
        };
        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw, snippets: snippets));

        Assert.Single(outcome.SnippetResults);
        Assert.Equal(CaptureSource.Rendered, outcome.SnippetResults[0].Source);
        Assert.Single(runner.Calls);
        Assert.NotNull(runner.Calls[0].Context.RenderedDocument);
    }

    private RenderedCaptureRequest Request(UrlRecordDto url, DomDocument raw,
        IReadOnlyList<CaptureSnippetDefinition>? snippets = null) =>
        new(_stack.AuditId, url.Id, url.CanonicalUrl, url.Path, url.Depth, raw, 100,
            _stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, null), CaptureDefaults.DesktopDevice,
            Snippets: snippets);
}
