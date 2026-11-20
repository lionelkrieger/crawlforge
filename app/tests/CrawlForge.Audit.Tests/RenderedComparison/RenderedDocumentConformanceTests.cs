using System.Text;
using System.Text.Json;
using CrawlForge.Audit.Indexability;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Audit.Tests.RenderedComparison.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Constants;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using Xunit;

namespace CrawlForge.Audit.Tests.RenderedComparison;

/// <summary>
/// Contract conformance (WP-10-02 YAML): a rendered document row per URL plus
/// response/rendered comparison findings, end to end through the REAL producer
/// and consumer. The capture coordinator (fake pool, real AngleSharp parser,
/// real content store, real SQLite) persists the RenderedDom artifact; the test
/// reloads it exactly as AuditExecutionEngine.BuildStoredContextAsync does and
/// feeds PageAuditContext.RenderedDocument (6th arg) to the REAL comparer and the
/// REAL audit pipeline. Proves G03 is closed at component level: the pipeline
/// invokes the comparer whenever a rendered document is present, and the
/// script-injected page's rendered word count reaches scopeDiagnostics.
/// </summary>
public sealed class RenderedDocumentConformanceTests : IClassFixture<RenderedCaptureStack>
{
    private readonly RenderedCaptureStack _stack;
    private readonly AngleSharpDomParser _parser = new();

    public RenderedDocumentConformanceTests(RenderedCaptureStack stack) => _stack = stack;

