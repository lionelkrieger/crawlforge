using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using CrawlForge.Crawler.Rendering;
using CrawlForge.Crawler.Tests.CaptureModes.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes;

/// <summary>
/// WP-10-04 per-audit rendering-pool wiring (WP-10-01 handoff §4.1): the
/// coordinator factory composes <see cref="RenderingPoolOptions"/> from the
/// frozen <see cref="CaptureSettings"/> snapshot for rendering-dependent
/// audits, those options reach the pool that audit renders through, non-JS
/// audits never build (or launch) a pool, and the per-audit pool is disposed
/// with its coordinator while the shared singleton is never disposed here.
/// Fakes only — no browser is ever launched.
/// </summary>
public sealed class PerAuditRenderingPoolTests : IClassFixture<CaptureTestStack>
{
    private readonly CaptureTestStack _stack;

    public PerAuditRenderingPoolTests(CaptureTestStack stack) => _stack = stack;

    // ── CaptureSettings → per-audit pool options ─────────────────────────────

    [Fact]
    public async Task Factory_PerAuditPool_ReceivesCaptureSettingsOptions_AndServesTheAudit()
    {
        var settings = new CaptureSettings(RenderWaitQuietMs: 1234, RenderWaitHardCapMs: 9000, RenderingPoolSize: 3);
        var shared = new FakeRenderingPool();
        var auditPool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        RenderingPoolOptions? observed = null;
        var factory = new CaptureModeCoordinatorFactory(
            shared, parser, _stack.ContentStore, _stack.Urls, _stack.Urls,
            renderingPoolFactory: options =>
            {
                observed = options;
                return auditPool;
            });

        ICaptureModeCoordinator coordinator =
            factory.Create(_stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, settings));

        // The frozen snapshot's non-default knobs reached the pool options —
        // exactly the WP-10-01 FromCaptureSettings mapping.
        Assert.NotNull(observed);
        Assert.Equal(3, observed!.MaxConcurrency);
        Assert.Equal(1234, observed.RenderWaitQuietMs);
        Assert.Equal(9000, observed.RenderWaitHardCapMs);
        RenderingPoolOptions expected = RenderingPoolOptions.FromCaptureSettings(settings);
        Assert.Equal(expected.MaxConcurrency, observed.MaxConcurrency);
        Assert.Equal(expected.RenderWaitQuietMs, observed.RenderWaitQuietMs);
        Assert.Equal(expected.RenderWaitHardCapMs, observed.RenderWaitHardCapMs);

