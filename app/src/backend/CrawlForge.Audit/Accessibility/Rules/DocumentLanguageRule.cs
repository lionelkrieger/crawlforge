using System.Text.RegularExpressions;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Document language family (FS-CRAWL-001 §7.10): the html element declares a
/// primary language with a well-formed BCP 47 tag.
/// </summary>
internal static partial class DocumentLanguageRule
{
    /// <summary>
    /// BCP 47 shape check: 2–3 letter primary language plus optional
    /// alphanumeric subtags (script/region/variants). Full registry validation
    /// is out of scope for the bundled rule set.
    /// </summary>
    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagRegex();

    /// <summary>Evaluates document-language rules.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        DomNode? html = null;
        DomLocator htmlLocator = default;
        foreach (var (node, locator) in context.Elements)
        {
            if (string.Equals(node.TagName, "html", StringComparison.OrdinalIgnoreCase))
            {
                html = node;
                htmlLocator = locator;
                break;
            }
        }

        if (html is null)
        {
            // Fragment documents (no html element): nothing to declare.
            return;
        }

        var lang = DomTraversal.Attr(html, "lang");
        if (string.IsNullOrWhiteSpace(lang))
        {
            context.Add(
                AccessibilityRuleKeys.DocumentLanguage,
                html,
                htmlLocator,
                "The html element has no lang attribute.");
            return;
        }

        if (!LanguageTagRegex().IsMatch(lang.Trim()))
        {
            context.Add(
                AccessibilityRuleKeys.DocumentLanguageValid,
                html,
                htmlLocator,
                $"The language tag '{lang}' is not a well-formed BCP 47 tag.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["lang"] = lang });
        }
    }
}
