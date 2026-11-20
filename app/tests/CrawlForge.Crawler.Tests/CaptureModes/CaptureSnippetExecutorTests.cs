using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using CrawlForge.Crawler.Tests.CaptureModes.TestSupport;
using Xunit;

namespace CrawlForge.Crawler.Tests.CaptureModes;

/// <summary>
/// Capture-time snippet execution (§7.17): raw/rendered/both source selection,
/// the rendering gate, outer timeout isolation, and the no-silent-raw-fallback
/// rule for rendered-source snippets without a rendered capture.
/// </summary>
public sealed class CaptureSnippetExecutorTests
{
    [Fact]
    public async Task RawSource_EvaluatesRawDocument_Only()
    {
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: Doc("rendered"));

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Raw) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        CaptureSnippetOutcome outcome = Assert.Single(outcomes);
        Assert.Equal(CaptureSource.Raw, outcome.Source);
        Assert.Single(runner.Calls);
        Assert.Null(runner.Calls[0].Context.RenderedDocument);
        Assert.Equal("raw", runner.Calls[0].Context.Document!.Url);
    }

    [Fact]
    public async Task RenderedSource_EvaluatesRenderedDocument()
    {
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: Doc("rendered"));

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Rendered) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        Assert.Equal(CaptureSource.Rendered, Assert.Single(outcomes).Source);
        Assert.NotNull(runner.Calls[0].Context.RenderedDocument);
        Assert.Equal("rendered", runner.Calls[0].Context.Document!.Url);
    }

    [Fact]
    public async Task BothSource_RunsAgainstRawAndRendered()
    {
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: Doc("rendered"));

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Both) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        Assert.Equal(2, outcomes.Count);
        Assert.Contains(outcomes, o => o.Source == CaptureSource.Raw);
        Assert.Contains(outcomes, o => o.Source == CaptureSource.Rendered);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task RenderedSource_WithoutRenderedCapture_YieldsEmpty_NotRawFallback()
    {
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: null);

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Rendered) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        CaptureSnippetOutcome outcome = Assert.Single(outcomes);
        Assert.Equal(CaptureSource.Rendered, outcome.Source);
        Assert.Empty(runner.Calls); // never silently evaluated against raw
        Assert.All(outcome.Results, r => Assert.Equal("[]", r.RowsJson));
    }

    [Fact]
    public async Task NonJsAudit_WithoutRendering_IsOutOfScope()
    {
        var runner = new RecordingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: null, mode: CaptureMode.NonJs);

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Raw) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        Assert.Empty(outcomes);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task OuterTimeout_IsolatesHungRunner()
    {
        var runner = new DelayingSnippetRunner { Delay = TimeSpan.FromSeconds(30) };
        var executor = new CaptureSnippetExecutor(runner, outerTimeout: TimeSpan.FromMilliseconds(50));
        PageAuditContext context = Context(raw: Doc("raw"), rendered: null);

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Raw) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        CaptureSnippetOutcome outcome = Assert.Single(outcomes);
        Assert.Contains("k", outcome.TimedOutKeys);
        Assert.All(outcome.Results, r => Assert.Equal("[]", r.RowsJson));
    }

    [Fact]
    public async Task RunnerFailure_IsIsolated_NeverThrows()
    {
        var runner = new ThrowingSnippetRunner();
        var executor = new CaptureSnippetExecutor(runner);
        PageAuditContext context = Context(raw: Doc("raw"), rendered: null);

        var snippets = new[] { new CaptureSnippetDefinition(new JsSnippetDefinition("k", "K", "cf.submit(1);"), CaptureSource.Raw) };
        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(context, snippets);

        Assert.All(Assert.Single(outcomes).Results, r => Assert.Equal("[]", r.RowsJson));
    }

    private static DomDocument Doc(string url) =>
        new(url, CaptureDomBuilder.Page("T", null, new[] { CaptureDomBuilder.Text("p", "text") }), Array.Empty<string>());

    private static PageAuditContext Context(DomDocument? raw, DomDocument? rendered, CaptureMode mode = CaptureMode.Js)
    {
        var url = new UrlRecordDto(
            1, 1, "https://fixture.test/page", "fixture.test", "/page", 0, DiscoverySource.Seed,
            CrawlStatusClass.Success, 200, null, "text/html", 100, null, RobotsState.Allowed,
            IndexabilityFlags.None, true);
        var configuration = new CrawlConfigurationDto(
            1, 1, "cfg", "[]", null, null, null, RenderMode.None, null, null, null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        { CaptureMode = mode };
        return new PageAuditContext(1, url, configuration, null, raw, rendered, null, null);
    }

    private sealed class DelayingSnippetRunner : IJsSnippetRunner
    {
        public TimeSpan Delay { get; set; }

        public async Task<IReadOnlyList<SnippetResultDraft>> RunAsync(
            PageAuditContext context, IReadOnlyList<JsSnippetDefinition> snippets, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Delay, cancellationToken);
            return snippets.Select(s => new SnippetResultDraft(s.Key, "[]", "[]", false)).ToList();
        }
    }

    private sealed class ThrowingSnippetRunner : IJsSnippetRunner
    {
        public Task<IReadOnlyList<SnippetResultDraft>> RunAsync(
            PageAuditContext context, IReadOnlyList<JsSnippetDefinition> snippets, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("runner blew up");
    }
}
