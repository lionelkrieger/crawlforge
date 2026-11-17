using System.Text.Json;
using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Tests.Custom.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// Custom content extraction (FS-CRAWL-001 §7.9, criterion #24): typed
/// captures, mandatory bounds, invalid/truncated flags, isolation, offline
/// determinism.
/// </summary>
public sealed class CustomContentExtractorTests
{
    [Fact]
    public async Task CssSelector_CapturesElementText()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Products", new[] { CustomDomBuilder.Div("price", CustomDomBuilder.Text("span", "19.99")) }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("price", ".price span", ExtractedValueType.Number) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        ExtractedValueDraft value = Assert.Single(values);
        Assert.Equal("price", value.RuleName);
        Assert.Equal(0, value.Ordinal);
        Assert.False(value.IsInvalid);
        Assert.Equal(19.99m, JsonSerializer.Deserialize<decimal>(value.ValueJson!));
    }

    [Fact]
    public async Task AllowMultiple_CapturesOneValuePerElement_WithOrdinals()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("List", new[]
            {
                CustomDomBuilder.Text("li", "first"),
                CustomDomBuilder.Text("li", "second"),
                CustomDomBuilder.Text("li", "third"),
            }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("items", "li", ExtractedValueType.Text, AllowMultiple: true) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal(3, values.Count);
        Assert.Equal(new[] { 0, 1, 2 }, values.Select(v => v.Ordinal));
        Assert.Equal("\"second\"", values[1].ValueJson);
    }

    [Fact]
    public async Task SingleValueRule_CapsAtOne()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("List", new[] { CustomDomBuilder.Text("li", "first"), CustomDomBuilder.Text("li", "second") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("items", "li", ExtractedValueType.Text) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Single(values);
        Assert.Equal("\"first\"", values[0].ValueJson);
    }

    [Fact]
    public async Task AttributeCapture_SuffixCapturesNamedAttribute()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Links", new[] { CustomDomBuilder.Anchor("/target", "link text") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("href", "a @href", ExtractedValueType.Text) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal("\"/target\"", Assert.Single(values).ValueJson);
    }

    [Fact]
    public async Task InnerMarkupCapture_SuffixCapturesMarkup()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Markup", new[] { CustomDomBuilder.Div("box", CustomDomBuilder.Text("b", "bold")) }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("markup", ".box ::markup", ExtractedValueType.Text) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        string decoded = JsonSerializer.Deserialize<string>(Assert.Single(values).ValueJson!)!;
        Assert.Contains("<b>bold</b>", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumberCoercionFailure_RecordsInvalidMarker()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Bad number", new[] { CustomDomBuilder.Text("span", "not-a-number") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("count", "span", ExtractedValueType.Number) };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        ExtractedValueDraft value = Assert.Single(result.Values);
        Assert.True(value.IsInvalid);
        Assert.Null(value.ValueJson);
        Assert.Contains(result.Diagnostics, d => d.ItemKey == "count" && d.Kind == CustomDiagnosticKinds.Invalid);
    }

    [Fact]
    public async Task BooleanAndDateTimeCoercion()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Typed", new[]
            {
                CustomDomBuilder.Element("span", new Dictionary<string, string> { ["id"] = "flag" }, CustomDomBuilder.Text("ignored", "true")),
                CustomDomBuilder.Text("time", "2025-08-25T15:00:00Z"),
            }));
        var extractor = new CustomContentExtractor();
        var rules = new[]
        {
            new CustomExtractionRule("flag", "#flag", ExtractedValueType.Boolean),
            new CustomExtractionRule("when", "time", ExtractedValueType.DateTime),
        };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal("true", values[0].ValueJson);
        Assert.False(values[1].IsInvalid);
        Assert.Contains("2025-08-25", values[1].ValueJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncation_IsSurrogateSafe_AndFlagged()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Emoji", new[] { CustomDomBuilder.Text("p", "ab\uD83D\uDE00cd") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("text", "p", ExtractedValueType.Text, MaxLength: 3) };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        ExtractedValueDraft value = Assert.Single(result.Values);
        Assert.True(value.IsTruncated);
        // The surrogate pair must not be split: "ab" survives, not "ab\uDE00".
        Assert.Equal("\"ab\"", value.ValueJson);
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.Truncated);
    }

    [Fact]
    public async Task PerPageCap_DiscardsExcess_AndCounts()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Many", Enumerable.Range(0, 5).Select(i => CustomDomBuilder.Text("li", "v" + i)).ToArray()));
        var extractor = new CustomContentExtractor(new CustomExtractionOptions { MaxValuesPerPage = 2 });
        var rules = new[] { new CustomExtractionRule("items", "li", ExtractedValueType.Text, AllowMultiple: true) };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal(2, result.Values.Count);
        CustomDiagnostic discarded = Assert.Single(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.Discarded);
        Assert.Equal(3, discarded.Count);
    }

    [Fact]
    public async Task RequiredRule_WithNoMatch_RecordsInvalidDraft()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Empty", Array.Empty<DomNode>()));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("must", ".missing", ExtractedValueType.Text, Required: true) };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        ExtractedValueDraft value = Assert.Single(result.Values);
        Assert.True(value.IsInvalid);
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.Missing);
    }

    [Fact]
    public async Task XPathPrefix_CapturesElements()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("XPath", new[] { CustomDomBuilder.Div("sku", CustomDomBuilder.Text("span", "ABC-123")) }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("sku", "xpath://div[@class='sku']/span", ExtractedValueType.Text) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal("\"ABC-123\"", Assert.Single(values).ValueJson);
    }

    [Fact]
    public async Task RegexPrefix_CapturesFirstGroup()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Regex", new[] { CustomDomBuilder.Text("p", "Order 12345 confirmed") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("order", "regex:Order (\\d+)", ExtractedValueType.Number) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Equal("12345", Assert.Single(values).ValueJson);
    }

    [Fact]
    public async Task PathScope_ExcludesNonMatchingPage()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Scoped", new[] { CustomDomBuilder.Text("p", "value") }));
        var extractor = new CustomContentExtractor(new CustomExtractionOptions
        {
            PathScopes = new Dictionary<string, PathScope>
            {
                ["scoped"] = new PathScope(new[] { "/blog" }, Array.Empty<string>()),
            },
        });
        var rules = new[] { new CustomExtractionRule("scoped", "p", ExtractedValueType.Text) };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Empty(result.Values);
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.OutOfScope);
    }

    [Fact]
    public async Task CatastrophicPattern_TimesOut_Isolated()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Evil", new[] { CustomDomBuilder.Text("p", new string('a', 40) + "!") }));
        var extractor = new CustomContentExtractor(new CustomExtractionOptions { RuleTimeout = TimeSpan.FromMilliseconds(200) });
        var rules = new[]
        {
            new CustomExtractionRule("evil", "regex:(a+)+$", ExtractedValueType.Text),
            new CustomExtractionRule("ok", "p", ExtractedValueType.Text),
        };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "evil" && d.Kind == CustomDiagnosticKinds.Timeout);
        Assert.Contains(result.Values, v => v.RuleName == "ok");
    }

    [Fact]
    public async Task InvalidSelector_Isolated_OtherRulesStillRun()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Broken", new[] { CustomDomBuilder.Text("p", "survivor") }));
        var extractor = new CustomContentExtractor();
        var rules = new[]
        {
            new CustomExtractionRule("broken", "[[[invalid", ExtractedValueType.Text),
            new CustomExtractionRule("ok", "p", ExtractedValueType.Text),
        };

        CustomExtractionResult result = await extractor.ExtractWithDiagnosticsAsync(CustomContextFactory.Context(document: document), rules);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "broken" && d.Kind == CustomDiagnosticKinds.Error);
        Assert.Contains(result.Values, v => v.RuleName == "ok");
    }

    [Fact]
    public async Task RenderingMode_UsesRenderedDocument()
    {
        DomDocument raw = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Raw", new[] { CustomDomBuilder.Text("p", "raw-value") }));
        DomDocument rendered = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Rendered", new[] { CustomDomBuilder.Text("p", "rendered-value") }));
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("text", "p", ExtractedValueType.Text) };

        IReadOnlyList<ExtractedValueDraft> values = await extractor.ExtractAsync(
            CustomContextFactory.Context(document: raw, renderedDocument: rendered, renderingMode: RenderMode.Always), rules);

        Assert.Equal("\"rendered-value\"", Assert.Single(values).ValueJson);
    }

    [Fact]
    public async Task Rerun_FromSameStoredContext_IsDeterministic()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Determinism", new[] { CustomDomBuilder.Text("li", "a"), CustomDomBuilder.Text("li", "b") }));
        PageAuditContext context = CustomContextFactory.Context(document: document);
        var extractor = new CustomContentExtractor();
        var rules = new[] { new CustomExtractionRule("items", "li", ExtractedValueType.Text, AllowMultiple: true) };

        CustomExtractionResult first = await extractor.ExtractWithDiagnosticsAsync(context, rules);
        CustomExtractionResult second = await extractor.ExtractWithDiagnosticsAsync(context, rules);

        Assert.Equal(first.Values.Count, second.Values.Count);
        for (int i = 0; i < first.Values.Count; i++)
        {
            Assert.Equal(first.Values[i], second.Values[i]);
        }
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var extractor = new CustomContentExtractor();
        await Assert.ThrowsAsync<ArgumentNullException>(() => extractor.ExtractAsync(null!, Array.Empty<CustomExtractionRule>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => extractor.ExtractAsync(CustomContextFactory.Context(), null!));
    }
}