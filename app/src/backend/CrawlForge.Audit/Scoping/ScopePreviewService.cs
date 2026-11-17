using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Scoping;

/// <summary>One matched region in a scope preview.</summary>
/// <param name="Selector">Selector that matched the region.</param>
/// <param name="Language">Selector language.</param>
/// <param name="TagName">Root tag of the matched region.</param>
/// <param name="TextSample">Bounded visible-text sample of the region.</param>
/// <param name="WordCount">Visible word count of the region.</param>
public sealed record ScopePreviewRegion(
    string Selector,
    SelectorLanguage Language,
    string TagName,
    string TextSample,
    int WordCount);

/// <summary>
/// Scope-preview result (FS-CRAWL-002 §4.6, POST /api/tools/scope-preview):
/// matched-region summary, word counts before/after, sample scoped text, and
/// per-selector match counts.
/// </summary>
/// <param name="Regions">Matched include regions (document order).</param>
/// <param name="FullWordCount">Visible word count before scoping.</param>
/// <param name="ScopedWordCount">Visible word count after scoping.</param>
/// <param name="RemovedWordCount">Words removed by scoping.</param>
/// <param name="SampleScopedText">Bounded sample of the scoped text.</param>
/// <param name="PerSelectorCounts">Per-entry match counts.</param>
/// <param name="Diagnostics">Resolver diagnostics.</param>
public sealed record ScopePreviewResult(
    IReadOnlyList<ScopePreviewRegion> Regions,
    int FullWordCount,
    int ScopedWordCount,
    int RemovedWordCount,
    string SampleScopedText,
    IReadOnlyList<SelectorMatchCount> PerSelectorCounts,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Stateless live scope preview against a sample document (FS-CRAWL-002 §4.2/§4.6):
/// the same resolver the pipeline uses, so preview and audit agree exactly.
/// The REST tool endpoint and the single-page analyzer embed this service.
/// </summary>
public sealed class ScopePreviewService
{
    private const int SampleLength = 500;

    private readonly ContentScopeResolver _resolver;

    /// <summary>Creates the preview service over a scope resolver.</summary>
    /// <param name="resolver">Resolver shared with the pipeline.</param>
    public ScopePreviewService(ContentScopeResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>Previews a scope against a sample document.</summary>
    /// <param name="document">Sample document (stored artifact or fetched sample).</param>
    /// <param name="scope">Scope to preview; null previews the baseline.</param>
    /// <returns>Matched regions, counts, sample text, and diagnostics.</returns>
    public ScopePreviewResult Preview(DomDocument document, ContentScopeDefinition? scope)
    {
        var resolution = _resolver.ResolveDetailed(document, scope);
        var regions = new List<ScopePreviewRegion>();

        if (scope is not null)
        {
            foreach (var expression in scope.IncludeSelectors)
            {
                var language = SelectorLanguageDetector.Detect(expression);
                var single = new ContentScopeDefinition([expression], []);
                var regionResolution = _resolver.ResolveDetailed(document, single);
                if (regionResolution.ScopedWordCount > 0 || regionResolution.IncludeMatchCount > 0)
                {
                    regions.Add(new ScopePreviewRegion(
                        expression,
                        language,
                        "region",
                        Truncate(regionResolution.VisibleText.Text),
                        regionResolution.ScopedWordCount));
                }
            }
        }

        return new ScopePreviewResult(
            regions,
            resolution.FullWordCount,
            resolution.ScopedWordCount,
            resolution.RemovedWordCount,
            Truncate(resolution.VisibleText.Text),
            resolution.PerSelectorCounts,
            resolution.VisibleText.Diagnostics);
    }

    private static string Truncate(string text) =>
        text.Length <= SampleLength ? text : text[..SampleLength];
}
