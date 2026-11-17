using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Link purpose family (FS-CRAWL-001 §7.10): links need discernible purpose
/// text; generic phrases such as "click here" are flagged as notices.
/// </summary>
internal static class LinkPurposeRule
{
    /// <summary>Generic link phrases that lack out-of-context purpose.</summary>
    private static readonly HashSet<string> GenericPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "click here", "click this", "here", "read more", "more", "link", "this link",
        "go", "details", "learn more", "this", "page",
    };

    /// <summary>Evaluates link-purpose rules over anchors and image-map areas.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        foreach (var (node, locator) in context.Elements)
        {
            var tag = node.TagName.ToLowerInvariant();
            if (tag is not ("a" or "area"))
            {
                continue;
            }

            // Only hyperlink-bearing elements are links.
            if (DomTraversal.Attr(node, "href") is null)
            {
                continue;
            }

            var name = context.AccessibleName(node);
            if (string.IsNullOrWhiteSpace(name))
            {
                context.Add(
                    AccessibilityRuleKeys.LinkPurpose,
                    node,
                    locator,
                    "Link has no discernible purpose text.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["href"] = DomTraversal.Attr(node, "href") ?? string.Empty,
                    });
                continue;
            }

            if (GenericPhrases.Contains(name.Trim()))
            {
                context.Add(
                    AccessibilityRuleKeys.LinkGenericText,
                    node,
                    locator,
                    $"Link text '{name}' is generic and lacks out-of-context purpose.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["text"] = name,
                        ["href"] = DomTraversal.Attr(node, "href") ?? string.Empty,
                    });
            }
        }
    }
}
