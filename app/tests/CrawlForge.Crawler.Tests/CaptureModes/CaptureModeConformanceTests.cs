using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using CrawlForge.Crawler.Tests.CaptureModes.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes;

/// <summary>
/// Contract conformance for the capture-mode execution component (WP-10-02;
/// FS-CRAWL-001 §5.6.1). Proves the locked wire vocabulary maps onto the mode
/// semantics, every mode dispatches correctly, manual overrides win over
/// adaptive switching and apply on top of every mode, adaptive sampling and
/// material-change switching behave per spec, the dual-storage threshold warns
/// without a hard cap, and per-URL capture source is stamped through the real
/// SQLite stack. Rendered artifacts persist through the real content store.
/// </summary>
public sealed class CaptureModeConformanceTests : IClassFixture<CaptureTestStack>
{
    private readonly CaptureTestStack _stack;

    public CaptureModeConformanceTests(CaptureTestStack stack) => _stack = stack;

    // ── Locked wire vocabulary ───────────────────────────────────────────────

    [Fact]
    public void CaptureModeWireTokens_AreLocked()
    {
        Assert.Equal("non-js", EnumWireNames.ToWireValue(CaptureMode.NonJs));
        Assert.Equal("js", EnumWireNames.ToWireValue(CaptureMode.Js));
        Assert.Equal("dual", EnumWireNames.ToWireValue(CaptureMode.Dual));
        Assert.Equal("adaptive", EnumWireNames.ToWireValue(CaptureMode.Adaptive));
        Assert.Equal("non-rendered", EnumWireNames.ToWireValue(PatternCaptureMode.NonRendered));
        Assert.Equal("rendered", EnumWireNames.ToWireValue(PatternCaptureMode.Rendered));
        Assert.Equal("raw", EnumWireNames.ToWireValue(CaptureSource.Raw));
        Assert.Equal("rendered", EnumWireNames.ToWireValue(CaptureSource.Rendered));
        Assert.Equal("both", EnumWireNames.ToWireValue(CaptureSource.Both));

        foreach (CaptureMode mode in Enum.GetValues<CaptureMode>())
        {
            Assert.True(EnumWireNames.TryParse<CaptureMode>(EnumWireNames.ToWireValue(mode), out var parsed));
            Assert.Equal(mode, parsed);
        }
    }

    [Fact]
    public void NullCaptureMode_DefaultsToNonJs_AndNeverRenders()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var factory = new CaptureModeCoordinatorFactory(pool, parser, _stack.ContentStore);
        CrawlConfigurationDto configuration = _stack.NewConfiguration(_stack.ProjectId, mode: null, settings: null);

        ICaptureModeCoordinator coordinator = factory.Create(configuration);

