using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Configuration-time selector validation, the bundled boilerplate preset
/// (FS-CRAWL-002 §4.2), and the stateless scope preview (§4.6).
/// </summary>
public class ContentScopeValidatorTests
{
    [Fact]
    public void ValidCssAndXPathSelectorsPass()
    {
        var scope = new ContentScopeDefinition(
            ["main", "#content", "//article[@id='post']"],
            ["nav", ".cookie-banner", "//footer"]);
        ContentScopeValidator.Validate(scope);
        Assert.True(ContentScopeValidator.TryValidate(scope, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void InvalidCssSelectorIsRejectedWithDescriptiveError()
    {
        var scope = new ContentScopeDefinition(["main >> invalid >>>"], []);
        var ex = Assert.Throws<ContentScopeValidationException>(() => ContentScopeValidator.Validate(scope));
        Assert.Contains(ex.EntryErrors, e => e.Contains("include[0]", StringComparison.Ordinal));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidXPathSelectorIsRejectedWithDescriptiveError()
    {
        var scope = new ContentScopeDefinition([], ["//div[@id="]);
        Assert.False(ContentScopeValidator.TryValidate(scope, out var errors));
        Assert.Contains(errors, e => e.Contains("exclude[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyExpressionIsRejected()
    {
        var scope = new ContentScopeDefinition(["  "], []);
        Assert.False(ContentScopeValidator.TryValidate(scope, out var errors));
        Assert.Contains(errors, e => e.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public void NullScopeIsValidBaseline()
    {
        ContentScopeValidator.Validate(null);
        Assert.True(ContentScopeValidator.TryValidate(null, out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void BoilerplatePresetIsValidAndStripsCommonBoilerplate()
    {
        var preset = ContentScopeValidator.BoilerplatePreset;
        ContentScopeValidator.Validate(preset);
        Assert.Empty(preset.IncludeSelectors);
        Assert.Contains("nav", preset.ExcludeSelectors);
        Assert.Contains("[role=navigation]", preset.ExcludeSelectors);
        Assert.Contains(".cookie-banner", preset.ExcludeSelectors);

        var parser = new AngleSharpDomParser();
        var resolver = new ContentScopeResolver();
        var document = parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var resolution = resolver.ResolveDetailed(document, preset);

        Assert.DoesNotContain("Nav one", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Footer boilerplate", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.Contains("Alpha beta gamma", resolution.VisibleText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsStrippedWithoutAnExplicitScope()
    {
        // Baseline-preserving defaults: the preset exists but is never applied
        // automatically (precedence rule 4).
        var parser = new AngleSharpDomParser();
        var resolver = new ContentScopeResolver();
        var document = parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");
        var baseline = resolver.Resolve(document, null);
        Assert.Contains("Nav one", baseline.Text, StringComparison.Ordinal);
        Assert.Contains("Footer boilerplate", baseline.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewReportsCountsSampleTextAndPerSelectorMatches()
    {
        var parser = new AngleSharpDomParser();
        var resolver = new ContentScopeResolver();
        var preview = new ScopePreviewService(resolver);
        var document = parser.Parse(PipelineTestContext.SampleHtml, "https://example.test/page");

        var result = preview.Preview(document, new ContentScopeDefinition(["main"], ["nav"]));

        Assert.True(result.FullWordCount > result.ScopedWordCount);
        Assert.Equal(result.FullWordCount - result.ScopedWordCount, result.RemovedWordCount);
        Assert.Contains("Alpha beta gamma", result.SampleScopedText, StringComparison.Ordinal);
        Assert.True(result.SampleScopedText.Length <= 500);
        Assert.Equal(2, result.PerSelectorCounts.Count);
        Assert.Single(result.Regions);
        Assert.Equal("main", result.Regions[0].Selector);
        Assert.True(result.Regions[0].WordCount > 0);
    }

    [Fact]
    public void SelectorLanguageDetectionIsDeterministic()
    {
        Assert.Equal(SelectorLanguage.XPath, SelectorLanguageDetector.Detect("//div"));
        Assert.Equal(SelectorLanguage.XPath, SelectorLanguageDetector.Detect("(//div)[1]"));
        Assert.Equal(SelectorLanguage.Css, SelectorLanguageDetector.Detect("div.content"));
        Assert.Equal(SelectorLanguage.Css, SelectorLanguageDetector.Detect("#main"));
        Assert.Equal(SelectorLanguage.Css, SelectorLanguageDetector.Detect("  main > p"));
    }
}
