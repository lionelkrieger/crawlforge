using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Custom content extraction (FS-CRAWL-001 §7.9, v1.3): user-defined rules
/// capture typed values with mandatory bounds; invalid/truncated values are
/// flagged, never silently dropped.
/// </summary>
public interface ICustomContentExtractor
{
    /// <summary>Extracts custom values from a page according to rules.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="rules">User-defined extraction rules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Extracted values with validity flags.</returns>
    Task<IReadOnlyList<ExtractedValueDraft>> ExtractAsync(
        PageAuditContext context,
        IReadOnlyList<CustomExtractionRule> rules,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One user-defined extraction rule.
/// </summary>
/// <param name="Name">Rule name (dynamic column key).</param>
/// <param name="Selector">CSS selector capturing the value.</param>
/// <param name="ValueType">Expected typed value kind.</param>
/// <param name="AllowMultiple">Whether multiple values per page are captured.</param>
/// <param name="MaxLength">Optional maximum value length (truncation bound).</param>
/// <param name="Required">Whether a missing value is flagged invalid.</param>
public sealed record CustomExtractionRule(
    string Name,
    string Selector,
    ExtractedValueType ValueType,
    bool AllowMultiple = false,
    int? MaxLength = null,
    bool Required = false);

/// <summary>
/// One extracted value before persistence (schema catalog: extracted_content).
/// </summary>
/// <param name="RuleName">Capturing rule name.</param>
/// <param name="Ordinal">Zero-based value ordinal within the rule.</param>
/// <param name="ValueJson">Typed value payload; null when invalid/missing.</param>
/// <param name="ValueType">Typed value kind.</param>
/// <param name="IsInvalid">Whether the value failed type/bounds validation.</param>
/// <param name="IsTruncated">Whether the value was truncated at a bound.</param>
public sealed record ExtractedValueDraft(
    string RuleName,
    int Ordinal,
    string? ValueJson,
    ExtractedValueType ValueType,
    bool IsInvalid,
    bool IsTruncated);

/// <summary>
/// Custom content search (FS-CRAWL-001 §7.16): user-defined search items
/// matched against scoped visible text, with per-item aggregates.
/// </summary>
public interface ICustomContentSearch
{
    /// <summary>Searches a page for user-defined items.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="items">User-defined search items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-item matches and snippets.</returns>
    Task<IReadOnlyList<ContentSearchMatch>> SearchAsync(
        PageAuditContext context,
        IReadOnlyList<CustomSearchItem> items,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One user-defined search item.
/// </summary>
/// <param name="Key">Stable item key.</param>
/// <param name="Pattern">Search pattern.</param>
/// <param name="IsRegex">Whether the pattern is a regular expression.</param>
public sealed record CustomSearchItem(string Key, string Pattern, bool IsRegex = false);

/// <summary>
/// Search matches of one item on one page (schema catalog:
/// content_search_results).
/// </summary>
/// <param name="ItemKey">Search item key.</param>
/// <param name="MatchCount">Number of matches on the page.</param>
/// <param name="Snippets">Bounded matched snippets.</param>
public sealed record ContentSearchMatch(string ItemKey, int MatchCount, IReadOnlyList<string> Snippets);

/// <summary>
/// Custom JavaScript snippet runner (FS-CRAWL-001 §7.17): runs user-authored
/// snippets in the rendered page with bounded rows and typed result columns.
/// Snippets are local user artifacts (exclusion E1).
/// </summary>
public interface IJsSnippetRunner
{
    /// <summary>Runs snippets against a rendered page.</summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="snippets">User-defined snippets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Snippet outputs with bounded rows.</returns>
    Task<IReadOnlyList<SnippetResultDraft>> RunAsync(
        PageAuditContext context,
        IReadOnlyList<JsSnippetDefinition> snippets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One user-defined JavaScript snippet.
/// </summary>
/// <param name="Key">Stable snippet key.</param>
/// <param name="Name">Display name.</param>
/// <param name="JavaScript">Snippet source.</param>
/// <param name="MaxRows">Row bound for the snippet output.</param>
public sealed record JsSnippetDefinition(string Key, string Name, string JavaScript, int MaxRows = 100);

/// <summary>
/// One snippet output before persistence (schema catalog: snippet_results).
/// </summary>
/// <param name="SnippetKey">Snippet key.</param>
/// <param name="ColumnsJson">Typed column definitions (dynamic columns).</param>
/// <param name="RowsJson">Bounded result rows.</param>
/// <param name="IsTruncated">Whether rows were truncated at the bound.</param>
public sealed record SnippetResultDraft(string SnippetKey, string ColumnsJson, string RowsJson, bool IsTruncated);
