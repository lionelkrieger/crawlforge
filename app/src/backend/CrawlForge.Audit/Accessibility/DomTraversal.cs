using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Accessibility;

/// <summary>
/// Deterministic element locator for accessibility evidence: a CSS-like path
/// and an XPath, both rooted at the document element.
/// </summary>
/// <param name="CssPath">CSS-like locator (id shortcuts, nth-of-type chains).</param>
/// <param name="XPath">Positional XPath locator.</param>
internal readonly record struct DomLocator(string CssPath, string XPath);

/// <summary>
/// Traversal helpers over the contract DOM model (<see cref="DomNode"/>).
/// Pure, local computation — no parser dependency in the analyzer itself.
/// </summary>
internal static class DomTraversal
{
    /// <summary>Tags whose text content is not visible page text.</summary>
    private static readonly HashSet<string> NonVisibleTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template",
    };

    /// <summary>
    /// Depth-first document-order walk carrying a locator and the ancestor
    /// chain (root first, excluding the visited node) for every element.
    /// </summary>
    public static void Walk(
        DomNode root,
        Action<DomNode, DomLocator, IReadOnlyList<DomNode>> visit)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(visit);
        var ancestors = new List<DomNode>();
        WalkCore(root, new DomLocator(root.TagName, "/" + root.TagName), ancestors, visit);
    }

    /// <summary>Enumerates all elements in document order with locators.</summary>
    public static IReadOnlyList<(DomNode Node, DomLocator Locator)> Enumerate(DomNode root)
    {
        var result = new List<(DomNode, DomLocator)>();
        Walk(root, (node, locator, _) => result.Add((node, locator)));
        return result;
    }

    /// <summary>Reads an attribute case-insensitively; null when absent.</summary>
    public static string? Attr(DomNode node, string name)
    {
        foreach (var (key, value) in node.Attributes)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>True when the attribute exists (any value, including empty).</summary>
    public static bool HasAttr(DomNode node, string name) => Attr(node, name) is not null;

    /// <summary>
    /// Collapsed visible text of the subtree: skips script/style/template
    /// content and collapses whitespace runs to single spaces.
    /// </summary>
    public static string VisibleText(DomNode node)
    {
        var builder = new StringBuilder();
        AppendVisibleText(node, builder);
        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>Collapses whitespace runs and trims.</summary>
    public static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reconstructs a bounded start-tag snippet for evidence (never the full
    /// subtree; attribute values are truncated).
    /// </summary>
    public static string StartTagSnippet(DomNode node, int maxLength = 200)
    {
        var builder = new StringBuilder();
        builder.Append('<').Append(node.TagName);
        foreach (var (key, value) in node.Attributes)
        {
            builder.Append(' ').Append(key).Append("=\"");
            builder.Append(value.Length > 60 ? value[..60] + "…" : value);
            builder.Append('"');
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        builder.Append('>');
        var snippet = builder.ToString();
        return snippet.Length > maxLength ? snippet[..maxLength] + "…" : snippet;
    }

    /// <summary>
    /// Simplified accessible-name computation (ARIA naming order):
    /// aria-labelledby → aria-label → native semantics → title.
    /// Returns null when no name source yields content.
    /// </summary>
    public static string? AccessibleName(
        DomNode node,
        IReadOnlyDictionary<string, DomNode> elementsById,
        IReadOnlyList<DomNode> allElements)
    {
        var labelledBy = Attr(node, "aria-labelledby");
        if (!string.IsNullOrWhiteSpace(labelledBy))
        {
            var parts = new List<string>();
            foreach (var id in labelledBy.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (elementsById.TryGetValue(id, out var referenced))
                {
                    var text = VisibleText(referenced);
                    if (text.Length > 0)
                    {
                        parts.Add(text);
                    }
                }
            }

            if (parts.Count > 0)
            {
                return string.Join(' ', parts);
            }
        }

        var ariaLabel = Attr(node, "aria-label");
        if (!string.IsNullOrWhiteSpace(ariaLabel))
        {
            return CollapseWhitespace(ariaLabel);
        }

        var tag = node.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "img":
                var alt = Attr(node, "alt");
                if (!string.IsNullOrWhiteSpace(alt))
                {
                    return CollapseWhitespace(alt);
                }

                break;
            case "input":
                var inputType = (Attr(node, "type") ?? "text").ToLowerInvariant();
                if (inputType is "button" or "submit" or "reset")
                {
                    var value = Attr(node, "value");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return CollapseWhitespace(value);
                    }
                }
                else if (inputType == "image")
                {
                    var imageAlt = Attr(node, "alt");
                    if (!string.IsNullOrWhiteSpace(imageAlt))
                    {
                        return CollapseWhitespace(imageAlt);
                    }
                }

                // Associated label element (for=id).
                var id = Attr(node, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    foreach (var label in allElements)
                    {
                        if (string.Equals(label.TagName, "label", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Attr(label, "for"), id, StringComparison.Ordinal))
                        {
                            var labelText = VisibleText(label);
                            if (labelText.Length > 0)
                            {
                                return labelText;
                            }
                        }
                    }
                }

                break;
            case "area":
                var areaAlt = Attr(node, "alt");
                if (!string.IsNullOrWhiteSpace(areaAlt))
                {
                    return CollapseWhitespace(areaAlt);
                }

                break;
            default:
                var text = VisibleText(node);
                if (text.Length > 0)
                {
                    return text;
                }

                // Image-only content: descendant img alt contributes a name.
                foreach (var (child, _) in Enumerate(node))
                {
                    if (string.Equals(child.TagName, "img", StringComparison.OrdinalIgnoreCase))
                    {
                        var childAlt = Attr(child, "alt");
                        if (!string.IsNullOrWhiteSpace(childAlt))
                        {
                            return CollapseWhitespace(childAlt);
                        }
                    }
                }

                break;
        }

        var title = Attr(node, "title");
        return string.IsNullOrWhiteSpace(title) ? null : CollapseWhitespace(title);
    }

    private static void WalkCore(
        DomNode node,
        DomLocator locator,
        List<DomNode> ancestors,
        Action<DomNode, DomLocator, IReadOnlyList<DomNode>> visit)
    {
        visit(node, locator, ancestors);
        ancestors.Add(node);
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in node.Children)
        {
            tagCounts.TryGetValue(child.TagName, out var seen);
            seen++;
            tagCounts[child.TagName] = seen;

            var id = Attr(child, "id");
            var cssSegment = string.IsNullOrWhiteSpace(id)
                ? $"{child.TagName}:nth-of-type({seen})"
                : $"{child.TagName}#{id}";
            var childLocator = new DomLocator(
                locator.CssPath + " > " + cssSegment,
                locator.XPath + "/" + child.TagName + "[" + seen + "]");
            WalkCore(child, childLocator, ancestors, visit);
        }

        ancestors.RemoveAt(ancestors.Count - 1);
    }

    private static void AppendVisibleText(DomNode node, StringBuilder builder)
    {
        if (NonVisibleTextTags.Contains(node.TagName))
        {
            return;
        }

        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            AppendVisibleText(child, builder);
        }
    }
}
