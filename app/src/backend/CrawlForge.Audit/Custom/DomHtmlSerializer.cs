using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Custom;

/// <summary>
/// Serializes the contract DOM model back to HTML so selector engines
/// (AngleSharp CSS/XPath), raw-markup patterns, and the snippet host API can
/// evaluate the same stored document offline. Deterministic: identical input
/// always produces identical markup (re-audit purity).
/// </summary>
internal static class DomHtmlSerializer
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Serializes a document to HTML; null yields an empty string.</summary>
    public static string Serialize(DomDocument? document)
    {
        if (document is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendNode(builder, document.Root);
        return builder.ToString();
    }

    private static void AppendNode(StringBuilder builder, DomNode node)
    {
        string tag = node.TagName.ToLowerInvariant();
        builder.Append('<').Append(tag);
        foreach (var (name, value) in node.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(name).Append("=\"").Append(EscapeAttribute(value)).Append('"');
        }

        if (VoidElements.Contains(tag))
        {
            builder.Append(" />");
            return;
        }

        builder.Append('>');
        if (node.Children.Count == 0)
        {
            builder.Append(EscapeText(node.TextContent));
        }
        else
        {
            foreach (DomNode child in node.Children)
            {
                AppendNode(builder, child);
            }
        }

        builder.Append("</").Append(tag).Append('>');
    }

    private static string EscapeText(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string EscapeAttribute(string value) =>
        EscapeText(value).Replace("\"", "&quot;");
}