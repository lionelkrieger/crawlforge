namespace CrawlForge.Contracts.Audit;

/// <summary>
/// Resolves the Scoped Visible Text Tree (FS-CRAWL-002 §4): the scope-filtered
/// visible-text view feeding all text passes. With no scope configured the
/// resolver returns the full visible text, reproducing the baseline exactly
/// (precedence rule 4).
/// </summary>
public interface IContentScopeResolver
{
    /// <summary>Resolves scoped visible text for a document.</summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="scope">Include/exclude scope; null means full visible text.</param>
    /// <returns>The scoped visible text with diagnostics.</returns>
    ScopedVisibleText Resolve(DomDocument document, ContentScopeDefinition? scope);
}

/// <summary>
/// An include/exclude content scope (schema catalog: content_scope_profiles;
/// inline on configurations). Selectors are CSS selectors.
/// </summary>
/// <param name="IncludeSelectors">Selectors whose subtrees are included.</param>
/// <param name="ExcludeSelectors">Selectors whose subtrees are stripped as boilerplate.</param>
public sealed record ContentScopeDefinition(
    IReadOnlyList<string> IncludeSelectors,
    IReadOnlyList<string> ExcludeSelectors);

/// <summary>
/// The resolved scoped visible text of a document.
/// </summary>
/// <param name="Text">Visible text after scope filtering.</param>
/// <param name="WordCount">Word count of the scoped text.</param>
/// <param name="Diagnostics">Scope diagnostics (element group content_scope).</param>
public sealed record ScopedVisibleText(
    string Text,
    int WordCount,
    IReadOnlyList<string> Diagnostics);
