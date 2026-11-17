using System.Xml.XPath;
using AngleSharp.Html.Parser;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Scoping;

/// <summary>
/// Configuration-time content-scope validation (FS-CRAWL-002 §4.2): every
/// include/exclude entry is compiled in its detected language; invalid
/// selectors are rejected with a descriptive local error before any crawl
/// starts. Also exposes the bundled read-only boilerplate preset — applying it
/// is an explicit user action; nothing is stripped automatically (precedence
/// rule 4, baseline-preserving defaults).
/// </summary>
public static class ContentScopeValidator
{
    /// <summary>Name of the bundled boilerplate preset (FS-CRAWL-002 §4.2).</summary>
    public const string BoilerplatePresetName = "boilerplate";

    private static readonly Lazy<AngleSharp.Dom.IDocument> ScratchDocument = new(
        () => new HtmlParser().ParseDocument("<html><head></head><body></body></html>"));

    /// <summary>
    /// The bundled common-boilerplate exclude preset: nav, header, footer,
    /// aside, cookie banner, mega menu, ARIA landmark roles, sidebar,
    /// newsletter signup, social share. Include list is empty.
    /// </summary>
    public static ContentScopeDefinition BoilerplatePreset { get; } = new(
        IncludeSelectors: [],
        ExcludeSelectors:
        [
            "nav",
            "header",
            "footer",
            "aside",
            ".cookie-banner",
            ".mega-menu",
            "[role=navigation]",
            "[role=banner]",
            "[role=contentinfo]",
            ".sidebar",
            ".newsletter-signup",
            ".social-share",
        ]);

    /// <summary>Validates a scope, throwing on any invalid entry.</summary>
    /// <param name="scope">Scope to validate; null is valid (baseline).</param>
    /// <exception cref="ContentScopeValidationException">Any entry is invalid.</exception>
    public static void Validate(ContentScopeDefinition? scope)
    {
        if (!TryValidate(scope, out var errors))
        {
            throw new ContentScopeValidationException(errors);
        }
    }

    /// <summary>Validates a scope without throwing.</summary>
    /// <param name="scope">Scope to validate; null is valid (baseline).</param>
    /// <param name="errors">Per-entry descriptive errors when invalid.</param>
    /// <returns>True when every entry compiles in its detected language.</returns>
    public static bool TryValidate(ContentScopeDefinition? scope, out IReadOnlyList<string> errors)
    {
        var collected = new List<string>();
        if (scope is not null)
        {
            ValidateList(scope.IncludeSelectors, "include", collected);
            ValidateList(scope.ExcludeSelectors, "exclude", collected);
        }

        errors = collected;
        return collected.Count == 0;
    }

    private static void ValidateList(IReadOnlyList<string> selectors, string listName, List<string> errors)
    {
        for (var i = 0; i < selectors.Count; i++)
        {
            var expression = selectors[i];
            if (string.IsNullOrWhiteSpace(expression))
            {
                errors.Add($"{listName}[{i}]: selector expression is empty");
                continue;
            }

            var language = SelectorLanguageDetector.Detect(expression);
            try
            {
                if (language == SelectorLanguage.XPath)
                {
                    XPathExpression.Compile(expression);
                }
                else
                {
                    ScratchDocument.Value.QuerySelectorAll(expression);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{listName}[{i}] '{expression}' ({language.ToString().ToLowerInvariant()}): {ex.Message}");
            }
        }
    }
}
