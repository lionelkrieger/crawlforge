using CrawlForge.Audit.Pipeline;
using CrawlForge.Audit.Scoping;
using CrawlForge.Contracts.Audit;
using Xunit;

namespace CrawlForge.Audit.Tests.Pipeline;

/// <summary>
/// Scoped Visible Text Tree semantics (FS-CRAWL-002 §4.2/§4.3) and the
/// baseline-preserving default (criterion #37).
/// </summary>
public class ScopeResolverTests
{
    private readonly AngleSharpDomParser _parser = new();
    private readonly ContentScopeResolver _resolver = new();

    private DomDocument Parse(string html) => _parser.Parse(html, "https://example.test/page");

    [Fact]
    public void UnconfiguredScopeReproducesTheBaselineExactly()
    {
        // Criterion #37: with no scope configured the visible text is the full
        // visible text — byte-for-byte the baseline.
        var document = Parse(PipelineTestContext.SampleHtml);
        var baseline = _resolver.Resolve(document, null);
        var emptyScope = _resolver.Resolve(document, new ContentScopeDefinition([], []));

        Assert.Equal(baseline.Text, emptyScope.Text);
        Assert.Equal(baseline.WordCount, emptyScope.WordCount);
        Assert.Contains("Alpha beta gamma", baseline.Text, StringComparison.Ordinal);
        Assert.Contains("Nav one", baseline.Text, StringComparison.Ordinal);
        Assert.Contains("Footer boilerplate", baseline.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("script text", baseline.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sample Page", baseline.Text, StringComparison.Ordinal); // title is head, not visible body text
    }

    [Fact]
    public void ExcludeStripsBoilerplateSubtrees()
    {
        var document = Parse(PipelineTestContext.SampleHtml);
        var scope = new ContentScopeDefinition([], ["nav", "footer"]);
        var resolution = _resolver.ResolveDetailed(document, scope);

        Assert.DoesNotContain("Nav one", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Footer boilerplate", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.Contains("Alpha beta gamma", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.Equal(2, resolution.ExcludeMatchCount);
        Assert.True(resolution.RemovedWordCount > 0);
        Assert.Equal(resolution.FullWordCount - resolution.ScopedWordCount, resolution.RemovedWordCount);
    }

    [Fact]
    public void IncludeUnionKeepsDocumentOrderAndDeduplicatesNestedMatches()
    {
        var html = """
            <html><body>
              <div id="a"><p>First region words.</p><div id="nested">Nested region words.</div></div>
              <div id="b">Second region words.</div>
              <p>Outside any region.</p>
            </body></html>
            """;
        var document = Parse(html);
        var scope = new ContentScopeDefinition(["#a", "#nested", "#b"], []);
        var resolution = _resolver.ResolveDetailed(document, scope);

        // #nested is subsumed by #a: its words appear exactly once.
        var text = resolution.VisibleText.Text;
        Assert.Contains("First region words.", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(text, "Nested region words."));
        Assert.Contains("Second region words.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Outside any region.", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("First region", StringComparison.Ordinal) < text.IndexOf("Second region", StringComparison.Ordinal));
        Assert.Equal(3, resolution.IncludeMatchCount);
    }

    [Fact]
    public void ExcludeWinsOverInclude()
    {
        var html = """
            <html><body>
              <main id="content"><p>Keep this text.</p><aside class="promo">Drop this promo.</aside></main>
            </body></html>
            """;
        var document = Parse(html);
        var scope = new ContentScopeDefinition(["#content"], [".promo"]);
        var resolution = _resolver.ResolveDetailed(document, scope);

        Assert.Contains("Keep this text.", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Drop this promo.", resolution.VisibleText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedCssAndXPathSelectorsWorkInOneScope()
    {
        var document = Parse(PipelineTestContext.SampleHtml);
        var scope = new ContentScopeDefinition(["//main[@id='content']", "footer"], []);
        var resolution = _resolver.ResolveDetailed(document, scope);

        Assert.Contains("Alpha beta gamma", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.Contains("Footer boilerplate", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nav one", resolution.VisibleText.Text, StringComparison.Ordinal);
        Assert.Equal(2, resolution.IncludeMatchCount);
        Assert.Contains(resolution.PerSelectorCounts, c => c.Language == SelectorLanguage.XPath);
        Assert.Contains(resolution.PerSelectorCounts, c => c.Language == SelectorLanguage.Css);
    }

    [Fact]
    public void HiddenElementsAreExcludedFromVisibleText()
    {
        var html = """
            <html><body>
              <p>Visible words.</p>
              <p hidden>Hidden by attribute.</p>
              <p aria-hidden="true">Hidden by aria.</p>
              <p style="display:none">Hidden by style.</p>
              <p style="visibility: hidden">Hidden by visibility.</p>
              <style>.css { color: red; }</style>
              <noscript>No script fallback.</noscript>
            </body></html>
            """;
        var document = Parse(html);
        var text = _resolver.Resolve(document, null).Text;

        Assert.Contains("Visible words.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden by attribute.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden by aria.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden by style.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden by visibility.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("No script fallback.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyIncludeMatchesYieldEmptyScopedText()
    {
        var document = Parse(PipelineTestContext.SampleHtml);
        var scope = new ContentScopeDefinition(["#does-not-exist"], []);
        var resolution = _resolver.ResolveDetailed(document, scope);

        Assert.Equal(string.Empty, resolution.VisibleText.Text);
        Assert.Equal(0, resolution.ScopedWordCount);
        Assert.Equal(resolution.FullWordCount, resolution.RemovedWordCount);
    }

    [Fact]
    public void DiagnosticsCarryTheContentScopeCounts()
    {
        var document = Parse(PipelineTestContext.SampleHtml);
        var scope = new ContentScopeDefinition(["main"], ["nav"]);
        var resolution = _resolver.ResolveDetailed(document, scope);

        Assert.Contains(resolution.VisibleText.Diagnostics, d => d.StartsWith("include-matches:", StringComparison.Ordinal));
        Assert.Contains(resolution.VisibleText.Diagnostics, d => d.StartsWith("exclude-matches:", StringComparison.Ordinal));
        Assert.Contains(resolution.VisibleText.Diagnostics, d => d.StartsWith("full-word-count:", StringComparison.Ordinal));
        Assert.Contains(resolution.VisibleText.Diagnostics, d => d.StartsWith("scoped-word-count:", StringComparison.Ordinal));
        Assert.Contains(resolution.VisibleText.Diagnostics, d => d.StartsWith("removed-word-count:", StringComparison.Ordinal));
        Assert.Equal(2, resolution.PerSelectorCounts.Count);
    }

    [Fact]
    public void MalformedMarkupStillResolvesWithoutThrowing()
    {
        var document = Parse("<html><body><p>Unclosed <b>bold text<div>Another block</body>");
        var resolution = _resolver.ResolveDetailed(document, new ContentScopeDefinition(["p"], []));
        Assert.Contains("Unclosed", resolution.VisibleText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WordCountMatchesWhitespaceTokenization()
    {
        var html = "<html><body><p>one   two\tthree\nfour</p></body></html>";
        var document = Parse(html);
        var scoped = _resolver.Resolve(document, null);
        Assert.Equal(4, scoped.WordCount);
        Assert.Equal("one two three four", scoped.Text);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
