using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Tests.RenderedComparison.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Capture;
using Xunit;

namespace CrawlForge.Audit.Tests.RenderedComparison;

/// <summary>
/// Capture-time snippet execution (§7.17) through the REAL isolated Jint wrapper
/// (JintSnippetRunner) composed with the WP-10-02 source-selecting executor.
/// The wrapper API is column-major (<c>cf.submit(column, value)</c>) over the
/// read-only <c>cf.html</c>/<c>cf.text</c>/<c>cf.url</c> surface — no DOM object,
/// no CLR interop, no network. These tests prove raw/rendered/both produce
/// distinct typed results, execution is time-bounded and isolated, and the
/// wrapper exposes no backend surface.
/// </summary>
public sealed class CaptureSnippetSourceTests
{
    private readonly AngleSharpDomParser _parser = new();

    private PageAuditContext Context(string rawTitle, string renderedTitle)
    {
        DomDocument raw = _parser.Parse($"<html><head><title>{rawTitle}</title></head><body><p>raw body</p></body></html>", FixturePages.BaseUrl + "/page");
        DomDocument rendered = _parser.Parse($"<html><head><title>{renderedTitle}</title></head><body><p>rendered body</p></body></html>", FixturePages.BaseUrl + "/page");
        return ComparisonContext.Context(raw, rendered);
    }

    private static string RowString(SnippetResultDraft draft, string column)
    {
        using JsonDocument rows = JsonDocument.Parse(draft.RowsJson);
        return rows.RootElement.GetArrayLength() == 0
            ? string.Empty
            : rows.RootElement[0].GetProperty(column).GetString() ?? string.Empty;
    }

    [Fact]
    public async Task BothSource_ProducesDistinctRawAndRenderedResults()
    {
        var executor = new CaptureSnippetExecutor(new JintSnippetRunner());
        var snippets = new[]
        {
            new CaptureSnippetDefinition(new JsSnippetDefinition("markup", "Markup", "cf.submit('markup', cf.html);"), CaptureSource.Both),
        };

        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(Context("Raw title", "Rendered title"), snippets);

        CaptureSnippetOutcome raw = Assert.Single(outcomes, o => o.Source == CaptureSource.Raw);
        CaptureSnippetOutcome rendered = Assert.Single(outcomes, o => o.Source == CaptureSource.Rendered);
        string rawMarkup = RowString(Assert.Single(raw.Results), "markup");
        string renderedMarkup = RowString(Assert.Single(rendered.Results), "markup");
        Assert.Contains("Raw title", rawMarkup);
        Assert.DoesNotContain("Rendered title", rawMarkup);
        Assert.Contains("Rendered title", renderedMarkup);
        Assert.DoesNotContain("Raw title", renderedMarkup);
    }

    [Fact]
    public async Task RawSource_EvaluatesRawDocumentOnly()
    {
        var executor = new CaptureSnippetExecutor(new JintSnippetRunner());
        var snippets = new[]
        {
            new CaptureSnippetDefinition(new JsSnippetDefinition("markup", "Markup", "cf.submit('markup', cf.html);"), CaptureSource.Raw),
        };

        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(Context("Raw title", "Rendered title"), snippets);

        CaptureSnippetOutcome raw = Assert.Single(outcomes);
        Assert.Equal(CaptureSource.Raw, raw.Source);
        string markup = RowString(Assert.Single(raw.Results), "markup");
        Assert.Contains("Raw title", markup);
        Assert.DoesNotContain("Rendered title", markup);
    }

    [Fact]
    public async Task RenderedSource_EvaluatesRenderedDocumentOnly()
    {
        var executor = new CaptureSnippetExecutor(new JintSnippetRunner());
        var snippets = new[]
        {
            new CaptureSnippetDefinition(new JsSnippetDefinition("markup", "Markup", "cf.submit('markup', cf.html);"), CaptureSource.Rendered),
        };

        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(Context("Raw title", "Rendered title"), snippets);

        CaptureSnippetOutcome rendered = Assert.Single(outcomes);
        Assert.Equal(CaptureSource.Rendered, rendered.Source);
        string markup = RowString(Assert.Single(rendered.Results), "markup");
        Assert.Contains("Rendered title", markup);
        Assert.DoesNotContain("Raw title", markup);
    }

    [Fact]
    public async Task SnippetTimeout_IsIsolated_OtherSnippetsStillRun()
    {
        var options = new JsSnippetOptions { DefaultTimeout = TimeSpan.FromMilliseconds(100) };
        var executor = new CaptureSnippetExecutor(new JintSnippetRunner(options));
        var snippets = new[]
        {
            new CaptureSnippetDefinition(new JsSnippetDefinition("loop", "Loop", "while (true) { }"), CaptureSource.Rendered),
            new CaptureSnippetDefinition(new JsSnippetDefinition("ok", "Ok", "cf.submit('v', 'ok');"), CaptureSource.Rendered),
        };

        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(Context("T", "T"), snippets);

        IReadOnlyList<SnippetResultDraft> results = Assert.Single(outcomes).Results;
        SnippetResultDraft loop = Assert.Single(results, r => r.SnippetKey == "loop");
        SnippetResultDraft ok = Assert.Single(results, r => r.SnippetKey == "ok");
        Assert.Equal("[]", loop.RowsJson); // aborted + isolated
        Assert.Equal("ok", RowString(ok, "v")); // unaffected
    }

    [Fact]
    public async Task Wrapper_HasNoBackendSurface()
    {
        var executor = new CaptureSnippetExecutor(new JintSnippetRunner());
        var snippets = new[]
        {
            // No CLR interop, no require, no network: the attempt errors and is
            // isolated; the audit never fails.
            new CaptureSnippetDefinition(new JsSnippetDefinition("escape", "Escape", "cf.submit('x', require('fs').readFileSync('/etc/passwd'));"), CaptureSource.Rendered),
        };

        IReadOnlyList<CaptureSnippetOutcome> outcomes = await executor.RunAsync(Context("T", "T"), snippets);

        SnippetResultDraft result = Assert.Single(Assert.Single(outcomes).Results);
        Assert.Equal("[]", result.RowsJson);
    }
}
