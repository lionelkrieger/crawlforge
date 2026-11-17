using System.Text;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Intelligence.Tests.Geo.Support;

/// <summary>
/// Test double for the pipeline's tolerant DOM parser (cf-wp-00-02 owns the
/// production implementation). Implements a small, deterministic tolerant HTML
/// parser sufficient for structured-data fixtures: tags, quoted/unquoted
/// attributes, comments, doctype, void elements, script/style raw text, and
/// mismatched close tags (recorded as parse warnings, never thrown).
/// </summary>
public sealed class TestDomParser : ITolerantDomParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style" };

    /// <inheritdoc />
    public DomDocument Parse(string html, string url)
    {
        var warnings = new List<string>();
        var root = new NodeBuilder("#document");
        var stack = new Stack<NodeBuilder>();
        stack.Push(root);
        var position = 0;

        while (position < html.Length)
        {
            var next = html.IndexOf('<', position);
            if (next < 0)
            {
                stack.Peek().Text.Append(DecodeEntities(html[position..]));
                break;
            }

            if (next > position)
            {
                stack.Peek().Text.Append(DecodeEntities(html[position..next]));
            }

            if (html.AsSpan(next).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", next + 4, StringComparison.Ordinal);
                position = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (html.AsSpan(next).StartsWith("<!", StringComparison.Ordinal) || html.AsSpan(next).StartsWith("<?", StringComparison.Ordinal))
            {
                var end = html.IndexOf('>', next);
                position = end < 0 ? html.Length : end + 1;
                continue;
            }

            if (html.AsSpan(next).StartsWith("</", StringComparison.Ordinal))
            {
                var end = html.IndexOf('>', next);
                var name = end < 0 ? html[(next + 2)..] : html[(next + 2)..end];
                name = name.Trim().ToLowerInvariant();
                position = end < 0 ? html.Length : end + 1;

                if (stack.Any(n => n.TagName == name))
                {
                    while (stack.Count > 1 && stack.Peek().TagName != name)
                    {
                        warnings.Add($"implicitly closed <{stack.Peek().TagName}> at </{name}>");
                        stack.Pop();
                    }

                    if (stack.Count > 1)
                    {
                        stack.Pop();
                    }
                }
                else
                {
                    warnings.Add($"ignored unmatched close tag </{name}>");
                }

                continue;
            }

            // Open tag.
            var tagEnd = FindTagEnd(html, next);
            var tagText = html[(next + 1)..tagEnd];
            position = tagEnd + 1;
            var selfClosing = tagText.EndsWith('/');
            if (selfClosing)
            {
                tagText = tagText[..^1];
            }

            var (tagName, attributes) = ParseTag(tagText);
            if (tagName.Length == 0)
            {
                continue;
            }

            var node = new NodeBuilder(tagName);
            foreach (var (key, value) in attributes)
            {
                node.Attributes[key] = value;
            }

            stack.Peek().Children.Add(node);

            if (VoidElements.Contains(tagName) || selfClosing)
            {
                continue;
            }

            if (RawTextElements.Contains(tagName))
            {
                var closeIndex = IndexOfCloseTag(html, position, tagName);
                var rawEnd = closeIndex < 0 ? html.Length : closeIndex;
                node.Text.Append(html[position..rawEnd]);
                position = closeIndex < 0
                    ? html.Length
                    : html.IndexOf('>', closeIndex) + 1;
                continue;
            }

            stack.Push(node);
        }

        while (stack.Count > 1)
        {
            warnings.Add($"unclosed <{stack.Peek().TagName}> at end of document");
            stack.Pop();
        }

        return new DomDocument(url, Build(root), warnings);
    }

    private static int FindTagEnd(string html, int start)
    {
        var inQuote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                {
                    inQuote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                inQuote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return html.Length - 1;
    }

    private static int IndexOfCloseTag(string html, int start, string tagName)
    {
        var marker = "</" + tagName;
        var index = start;
        while (true)
        {
            index = html.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var after = index + marker.Length;
            if (after >= html.Length || html[after] is '>' or ' ' or '\t' or '\r' or '\n' or '/')
            {
                return index;
            }

            index = after;
        }
    }

    private static (string Name, List<(string Key, string Value)> Attributes) ParseTag(string tagText)
    {
        var trimmed = tagText.TrimStart();
        var nameEnd = 0;
        while (nameEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[nameEnd]))
        {
            nameEnd++;
        }

        var name = trimmed[..nameEnd].ToLowerInvariant();
        var attributes = new List<(string, string)>();
        var rest = trimmed[nameEnd..];
        var i = 0;
        while (i < rest.Length)
        {
            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }

            if (i >= rest.Length)
            {
                break;
            }

            var keyStart = i;
            while (i < rest.Length && rest[i] != '=' && !char.IsWhiteSpace(rest[i]))
            {
                i++;
            }

            var key = rest[keyStart..i].ToLowerInvariant();
            if (key.Length == 0)
            {
                i++;
                continue;
            }

            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }

            var value = string.Empty;
            if (i < rest.Length && rest[i] == '=')
            {
                i++;
                while (i < rest.Length && char.IsWhiteSpace(rest[i]))
                {
                    i++;
                }

                if (i < rest.Length && rest[i] is '"' or '\'')
                {
                    var quote = rest[i];
                    i++;
                    var valueStart = i;
                    while (i < rest.Length && rest[i] != quote)
                    {
                        i++;
                    }

                    value = rest[valueStart..i];
                    if (i < rest.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    var valueStart = i;
                    while (i < rest.Length && !char.IsWhiteSpace(rest[i]))
                    {
                        i++;
                    }

                    value = rest[valueStart..i];
                }
            }

            attributes.Add((key, DecodeEntities(value)));
        }

        return (name, attributes);
    }

    private static string DecodeEntities(string text) => text
        .Replace("&amp;", "&")
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&#39;", "'")
        .Replace("&nbsp;", " ");

    private static DomNode Build(NodeBuilder builder) =>
        new(builder.TagName, builder.Attributes, builder.Children.Select(Build).ToList(), builder.Text.ToString());

    private sealed class NodeBuilder(string tagName)
    {
        public string TagName { get; } = tagName;

        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<NodeBuilder> Children { get; } = [];

        public StringBuilder Text { get; } = new();
    }
}
