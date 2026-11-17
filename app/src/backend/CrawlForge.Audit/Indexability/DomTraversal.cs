using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Dependency-free traversal helpers over the contract DOM model
/// (<see cref="DomDocument"/>/<see cref="DomNode"/>). All indexability passes
/// are pure functions of the stored page context, so every rule re-runs
/// offline from stored artifacts on re-audit (FS-CRAWL-001 §9.6).
/// </summary>
internal static class DomTraversal
{
    private static readonly HashSet<string> NonVisibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title",
    };

    /// <summary>Depth-first enumeration of every element in document order.</summary>
    public static IEnumerable<DomNode> EnumerateElements(DomDocument? document)
    {
        if (document?.Root is null)
        {
            yield break;
        }

        var stack = new Stack<DomNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            DomNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    /// <summary>Elements with the given tag name (case-insensitive).</summary>
    public static IEnumerable<DomNode> Elements(DomDocument? document, string tagName) =>
        EnumerateElements(document).Where(n => string.Equals(n.TagName, tagName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Case-insensitive attribute lookup; null when absent.</summary>
    public static string? Attribute(DomNode node, string name)
    {
        foreach (KeyValuePair<string, string> attribute in node.Attributes)
        {
            if (string.Equals(attribute.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>The document title text (first title element), or null.</summary>
    public static string? Title(DomDocument? document)
    {
        DomNode? title = Elements(document, "title").FirstOrDefault();
        return title is null ? null : CollapseWhitespace(title.TextContent);
    }

    /// <summary>
    /// Visible text of the document: concatenated text nodes outside
    /// non-visible tags, whitespace-collapsed. Bounded by the caller when
    /// persisted as evidence.
    /// </summary>
    public static string VisibleText(DomDocument? document)
    {
        if (document?.Root is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        AppendVisibleText(document.Root, builder);
        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>Whitespace-collapsed concatenation of a node's direct text.</summary>
    public static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static void AppendVisibleText(DomNode node, System.Text.StringBuilder builder)
    {
        if (NonVisibleTags.Contains(node.TagName))
        {
            return;
        }

        if (!string.IsNullOrEmpty(node.TextContent))
        {
            builder.Append(node.TextContent).Append(' ');
        }

        foreach (DomNode child in node.Children)
        {
            AppendVisibleText(child, builder);
        }
    }
}