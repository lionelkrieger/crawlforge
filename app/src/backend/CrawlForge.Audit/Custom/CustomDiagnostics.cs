using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Diagnostic kinds surfaced by the custom surfaces (FS-CRAWL-001 §7.9:
/// errors, discards, and truncations are logged locally and surfaced as
/// rule-level diagnostics; they never fail the audit).
/// </summary>
public static class CustomDiagnosticKinds
{
    /// <summary>A captured value failed type coercion or bounds validation.</summary>
    public const string Invalid = "invalid";

    /// <summary>A value was safely truncated at a length bound.</summary>
    public const string Truncated = "truncated";

    /// <summary>Excess matches were discarded at the per-page value cap.</summary>
    public const string Discarded = "discarded";

    /// <summary>Rule/item/snippet evaluation failed and was isolated.</summary>
    public const string Error = "error";

    /// <summary>Evaluation exceeded its time bound and was aborted.</summary>
    public const string Timeout = "timeout";

    /// <summary>The item was skipped because its path scope did not match.</summary>
    public const string OutOfScope = "out-of-scope";

    /// <summary>A required rule captured no value.</summary>
    public const string Missing = "missing";
}

/// <summary>
/// One local rule-level diagnostic. Never transmitted; carries no identity
/// dimensions (privacy invariant).
/// </summary>
/// <param name="ItemKey">Rule name, search-item key, or snippet key.</param>
/// <param name="Kind">One of <see cref="CustomDiagnosticKinds"/>.</param>
/// <param name="Message">Human-readable local detail.</param>
/// <param name="Count">Affected value/match count (1 for single events).</param>
public sealed record CustomDiagnostic(string ItemKey, string Kind, string Message, int Count = 1);

/// <summary>
/// Extraction outcome: persisted value drafts plus local diagnostics
/// (additive surface; <see cref="ICustomContentExtractor.ExtractAsync"/>
/// returns <see cref="Values"/>).
/// </summary>
/// <param name="Values">Typed value drafts for extracted_content.</param>
/// <param name="Diagnostics">Rule-level diagnostics.</param>
public sealed record CustomExtractionResult(
    IReadOnlyList<ExtractedValueDraft> Values,
    IReadOnlyList<CustomDiagnostic> Diagnostics);

/// <summary>
/// Search outcome: per-item matches plus local diagnostics (additive surface;
/// <see cref="ICustomContentSearch.SearchAsync"/> returns <see cref="Matches"/>).
/// </summary>
/// <param name="Matches">Per-item match counts and snippets for content_search_results.</param>
/// <param name="Diagnostics">Item-level diagnostics.</param>
public sealed record CustomSearchResult(
    IReadOnlyList<ContentSearchMatch> Matches,
    IReadOnlyList<CustomDiagnostic> Diagnostics);

/// <summary>
/// Snippet outcome: typed result drafts plus local diagnostics (additive
/// surface; <see cref="IJsSnippetRunner.RunAsync"/> returns <see cref="Results"/>).
/// </summary>
/// <param name="Results">Typed column/row drafts for snippet_results.</param>
/// <param name="Diagnostics">Snippet-level diagnostics.</param>
public sealed record JsSnippetRunResult(
    IReadOnlyList<SnippetResultDraft> Results,
    IReadOnlyList<CustomDiagnostic> Diagnostics);