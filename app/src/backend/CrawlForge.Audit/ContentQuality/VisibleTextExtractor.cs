using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.ContentQuality;

/// <summary>
/// Extracts baseline visible text and head facts from the contract DOM model
/// (FS-CRAWL-001 §7.3). With no content scope configured this full visible
/// text is the baseline every text metric consumes (FS-CRAWL-002 §4.2
/// precedence rule 4).
/// </summary>
public static class VisibleTextExtractor
{
    private static readonly HashSet<string> NonVisibleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title", "base",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>Extracts the full visible text of a document, whitespace-collapsed.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Visible text in document order.</returns>
    public static string ExtractVisibleText(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        AppendNode(builder, document.Root);
        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>Extracts the document title (first <c>title</c> element), or null.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Title text, or null when absent.</returns>
    public static string? ExtractTitle(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FindFirstText(document.Root, "title");
    }

    /// <summary>Extracts the meta description content, or null.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Meta description, or null when absent.</returns>
    public static string? ExtractMetaDescription(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var meta = FindElement(document.Root, e =>
            e.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase)
            && e.Attributes.TryGetValue("name", out var name)
            && name.Equals("description", StringComparison.OrdinalIgnoreCase));
        return meta is not null && meta.Attributes.TryGetValue("content", out var content)
            ? CollapseWhitespace(content)
            : null;
    }

    /// <summary>Extracts the declared page language (<c>html lang</c>), or null.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Language tag, or null when undeclared.</returns>
    public static string? ExtractLanguage(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var html = FindElement(document.Root, e => e.TagName.Equals("html", StringComparison.OrdinalIgnoreCase));
        if (html is not null && html.Attributes.TryGetValue("lang", out var lang) && !string.IsNullOrWhiteSpace(lang))
        {
            return lang.Trim();
        }

        return null;
    }

    /// <summary>Counts whitespace-delimited words in text.</summary>
    /// <param name="text">Text to count.</param>
    /// <returns>Word count.</returns>
    public static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private static void AppendNode(StringBuilder builder, DomNode node)
    {
        if (NonVisibleTags.Contains(node.TagName))
        {
            return;
        }

        var isBlock = BlockTags.Contains(node.TagName);
        if (isBlock)
        {
            builder.Append(' ');
        }

        if (!string.IsNullOrEmpty(node.TextContent))
        {
            builder.Append(node.TextContent);
        }

        foreach (var child in node.Children)
        {
            AppendNode(builder, child);
        }

        if (isBlock)
        {
            builder.Append(' ');
        }
    }

    private static string? FindFirstText(DomNode node, string tagName)
    {
        if (node.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
        {
            var text = CollectText(node);
            return string.IsNullOrWhiteSpace(text) ? null : CollapseWhitespace(text);
        }

        foreach (var child in node.Children)
        {
            var found = FindFirstText(child, tagName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static DomNode? FindElement(DomNode node, Func<DomNode, bool> predicate)
    {
        if (predicate(node))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindElement(child, predicate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string CollectText(DomNode node)
    {
        var builder = new StringBuilder();
        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            builder.Append(CollectText(child));
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
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
}
