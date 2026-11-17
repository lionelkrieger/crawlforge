using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Visible-text and snippet helpers for the custom surfaces. Self-contained
/// so the Custom subfolder composes only against locked contracts.
/// </summary>
internal static class CustomTextUtility
{
    private static readonly HashSet<string> NonVisibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title",
    };

    /// <summary>Concatenates visible text nodes depth-first, whitespace-collapsed.</summary>
    public static string VisibleText(DomDocument? document)
    {
        if (document is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var stack = new Stack<DomNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            DomNode node = stack.Pop();
            if (NonVisibleTags.Contains(node.TagName))
            {
                continue;
            }

            if (node.Children.Count == 0 && node.TextContent.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(node.TextContent);
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>Collapses runs of whitespace to single spaces and trims.</summary>
    public static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
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

                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a bounded context snippet around a match, never splitting a
    /// surrogate pair at either boundary.
    /// </summary>
    public static string BuildSnippet(string text, int matchIndex, int matchLength, int contextChars)
    {
        int start = Math.Max(0, matchIndex - contextChars);
        int end = Math.Min(text.Length, matchIndex + matchLength + contextChars);

        // Never split a multi-byte character (surrogate pair) at the bounds.
        if (start > 0 && char.IsLowSurrogate(text[start]))
        {
            start--;
        }

        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        string snippet = text.Substring(start, end - start);
        string prefix = start > 0 ? "…" : string.Empty;
        string suffix = end < text.Length ? "…" : string.Empty;
        return prefix + CollapseWhitespace(snippet) + suffix;
    }

    /// <summary>
    /// Truncates a value at a character bound without splitting a surrogate
    /// pair (FS-CRAWL-001 §7.9: safe truncation that never splits a
    /// multi-byte character).
    /// </summary>
    public static string TruncateSafe(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int end = maxLength;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value.Substring(0, end);
    }
}