        Assert.Equal(CaptureMode.NonJs, coordinator.Mode);
        Assert.False(coordinator.Decide("https://fixture.test/blog/post").ShouldRender);
        Assert.Empty(pool.Requests);
    }

    // ── Mode dispatch ────────────────────────────────────────────────────────

    [Fact]
    public async Task NonJsMode_FetchesOnly_NoRender()
    {
        UrlRecordDto url = await _stack.CreateUrlAsync("/nonjs/page");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        var pool = new FakeRenderingPool();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.NonJs, null, pool, new RegisteredDomParser());

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.False(outcome.Rendered);
        Assert.Equal(CaptureSource.Raw, outcome.CaptureSource);
        Assert.Empty(pool.Requests);
        Assert.Null(outcome.RenderedDocument);
    }

    [Fact]
    public async Task JsMode_RendersEveryInScopePage_AndStampsRendered()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Js, null, pool, parser);

        for (int i = 0; i < 3; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/js/page-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            DomDocument rendered = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 80);
            string html = $"<rendered:js-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, rendered);

            RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

            Assert.True(outcome.Rendered);
            Assert.Equal(CaptureSource.Rendered, outcome.CaptureSource);
            Assert.NotNull(outcome.RenderedDocument);
            UrlRecordDto? stored = await _stack.Urls.GetByIdAsync(url.Id);
            Assert.Equal(CaptureSource.Rendered, stored!.CaptureSource);
        }

        Assert.Equal(3, pool.Requests.Count);
    }

    [Fact]
    public async Task DualMode_StoresBothCaptures_AndComparesEveryUrl()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Dual, null, pool, parser);

        for (int i = 0; i < 2; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/dual/page-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            DomDocument rendered = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            string html = $"<rendered:dual-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, rendered);

            RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

            Assert.True(outcome.Rendered);
            Assert.Equal(CaptureSource.Both, outcome.CaptureSource);
            // Dual compares EVERY URL: material-change facts are always produced.
            Assert.NotNull(outcome.MaterialChange);
            UrlRecordDto? stored = await _stack.Urls.GetByIdAsync(url.Id);
            Assert.Equal(CaptureSource.Both, stored!.CaptureSource);

            // Both captures stored: the rendered DOM artifact alongside the raw
            // Body artifact the engine stores.
            await _stack.ContentStore.PutAsync(_stack.AuditId, url.Id, ContentArtifactKind.Body, new MemoryStream(new byte[] { 1, 2, 3 }));
            var artifacts = await _stack.Artifacts.ListAsync(new Contracts.Common.ListQuery(0, 50,
                new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = url.Id.ToString() }));
            Assert.Contains(artifacts.Items, a => a.Kind == ContentArtifactKind.Body);
            Assert.Contains(artifacts.Items, a => a.Kind == ContentArtifactKind.RenderedDom);
        }
    }

    // ── Adaptive sampling + switching ────────────────────────────────────────

    [Fact]
    public async Task AdaptiveMode_SamplesDefaultFourPerGroup_ThenRaw()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, null, pool, parser);

        for (int i = 0; i < 6; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/blog/post-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            string html = $"<rendered:blog-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50)); // non-material

            RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

            if (i < 4)
            {
                Assert.True(outcome.Rendered);
                Assert.Equal("adaptive:sample", outcome.Decision.Reason);
                Assert.Equal(CaptureSource.Both, outcome.CaptureSource);
            }
            else
            {
                Assert.False(outcome.Rendered);
                Assert.Equal("adaptive:raw", outcome.Decision.Reason);
            }
        }

        Assert.Equal(4, pool.Requests.Count);
        Assert.Empty(coordinator.DrainAuditFindings()); // no material change → no switch
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 3)] // clamped to the spec range lower bound
    [InlineData(9, 5)] // clamped to the spec range upper bound
    public async Task AdaptiveSampleSize_IsConfigurable_ClampedToSpecRange(int configured, int effective)
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var settings = new CaptureSettings(AdaptiveSampleSizePerGroup: configured);
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, settings, pool, parser);

        for (int i = 0; i < effective + 2; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/clamp{configured}/post-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            string html = $"<rendered:clamp{configured}-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50));
            await coordinator.CaptureAsync(Request(url, raw));
        }

        Assert.Equal(effective, pool.Requests.Count);
    }

    [Fact]
    public async Task AdaptiveMode_MaterialTitleChange_SwitchesGroup_AndReportsTemplate()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, null, pool, parser);

        // First sample materially changes (title altered by script).
        UrlRecordDto first = await _stack.CreateUrlAsync("/product/item-0");
        DomDocument rawFirst = CaptureDomBuilder.WordsPage(first.CanonicalUrl, "Raw title", 50);
        pool.Register(first.CanonicalUrl, "<rendered:product-0>");
        parser.Register("<rendered:product-0>", CaptureDomBuilder.WordsPage(first.CanonicalUrl, "Rendered title", 50));
        RenderedCaptureOutcome firstOutcome = await coordinator.CaptureAsync(Request(first, rawFirst));
        Assert.True(firstOutcome.MaterialChange!.IsMaterial);
        Assert.True(firstOutcome.MaterialChange.TitleChanged);

        // The rest of the group switches to rendered capture.
        for (int i = 1; i < 4; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/product/item-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "Raw title", 50);
            string html = $"<rendered:product-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "Rendered title", 50));
            RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));
            Assert.True(outcome.Rendered);
            Assert.Equal("adaptive:switched", outcome.Decision.Reason);
            Assert.Equal(CaptureSource.Rendered, outcome.CaptureSource);
        }

        IReadOnlyList<FindingDraft> findings = coordinator.DrainAuditFindings();
        FindingDraft switchFinding = Assert.Single(findings, f => f.RuleKey == FindingRuleKeys.ContentJavascriptDependentTemplate);
        Assert.Equal(AuditCategory.Content, switchFinding.Category);
        Assert.Equal(first.Id, switchFinding.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(switchFinding.EvidenceJson);
        Assert.Equal("/product/*", evidence.RootElement.GetProperty("groupKey").GetString());
        Assert.True(evidence.RootElement.GetProperty("titleChanged").GetBoolean());
        Assert.Empty(coordinator.DrainAuditFindings()); // drained once
    }

    [Fact]
    public async Task AdaptiveMode_WordCountChangeAboveThreshold_IsMaterial()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, null, pool, parser);

        UrlRecordDto url = await _stack.CreateUrlAsync("/words/item-0");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 100);
        pool.Register(url.CanonicalUrl, "<rendered:words-0>");
        parser.Register("<rendered:words-0>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 200));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.MaterialChange!.IsMaterial);
        Assert.Equal(100, outcome.MaterialChange.RawWordCount);
        Assert.Equal(200, outcome.MaterialChange.RenderedWordCount);
        Assert.Equal(1.0, outcome.MaterialChange.WordCountChangeFraction!.Value, 3);
        Assert.Contains(coordinator.DrainAuditFindings(), f => f.RuleKey == FindingRuleKeys.ContentJavascriptDependentTemplate);
    }

    [Fact]
    public async Task AdaptiveMode_WordCountThresholdIsConfigurable()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        // 100 → 200 words is a 100% change; a 150% threshold makes it non-material.
        var settings = new CaptureSettings(MaterialChangeWordCountThreshold: 1.5);
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, settings, pool, parser);

        UrlRecordDto url = await _stack.CreateUrlAsync("/threshold/item-0");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 100);
        pool.Register(url.CanonicalUrl, "<rendered:threshold-0>");
        parser.Register("<rendered:threshold-0>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 200));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.False(outcome.MaterialChange!.IsMaterial);
        Assert.Empty(coordinator.DrainAuditFindings());
    }

    [Fact]
    public async Task AdaptiveMode_ZeroToNonzeroWordCount_IsAlwaysMaterial()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, null, pool, parser);

        UrlRecordDto url = await _stack.CreateUrlAsync("/injected/item-0");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 0);
        pool.Register(url.CanonicalUrl, "<rendered:injected-0>");
        parser.Register("<rendered:injected-0>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 200));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.MaterialChange!.IsMaterial);
        Assert.Null(outcome.MaterialChange.WordCountChangeFraction);
    }

    // ── Manual overrides win over adaptive and apply on top of every mode ────

    [Fact]
    public async Task NonRenderedOverride_WinsOverJsMode()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var settings = new CaptureSettings(PatternOverrides: new[]
        {
            new CapturePatternOverride("/private/*", PatternCaptureMode.NonRendered),
        });
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Js, settings, pool, parser);

        UrlRecordDto hidden = await _stack.CreateUrlAsync("/private/secret");
        DomDocument raw = CaptureDomBuilder.WordsPage(hidden.CanonicalUrl, "T", 50);
        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(hidden, raw));

        Assert.False(outcome.Rendered);
        Assert.True(outcome.Decision.OverrideApplied);
        Assert.Equal("override:non-rendered", outcome.Decision.Reason);
        Assert.Empty(pool.Requests);
    }

    [Fact]
    public async Task RenderedOverride_AppliesOnTopOfNonJsMode()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var settings = new CaptureSettings(PatternOverrides: new[]
        {
            new CapturePatternOverride("/rendered-override/*", PatternCaptureMode.Rendered),
        });
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.NonJs, settings, pool, parser);

        UrlRecordDto url = await _stack.CreateUrlAsync("/rendered-override/post-1");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
        pool.Register(url.CanonicalUrl, "<rendered:override>");
        parser.Register("<rendered:override>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 80));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.True(outcome.Rendered);
        Assert.Equal("override:rendered", outcome.Decision.Reason);
        Assert.Equal(CaptureSource.Rendered, outcome.CaptureSource);
        Assert.Single(pool.Requests);
    }

    [Fact]
    public async Task NonRenderedOverride_WinsOverAdaptiveSwitching()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var settings = new CaptureSettings(PatternOverrides: new[]
        {
            new CapturePatternOverride("/switch/*", PatternCaptureMode.NonRendered),
        });
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Adaptive, settings, pool, parser);

        // Even a materially-changing URL in the overridden group stays raw.
        UrlRecordDto url = await _stack.CreateUrlAsync("/switch/item-0");
        DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "Raw", 50);
        pool.Register(url.CanonicalUrl, "<rendered:switch-0>");
        parser.Register("<rendered:switch-0>", CaptureDomBuilder.WordsPage(url.CanonicalUrl, "Rendered", 200));

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw));

        Assert.False(outcome.Rendered);
        Assert.Equal("override:non-rendered", outcome.Decision.Reason);
        Assert.Empty(pool.Requests);
        Assert.Empty(coordinator.DrainAuditFindings());
    }

    [Fact]
    public async Task FirstMatchingOverride_Wins()
    {
        var pool = new FakeRenderingPool();
        var settings = new CaptureSettings(PatternOverrides: new[]
        {
            new CapturePatternOverride("/blog/*", PatternCaptureMode.NonRendered),
            new CapturePatternOverride("/blog/special/*", PatternCaptureMode.Rendered),
        });
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Js, settings, pool, new RegisteredDomParser());

        // "/blog/special/x" matches both; the first (non-rendered) wins.
        CaptureDecision decision = coordinator.Decide("https://fixture.test/blog/special/x");
        Assert.False(decision.ShouldRender);
        Assert.Equal("override:non-rendered", decision.Reason);
    }

    // ── Dual storage warning (never a hard cap) ──────────────────────────────

    [Fact]
    public async Task DualStorageAboveThreshold_WarnsOnce_NeverCaps()
    {
        var pool = new FakeRenderingPool();
        var parser = new RegisteredDomParser();
        var settings = new CaptureSettings(DualStorageWarningThresholdBytes: 50);
        CaptureModeCoordinator coordinator = Coordinator(CaptureMode.Dual, settings, pool, parser);

        for (int i = 0; i < 3; i++)
        {
            UrlRecordDto url = await _stack.CreateUrlAsync($"/storage/page-{i}");
            DomDocument raw = CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50);
            string html = $"<rendered:storage-{i}>";
            pool.Register(url.CanonicalUrl, html);
            parser.Register(html, CaptureDomBuilder.WordsPage(url.CanonicalUrl, "T", 50));
            RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, raw, rawBodyBytes: 100));
            // No hard cap: every URL keeps rendering past the threshold.
            Assert.True(outcome.Rendered);
        }

        Assert.Equal(3, pool.Requests.Count);
        IReadOnlyList<FindingDraft> findings = coordinator.DrainAuditFindings();
        FindingDraft warning = Assert.Single(findings, f => f.RuleKey == CaptureRuleKeys.DualStorageWarning);
        Assert.Equal(FindingSeverity.Warning, warning.Severity);
        Assert.Null(warning.UrlId);
        using JsonDocument evidence = JsonDocument.Parse(warning.EvidenceJson);
        Assert.False(evidence.RootElement.GetProperty("hardCap").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("storedBytes").GetInt64() > 50);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CaptureModeCoordinator Coordinator(
        CaptureMode mode, CaptureSettings? settings, FakeRenderingPool pool, RegisteredDomParser parser,
        ICaptureSnippetExecutor? snippets = null) =>
        new(mode, settings, pool, parser, _stack.ContentStore, _stack.Urls, _stack.Urls, snippets);

    private RenderedCaptureRequest Request(UrlRecordDto url, DomDocument raw, long rawBodyBytes = 100,
        IReadOnlyList<CaptureSnippetDefinition>? snippets = null) =>
        new(_stack.AuditId, url.Id, url.CanonicalUrl, url.Path, url.Depth, raw, rawBodyBytes,
            _stack.NewConfiguration(_stack.ProjectId, CaptureMode.Js, null), CaptureDefaults.DesktopDevice,
            Snippets: snippets);
}
