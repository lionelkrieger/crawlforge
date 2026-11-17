using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Tests.Custom.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// JavaScript snippet execution (FS-CRAWL-001 §7.17, criterion #31): isolated
/// sandbox, typed dynamic columns, bounded rows, timeouts, per-snippet
/// failure isolation, offline determinism.
/// </summary>
public sealed class JintSnippetRunnerTests
{
    [Fact]
    public async Task Submit_ProducesTypedColumnsAndRows()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("s1", "Typed", "cf.submit('name', 'widget'); cf.submit('count', 42); cf.submit('active', true);"),
        };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(RenderedContext(), snippets);

        SnippetResultDraft result = Assert.Single(results);
        using JsonDocument columns = JsonDocument.Parse(result.ColumnsJson);
        Assert.Equal(3, columns.RootElement.GetArrayLength());
        Assert.Equal("number", columns.RootElement[1].GetProperty("valueType").GetString());
        Assert.Equal("boolean", columns.RootElement[2].GetProperty("valueType").GetString());
        using JsonDocument rows = JsonDocument.Parse(result.RowsJson);
        JsonElement row = rows.RootElement[0];
        Assert.Equal("widget", row.GetProperty("name").GetString());
        Assert.Equal(42, row.GetProperty("count").GetInt32());
        Assert.True(row.GetProperty("active").GetBoolean());
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task ReturnValue_CapturedAsResultColumn()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[] { new JsSnippetDefinition("ret", "Return", "6 * 7") };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(RenderedContext(), snippets);

        using JsonDocument rows = JsonDocument.Parse(Assert.Single(results).RowsJson);
        Assert.Equal(42, rows.RootElement[0].GetProperty("result").GetInt32());
    }

    [Fact]
    public async Task MultipleSubmits_AlignRowsByIndex()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("multi", "Multi", "cf.submit('a', 1); cf.submit('a', 2); cf.submit('b', 'x');"),
        };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(RenderedContext(), snippets);

        using JsonDocument rows = JsonDocument.Parse(Assert.Single(results).RowsJson);
        Assert.Equal(2, rows.RootElement.GetArrayLength());
        Assert.Equal(2, rows.RootElement[1].GetProperty("a").GetInt32());
        Assert.Equal(JsonValueKind.Null, rows.RootElement[1].GetProperty("b").ValueKind);
    }

    [Fact]
    public async Task ReportedError_IsolatedAsDiagnostic()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[] { new JsSnippetDefinition("err", "Error", "cf.error('something went wrong');") };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "err" && d.Kind == CustomDiagnosticKinds.Error);
        Assert.Single(result.Results);
    }

    [Fact]
    public async Task InfiniteLoop_TimesOut_Isolated()
    {
        var runner = new JintSnippetRunner(new JsSnippetOptions { DefaultTimeout = TimeSpan.FromMilliseconds(200) });
        var snippets = new[]
        {
            new JsSnippetDefinition("loop", "Loop", "while (true) { }"),
            new JsSnippetDefinition("ok", "Ok", "cf.submit('v', 1);"),
        };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "loop" && d.Kind == CustomDiagnosticKinds.Timeout);
        Assert.Contains(result.Results, r => r.SnippetKey == "ok" && r.RowsJson.Contains('1'));
    }

    [Fact]
    public async Task MaxRows_TruncatesAndFlags()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("rows", "Rows", "for (var i = 0; i < 5; i++) { cf.submit('n', i); }", MaxRows: 2),
        };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        SnippetResultDraft draft = Assert.Single(result.Results);
        Assert.True(draft.IsTruncated);
        using JsonDocument rows = JsonDocument.Parse(draft.RowsJson);
        Assert.Equal(2, rows.RootElement.GetArrayLength());
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.Discarded && d.Count == 3);
    }

    [Fact]
    public async Task ThrownSnippetError_Isolated_OtherSnippetsRun()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("bad", "Bad", "throw new Error('boom');"),
            new JsSnippetDefinition("good", "Good", "cf.submit('v', 'ok');"),
        };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "bad" && d.Kind == CustomDiagnosticKinds.Error);
        Assert.Contains(result.Results, r => r.SnippetKey == "good" && r.RowsJson.Contains("ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sandbox_HasNoClrOrNetworkSurface()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("sandbox", "Sandbox",
                "cf.submit('noClr', typeof System === 'undefined'); cf.submit('noRequire', typeof require === 'undefined'); cf.submit('noFetch', typeof fetch === 'undefined');"),
        };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(RenderedContext(), snippets);

        using JsonDocument rows = JsonDocument.Parse(Assert.Single(results).RowsJson);
        Assert.True(rows.RootElement[0].GetProperty("noClr").GetBoolean());
        Assert.True(rows.RootElement[0].GetProperty("noRequire").GetBoolean());
        Assert.True(rows.RootElement[0].GetProperty("noFetch").GetBoolean());
    }

    [Fact]
    public async Task HostApi_ExposesReadOnlyPageData()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[]
        {
            new JsSnippetDefinition("page", "Page", "cf.submit('url', cf.url); cf.submit('text', cf.text); cf.submit('hasHtml', cf.html.length > 0);"),
        };

        IReadOnlyList<SnippetResultDraft> results = await runner.RunAsync(RenderedContext(), snippets);

        using JsonDocument rows = JsonDocument.Parse(Assert.Single(results).RowsJson);
        Assert.Equal(CustomContextFactory.PageUrl, rows.RootElement[0].GetProperty("url").GetString());
        Assert.Equal("rendered content", rows.RootElement[0].GetProperty("text").GetString());
        Assert.True(rows.RootElement[0].GetProperty("hasHtml").GetBoolean());
    }

    [Fact]
    public async Task RenderingModeRequired_NoneModeSkips()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[] { new JsSnippetDefinition("s", "S", "cf.submit('v', 1);") };
        PageAuditContext context = CustomContextFactory.Context(
            document: CustomDomBuilder.Document(CustomContextFactory.PageUrl, CustomDomBuilder.Page("T", null)),
            renderingMode: RenderMode.None);

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(context, snippets);

        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.OutOfScope);
        Assert.Equal("[]", Assert.Single(result.Results).RowsJson);
    }

    [Fact]
    public async Task LocalScriptLibrary_LoadsByName_UnknownReportsError()
    {
        var runner = new JintSnippetRunner(new JsSnippetOptions
        {
            LocalScriptLibrary = new Dictionary<string, string> { ["helpers"] = "function double(x) { return x * 2; }" },
        });
        var snippets = new[]
        {
            new JsSnippetDefinition("lib", "Lib", "cf.loadScript('helpers'); cf.submit('v', double(21));"),
            new JsSnippetDefinition("missing", "Missing", "cf.loadScript('not-in-library');"),
        };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        using JsonDocument rows = JsonDocument.Parse(result.Results[0].RowsJson);
        Assert.Equal(42, rows.RootElement[0].GetProperty("v").GetInt32());
        Assert.Contains(result.Diagnostics, d => d.ItemKey == "missing" && d.Kind == CustomDiagnosticKinds.Error);
    }

    [Fact]
    public async Task PathScope_ExcludesPage()
    {
        var runner = new JintSnippetRunner(new JsSnippetOptions
        {
            PathScopes = new Dictionary<string, PathScope> { ["scoped"] = new PathScope(new[] { "/blog" }, Array.Empty<string>()) },
        });
        var snippets = new[] { new JsSnippetDefinition("scoped", "Scoped", "cf.submit('v', 1);") };

        JsSnippetRunResult result = await runner.RunWithDiagnosticsAsync(RenderedContext(), snippets);

        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.OutOfScope);
    }

    [Fact]
    public async Task Rerun_IsDeterministic()
    {
        var runner = new JintSnippetRunner();
        var snippets = new[] { new JsSnippetDefinition("det", "Det", "cf.submit('a', 1); cf.submit('b', 'two');") };
        PageAuditContext context = RenderedContext();

        JsSnippetRunResult first = await runner.RunWithDiagnosticsAsync(context, snippets);
        JsSnippetRunResult second = await runner.RunWithDiagnosticsAsync(context, snippets);

        Assert.Equal(first.Results.Count, second.Results.Count);
        Assert.Equal(first.Results[0], second.Results[0]);
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var runner = new JintSnippetRunner();
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(null!, Array.Empty<JsSnippetDefinition>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.RunAsync(CustomContextFactory.Context(), null!));
    }

    private static PageAuditContext RenderedContext()
    {
        DomDocument rendered = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Rendered", new[] { CustomDomBuilder.Text("p", "rendered content") }));
        return CustomContextFactory.Context(renderedDocument: rendered, renderingMode: RenderMode.Always);
    }
}