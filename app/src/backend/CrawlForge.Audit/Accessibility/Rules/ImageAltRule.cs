using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility.Rules;

/// <summary>
/// Image alternative-text family (FS-CRAWL-001 §7.10): img, input[type=image],
/// and area elements must carry alternative text or an explicit decorative
/// marker. A figure/figcaption pairing counts as a text alternative.
/// </summary>
internal static class ImageAltRule
{
    /// <summary>Evaluates the image-alt rule over the page.</summary>
    public static void Evaluate(AccessibilityRuleContext context)
    {
        foreach (var (node, locator) in context.Elements)
        {
            var tag = node.TagName.ToLowerInvariant();
            switch (tag)
            {
                case "img":
                    EvaluateImage(context, node, locator);
                    break;
                case "input" when string.Equals(
                    DomTraversal.Attr(node, "type"), "image", StringComparison.OrdinalIgnoreCase):
                    if (DomTraversal.Attr(node, "alt") is null)
                    {
                        context.Add(
                            AccessibilityRuleKeys.ImageAlt,
                            node,
                            locator,
                            "Image input has no alt attribute.",
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["element"] = "input[type=image]" });
                    }

                    break;
                case "area":
                    if (DomTraversal.Attr(node, "alt") is null)
                    {
                        context.Add(
                            AccessibilityRuleKeys.ImageAlt,
                            node,
                            locator,
                            "Image-map area has no alt attribute.",
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["element"] = "area" });
                    }

                    break;
            }
        }
    }

    private static void EvaluateImage(AccessibilityRuleContext context, DomNode node, DomLocator locator)
    {
        // Explicit decorative markers: role=presentation/none or aria-hidden.
        var role = DomTraversal.Attr(node, "role");
        if (role is not null)
        {
            var tokens = role.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Any(token => token is "presentation" or "none"))
            {
                return;
            }
        }

        if (string.Equals(DomTraversal.Attr(node, "aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // alt present (including empty for decorative images) satisfies the rule.
        if (DomTraversal.Attr(node, "alt") is not null)
        {
            return;
        }

        // figure > figcaption provides a text alternative for the image.
        if (HasFigcaptionAlternative(context, node))
        {
            return;
        }

        context.Add(
            AccessibilityRuleKeys.ImageAlt,
            node,
            locator,
            "Image has no alt attribute.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["element"] = "img",
                ["src"] = DomTraversal.Attr(node, "src") ?? string.Empty,
            });
    }

    private static bool HasFigcaptionAlternative(AccessibilityRuleContext context, DomNode image)
    {
        // Locate the image's parent figure via the ancestor chain captured in
        // the element list: a figure whose subtree contains this image and a
        // figcaption with content.
        foreach (var (candidate, _) in context.ByTag("figure"))
        {
            var containsImage = false;
            var hasCaption = false;
            foreach (var (descendant, _) in DomTraversal.Enumerate(candidate))
            {
                if (ReferenceEquals(descendant, image))
                {
                    containsImage = true;
                }

                if (string.Equals(descendant.TagName, "figcaption", StringComparison.OrdinalIgnoreCase)
                    && DomTraversal.VisibleText(descendant).Length > 0)
                {
                    hasCaption = true;
                }
            }

            if (containsImage && hasCaption)
            {
                return true;
            }
        }

        return false;
    }
}