    [Fact]
    public async Task LateMetaChange_DualCapture_PersistsRenderedDom_AndEmitsDeltas()
    {
        const string path = "/pages/late-meta.html";
        UrlRecordDto url = await _stack.CreateUrlAsync(path);
        FakeRenderingPool pool = new FakeRenderingPool().Register(url.CanonicalUrl, FixturePages.LateMetaRendered);
        CaptureModeCoordinator coordinator = NewCoordinator(CaptureMode.Dual, pool);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, FixturePages.LateMetaRaw));
        Assert.True(outcome.Rendered);
        Assert.Equal(CaptureSource.Both, outcome.CaptureSource);

        // A rendered document row exists for the URL.
        ContentArtifactDto renderedDom = await RenderedDomArtifact(url.Id);
        Assert.Equal(ContentArtifactKind.RenderedDom, renderedDom.Kind);

        // Reload as BuildStoredContextAsync does → comparison findings.
        PageAuditContext context = await BuildStoredContextAsync(url, FixturePages.LateMetaRaw);
        Assert.NotNull(context.RenderedDocument);
        ResponseRenderedComparison comparison = await new ResponseRenderedComparer().CompareAsync(context);

        Assert.Contains(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderTitleDelta && f.Severity == FindingSeverity.Error);
        Assert.Contains(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderMetaRobotsDelta && f.Severity == FindingSeverity.Error);
        Assert.Contains(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderCanonicalDelta && f.Severity == FindingSeverity.Error);
        FindingDraft noindex = Assert.Single(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderNoindexDelta);
        Assert.Equal(FindingSeverity.Error, noindex.Severity);
        using JsonDocument evidence = JsonDocument.Parse(noindex.EvidenceJson);
        Assert.Equal("rendered-only", evidence.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public async Task ScriptInjectedContent_RenderedWordCount_ReachesPipelineScope()
    {
        const string path = "/pages/js-content.html";
        UrlRecordDto url = await _stack.CreateUrlAsync(path);
        FakeRenderingPool pool = new FakeRenderingPool().Register(url.CanonicalUrl, FixturePages.JsContentRendered);
        CaptureModeCoordinator coordinator = NewCoordinator(CaptureMode.Js, pool);

        await coordinator.CaptureAsync(Request(url, FixturePages.JsContentRaw));
        PageAuditContext context = await BuildStoredContextAsync(url, FixturePages.JsContentRaw);

        // The REAL pipeline (with the real comparer injected) runs over the
        // stored-context shape the engine produces.
        AuditPipeline pipeline = NewPipeline();
        PageAuditResult result = await pipeline.RunAsync(context);

        // G03: the pipeline invoked the comparer because RenderedDocument is present.
        Assert.Contains(result.Findings, f => f.RuleKey == FindingRuleKeys.RenderTextDelta);

        // The content_scope inventory group carries fullWordCount, which the API
        // (AuditExplorerController.ExtractScopeDiagnostics) maps to the wire
        // scopeDiagnostics.totalWordCount the harness reads (ev.words). The
        // rendered document must drive it (≥ 100 words).
        ElementInventoryItem scope = Assert.Single(result.Inventory, i => i.Group == "content_scope");
        using JsonDocument scopeJson = JsonDocument.Parse(scope.PayloadJson);
        int fullWordCount = scopeJson.RootElement.GetProperty("fullWordCount").GetInt32();
        Assert.True(fullWordCount >= 100, $"rendered fullWordCount={fullWordCount}");
    }

    [Fact]
    public async Task JsOnlyLink_DiscoveredByRender_AndLinksDeltaReported()
    {
        const string path = "/pages/js-link.html";
        UrlRecordDto url = await _stack.CreateUrlAsync(path);
        FakeRenderingPool pool = new FakeRenderingPool().Register(url.CanonicalUrl, FixturePages.JsLinkRendered);
        CaptureModeCoordinator coordinator = NewCoordinator(CaptureMode.Js, pool);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, FixturePages.JsLinkRaw));

        // The JS-only target is a discovered-by-render candidate.
        RenderedLinkCandidate candidate = Assert.Single(outcome.RenderedOnlyLinks);
        Assert.Equal(FixturePages.BaseUrl + "/pages/js-only-target.html", candidate.AbsoluteUrl);

        PageAuditContext context = await BuildStoredContextAsync(url, FixturePages.JsLinkRaw);
        ResponseRenderedComparison comparison = await new ResponseRenderedComparer().CompareAsync(context);
        Assert.Contains(comparison.Findings, f => f.RuleKey == FindingRuleKeys.RenderLinksDelta);
    }

    [Fact]
    public async Task ServiceWorkerPage_InformationalFinding_ThroughPipeline()
    {
        const string path = "/sw-app/";
        UrlRecordDto url = await _stack.CreateUrlAsync(path);
        FakeRenderingPool pool = new FakeRenderingPool().Register(url.CanonicalUrl, FixturePages.SwAppRendered);
        CaptureModeCoordinator coordinator = NewCoordinator(CaptureMode.Js, pool);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, FixturePages.SwAppRaw));
        Assert.True(outcome.ServiceWorkerRegistered);

        PageAuditContext context = await BuildStoredContextAsync(url, FixturePages.SwAppRaw);
        PageAuditResult result = await NewPipeline().RunAsync(context);
        FindingDraft finding = Assert.Single(result.Findings, f => f.RuleKey == FindingRuleKeys.RenderServiceWorkerRegistered);
        Assert.Equal(FindingSeverity.Notice, finding.Severity);
    }

    [Fact]
    public async Task NonJsMode_NoRenderedDom_NoComparisonFindings()
    {
        const string path = "/pages/nonjs.html";
        UrlRecordDto url = await _stack.CreateUrlAsync(path);
        FakeRenderingPool pool = new FakeRenderingPool();
        CaptureModeCoordinator coordinator = NewCoordinator(CaptureMode.NonJs, pool);

        RenderedCaptureOutcome outcome = await coordinator.CaptureAsync(Request(url, FixturePages.LateMetaRaw));

        Assert.False(outcome.Rendered);
        Assert.Empty(pool.Requests);
        var artifacts = await _stack.Artifacts.ListAsync(new ListQuery(0, 50,
            new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = url.Id.ToString() }));
        Assert.DoesNotContain(artifacts.Items, a => a.Kind == ContentArtifactKind.RenderedDom);
    }

    // ── Helpers mirroring the engine seams ───────────────────────────────────

    private CaptureModeCoordinator NewCoordinator(CaptureMode mode, IRenderingPool pool) =>
        new(mode, null, pool, _parser, _stack.ContentStore, _stack.Urls, _stack.Urls);

    private RenderedCaptureRequest Request(UrlRecordDto url, string rawHtml) =>
        new(_stack.AuditId, url.Id, url.CanonicalUrl, url.Path, url.Depth,
            _parser.Parse(rawHtml, url.CanonicalUrl), Encoding.UTF8.GetByteCount(rawHtml),
            ComparisonContext.Configuration(), CaptureDefaults.DesktopDevice);

    private async Task<ContentArtifactDto> RenderedDomArtifact(long urlId)
    {
        var artifacts = await _stack.Artifacts.ListAsync(new ListQuery(0, 50,
            new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = urlId.ToString() }));
        return Assert.Single(artifacts.Items, a => a.Kind == ContentArtifactKind.RenderedDom);
    }

    /// <summary>
    /// Mirrors AuditExecutionEngine.BuildStoredContextAsync: store the raw Body
    /// artifact (the engine does this during fetch), reload Body + RenderedDom,
    /// parse both, and pass the rendered document as the 6th context argument.
    /// </summary>
    private async Task<PageAuditContext> BuildStoredContextAsync(UrlRecordDto url, string rawHtml)
    {
        // The engine stores the raw body during the fetch step.
        await _stack.ContentStore.PutAsync(_stack.AuditId, url.Id, ContentArtifactKind.Body,
            new MemoryStream(Encoding.UTF8.GetBytes(rawHtml)));

        var artifacts = await _stack.Artifacts.ListAsync(new ListQuery(0, 50,
            new Dictionary<string, string> { ["auditId"] = _stack.AuditId.ToString(), ["urlId"] = url.Id.ToString() }));
        ContentArtifactDto body = Assert.Single(artifacts.Items, a => a.Kind == ContentArtifactKind.Body);
        ContentArtifactDto? renderedDom = artifacts.Items.FirstOrDefault(a => a.Kind == ContentArtifactKind.RenderedDom);

        DomDocument document = _parser.Parse(await ReadArtifact(body.StorageKey), url.CanonicalUrl);
        DomDocument? renderedDocument = renderedDom is null
            ? null
            : _parser.Parse(await ReadArtifact(renderedDom.StorageKey), url.CanonicalUrl);

        var fetch = new HttpFetchResult(url.CanonicalUrl, url.FinalStatusCode ?? 200,
            new Dictionary<string, string>(), Encoding.UTF8.GetBytes(rawHtml), TimeSpan.Zero, TimeSpan.Zero);
        return new PageAuditContext(_stack.AuditId, url, ComparisonContext.Configuration(), fetch, document, renderedDocument, null, null);
    }

    private async Task<string> ReadArtifact(string storageKey)
    {
        await using Stream? stream = await _stack.ContentStore.OpenReadAsync(storageKey);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        await stream!.CopyToAsync(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private AuditPipeline NewPipeline() =>
        new(_parser, new ElementInventoryExtractor(), new ContentScopeResolver(), new EmptyRuleHost(),
            projects: null, profileStore: null, indexability: null, headerHtmlEquivalency: null,
            responseRenderedComparer: new ResponseRenderedComparer(), soft404Detector: null);
}
