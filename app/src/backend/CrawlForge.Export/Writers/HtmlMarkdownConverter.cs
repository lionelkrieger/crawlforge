using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Export.Writers;

/// <summary>
/// HTML → Markdown conversion over the dependency-free contract DOM
/// (WP-09-10, CAP-G-165 full page content export; FS-CRAWL-001 §8.4 CR-017,
/// §11.8 "Markdown for page content"). Covers the common content vocabulary:
/// headings, paragraphs, lists (nested), links, images, emphasis, inline and
/// fenced code, blockquotes, horizontal rules, and pipe tables. Script/style
/// and other non-content elements are dropped. The contract DOM exposes
/// direct text (<see cref="DomNode.TextContent"/>) plus element children; text
/// is emitted before child elements, matching the accepted DomTraversal
/// visible-text convention.
/// </summary>
public static class HtmlMarkdownConverter
{
    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "head", "meta", "link", "title", "base",
    };

    /// <summary>Converts the document body to Markdown.</summary>
    /// <param name="document">Parsed document.</param>
    /// <returns>Markdown content (no document header; callers add identity).</returns>
    public static string Convert(DomDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var body = FindElement(document.Root, "body") ?? document.Root;
        var builder = new StringBuilder();
        AppendBlocks(builder, body, indent: string.Empty);
        return CollapseBlankLines(builder.ToString()).TrimEnd() + "\n";
    }

    private static void AppendBlocks(StringBuilder builder, DomNode node, string indent)
    {
        var directText = CollapseWhitespace(node.TextContent);
        if (directText.Length > 0 && !IsInlineOnly(node))
        {
            builder.Append(indent).AppendLine(directText).AppendLine();
        }

        foreach (var child in node.Children)
        {
            AppendBlock(builder, child, indent);
        }
    }

    private static void AppendBlock(StringBuilder builder, DomNode node, string indent)
    {
        var tag = node.TagName;
        if (SkipTags.Contains(tag))
        {
            return;
        }

        switch (tag)
        {
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                var level = tag[1] - '0';
                builder.Append(indent).Append(new string('#', level)).Append(' ').AppendLine(Inline(node)).AppendLine();
                break;

            case "p":
                var paragraph = Inline(node);
                if (paragraph.Length > 0)
                {
                    builder.Append(indent).AppendLine(paragraph).AppendLine();
                }

                break;

            case "ul":
            case "ol":
                AppendList(builder, node, ordered: tag == "ol", indent);
                builder.AppendLine();
                break;

            case "blockquote":
                var inner = new StringBuilder();
                AppendBlocks(inner, node, indent: string.Empty);
                foreach (var line in CollapseBlankLines(inner.ToString()).TrimEnd().Split('\n'))
                {
                    builder.Append(indent).Append("> ").AppendLine(line.TrimEnd());
                }

                builder.AppendLine();
                break;

            case "pre":
                builder.Append(indent).AppendLine("```");
                foreach (var line in RawText(node).TrimEnd().Split('\n'))
                {
                    builder.Append(indent).AppendLine(line.TrimEnd());
                }

                builder.Append(indent).AppendLine("```").AppendLine();
                break;

            case "hr":
                builder.Append(indent).AppendLine("---").AppendLine();
                break;

            case "table":
                AppendTable(builder, node, indent);
                builder.AppendLine();
                break;

            case "br":
                builder.AppendLine();
                break;

            default:
                if (node.Children.Count > 0)
                {
                    AppendBlocks(builder, node, indent);
                }
                else
                {
                    var text = Inline(node);
                    if (text.Length > 0)
                    {
                        builder.Append(indent).AppendLine(text).AppendLine();
                    }
                }

                break;
        }
    }

    private static void AppendList(StringBuilder builder, DomNode node, bool ordered, string indent)
    {
        var ordinal = 1;
        foreach (var child in node.Children)
        {
            if (!child.TagName.Equals("li", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var marker = ordered ? ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : "- ";
            var nested = new StringBuilder();
            var text = new StringBuilder();
            text.Append(CollapseWhitespace(child.TextContent));
            foreach (var item in child.Children)
            {
                if (item.TagName is "ul" or "ol")
                {
                    AppendList(nested, item, item.TagName == "ol", indent + "  ");
                }
                else
                {
                    text.Append(Inline(item));
                }
            }

            builder.Append(indent).Append(marker).AppendLine(CollapseWhitespace(text.ToString()));
            builder.Append(nested);
            ordinal++;
        }
    }

    private static void AppendTable(StringBuilder builder, DomNode node, string indent)
    {
        var rows = new List<List<string>>();
        CollectRows(node, rows);
        if (rows.Count == 0)
        {
            return;
        }

        var width = rows.Max(r => r.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            builder.Append(indent).Append('|');
            for (var c = 0; c < width; c++)
            {
                var cell = c < cells.Count ? cells[c] : string.Empty;
                builder.Append(' ').Append(cell.Replace("|", "\\|", StringComparison.Ordinal)).Append(" |");
            }

            builder.AppendLine();
            if (i == 0)
            {
                builder.Append(indent).Append('|');
                for (var c = 0; c < width; c++)
                {
                    builder.Append(" --- |");
                }

                builder.AppendLine();
            }
        }
    }

    private static void CollectRows(DomNode node, List<List<string>> rows)
    {
        foreach (var child in node.Children)
        {
            if (child.TagName.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                var cells = new List<string>();
                foreach (var cell in child.Children)
                {
                    if (cell.TagName is "td" or "th")
                    {
                        cells.Add(CollapseWhitespace(Inline(cell)));
                    }
                }

                rows.Add(cells);
            }
            else if (child.TagName is "thead" or "tbody" or "tfoot")
            {
                CollectRows(child, rows);
            }
        }
    }

    private static string Inline(DomNode node)
    {
        if (SkipTags.Contains(node.TagName))
        {
            return string.Empty;
        }

        var inner = new StringBuilder();
        inner.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            inner.Append(Inline(child));
        }

        var text = CollapseWhitespace(inner.ToString());
        return node.TagName switch
        {
            "a" => "[" + text + "](" + (node.Attributes.TryGetValue("href", out var href) ? href : string.Empty) + ")",
            "img" => "![" + (node.Attributes.TryGetValue("alt", out var alt) ? alt : string.Empty) + "]("
                + (node.Attributes.TryGetValue("src", out var src) ? src : string.Empty) + ")",
            "strong" or "b" => text.Length > 0 ? "**" + text + "**" : string.Empty,
            "em" or "i" => text.Length > 0 ? "*" + text + "*" : string.Empty,
            "code" => text.Length > 0 ? "`" + text + "`" : string.Empty,
            "br" => "\n",
            _ => text,
        };
    }

    private static string RawText(DomNode node)
    {
        var builder = new StringBuilder();
        builder.Append(node.TextContent);
        foreach (var child in node.Children)
        {
            builder.Append(RawText(child));
        }

        return builder.ToString();
    }

    private static bool IsInlineOnly(DomNode node)
        => node.Children.Count == 0;

    private static DomNode? FindElement(DomNode node, string tag)
    {
        if (node.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindElement(child, tag) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string CollapseWhitespace(string text)
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

    private static string CollapseBlankLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        var blankRun = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                blankRun++;
                if (blankRun <= 1)
                {
                    builder.AppendLine();
                }
            }
            else
            {
                blankRun = 0;
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }
}