        // The pool built from those options is the one this audit renders through.
        UrlRecordDto url = await _stack.CreateUrlAsync("/options/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        const string html = "<rendered:options>";
        auditPool.Register(url.CanonicalUrl, html);
        parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 80));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.Rendered);
        RenderRequest request = Assert.Single(auditPool.Requests);
        Assert.Equal(url.CanonicalUrl, request.Url);
        Assert.Empty(shared.Requests);
    }

    [Fact]
    public void Factory_NullCaptureSettings_UsesSpecBaselineOptions()
    {
        var shared = new FakeRenderingPool();
        RenderingPoolOptions? observed = null;
        var factory = new CaptureModeCoordinatorFactory(
            shared, new RegisteredDomParser(), _stack.ContentStore, _stack.Urls, _stack.Urls,
            renderingPoolFactory: options =>
            {
                observed = options;
                return new FakeRenderingPool();
            });

        factory.Create(_stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, settings: null));

        Assert.NotNull(observed);
        Assert.Equal(CaptureSettings.DefaultRenderingPoolSize, observed!.MaxConcurrency);
        Assert.Equal(500, observed.RenderWaitQuietMs);
        Assert.Equal(20_000, observed.RenderWaitHardCapMs);
    }

    // ── Non-JS audits never build (or launch) a pool ────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(CaptureMode.NonJs)]
    public async Task Factory_NonJsAudit_NeverBuildsPerAuditPool(CaptureMode? mode)
    {
        var shared = new FakeRenderingPool();
        var built = new List<RenderingPoolOptions>();
        var factory = new CaptureModeCoordinatorFactory(
            shared, new RegisteredDomParser(), _stack.ContentStore, _stack.Urls, _stack.Urls,
            renderingPoolFactory: options =>
            {
                built.Add(options);
                return new FakeRenderingPool();
            });
        var settings = new CaptureSettings(RenderWaitQuietMs: 1234, RenderingPoolSize: 3);

        ICaptureModeCoordinator coordinator =
            factory.Create(_stack.NewConfiguration(_stack.ProjectId, mode, settings));
        Assert.Equal(CaptureMode.NonJs, coordinator.Mode);
        Assert.Empty(built);

        UrlRecordDto url = await _stack.CreateUrlAsync($"/nonjs/no-pool-{mode?.ToString() ?? "null"}");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.False(outcome.Rendered);
        Assert.Empty(built);
        Assert.Empty(shared.Requests); // no render at all — no browser could launch
    }

    [Fact]
    public async Task Factory_NonJs_RenderedOverride_RendersThroughSharedPool_WithoutPerAuditPool()
    {
        // §5.6.1 override semantics: an explicit rendered pattern override
        // renders even in non-JS mode — through the shared pool, never a
        // per-audit instance (non-JS audits own nothing to dispose).
        var shared = new FakeRenderingPool();
        var built = new List<RenderingPoolOptions>();
        var parser = new RegisteredDomParser();
        var factory = new CaptureModeCoordinatorFactory(
            shared, parser, _stack.ContentStore, _stack.Urls, _stack.Urls,
            renderingPoolFactory: options =>
            {
                built.Add(options);
                return new FakeRenderingPool();
            });
        var settings = new CaptureSettings(PatternOverrides:
        [
            new CapturePatternOverride("/spa/*", PatternCaptureMode.Rendered),
        ]);

        ICaptureModeCoordinator coordinator =
            factory.Create(_stack.NewConfiguration(_stack.ProjectId, CaptureMode.NonJs, settings));

        UrlRecordDto url = await _stack.CreateUrlAsync("/spa/home");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        const string html = "<rendered:spa>";
        shared.Register(url.CanonicalUrl, html);
        parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 80));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.Rendered);
        Assert.Equal("override:rendered", outcome.Decision.Reason);
        Assert.Single(shared.Requests);
        Assert.Empty(built);
    }

    // ── Per-audit pool lifetime ─────────────────────────────────────────────

    [Fact]
    public async Task Factory_PerAuditPool_IsDisposedWithItsCoordinator()
    {
        var shared = new FakeRenderingPool();
        var auditPool = new FakeRenderingPool();
        var factory = new CaptureModeCoordinatorFactory(
            shared, new RegisteredDomParser(), _stack.ContentStore, _stack.Urls, _stack.Urls,
            renderingPoolFactory: _ => auditPool);

        ICaptureModeCoordinator coordinator =
            factory.Create(_stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, null));
        IAsyncDisposable lifetime = Assert.IsAssignableFrom<IAsyncDisposable>(coordinator);

        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync(); // idempotent

        Assert.Equal(1, auditPool.DisposeCount);
        Assert.Equal(0, shared.DisposeCount);
    }

    [Fact]
    public async Task Factory_NonJsCoordinator_NeverDisposesSharedPool()
    {
        var shared = new FakeRenderingPool();
        var factory = new CaptureModeCoordinatorFactory(
            shared, new RegisteredDomParser(), _stack.ContentStore, _stack.Urls, _stack.Urls);

        ICaptureModeCoordinator coordinator =
            factory.Create(_stack.NewConfiguration(_stack.ProjectId, CaptureMode.NonJs, null));
        IAsyncDisposable lifetime = Assert.IsAssignableFrom<IAsyncDisposable>(coordinator);

        await lifetime.DisposeAsync();

        Assert.Equal(0, shared.DisposeCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private RenderedCaptureRequest Request(UrlRecordDto url, DomDocument raw, long rawBodyBytes = 100) =>
        new(_stack.AuditId, url.Id, url.CanonicalUrl, url.Path, url.Depth, raw, rawBodyBytes,
            _stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, null), CaptureDefaults.DesktopDevice);
}
