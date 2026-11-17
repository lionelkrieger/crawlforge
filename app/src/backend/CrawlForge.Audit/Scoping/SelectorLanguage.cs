namespace CrawlForge.Audit.Scoping;

/// <summary>
/// Selector language of one content-scope entry (FS-CRAWL-002 §4.2: CSS
/// selector or XPath, declared per entry; both may be mixed within one scope).
/// The locked <see cref="Contracts.Audit.ContentScopeDefinition"/> carries plain
/// selector strings, so the language is detected deterministically per entry:
/// an expression starting with '/' or '(' is XPath; anything else is CSS.
/// </summary>
public enum SelectorLanguage
{
    /// <summary>CSS selector (AngleSharp selector engine).</summary>
    Css = 0,

    /// <summary>XPath expression (AngleSharp.XPath navigator).</summary>
    XPath = 1,
}

/// <summary>
/// One ordered content-scope selector entry as persisted in
/// content_scope_profiles payloads (FS-CRAWL-002 §4.4: entry = language,
/// expression, order).
/// </summary>
/// <param name="Expression">The selector expression.</param>
/// <param name="Language">Declared selector language.</param>
/// <param name="Order">Zero-based position within its list.</param>
public sealed record ScopeSelectorEntry(string Expression, SelectorLanguage Language, int Order);

/// <summary>
/// Deterministic per-entry selector-language detection for the locked
/// plain-string scope contract.
/// </summary>
public static class SelectorLanguageDetector
{
    /// <summary>Detects the language of a selector expression.</summary>
    /// <param name="expression">Selector expression (non-null).</param>
    /// <returns>XPath for leading '/' or '('; CSS otherwise.</returns>
    public static SelectorLanguage Detect(string expression)
    {
        var trimmed = expression.TrimStart();
        return trimmed.StartsWith('/') || trimmed.StartsWith('(')
            ? SelectorLanguage.XPath
            : SelectorLanguage.Css;
    }
}
