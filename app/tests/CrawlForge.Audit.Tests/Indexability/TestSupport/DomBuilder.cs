using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Tests.Indexability.TestSupport;

/// <summary>
/// Builds contract-model documents for tests without depending on the
/// parallel pipeline workpiece's real parser.
/// </summary>
internal static class DomBuilder
{
    public static DomDocument Document(string url, DomNode root) =>
        new(url, root, Array.Empty<string>());

    public static DomNode Element(string tag, IReadOnlyDictionary<string, string>? attributes = null, params DomNode[] children) =>
        new(tag.ToLowerInvariant(), attributes ?? new Dictionary<string, string>(), children, string.Empty);

    public static DomNode Text(string tag, string text) =>
        new(tag.ToLowerInvariant(), new Dictionary<string, string>(), Array.Empty<DomNode>(), text);

    public static DomNode Meta(string name, string content) =>
        Element("meta", new Dictionary<string, string> { ["name"] = name, ["content"] = content });

    public static DomNode Link(string rel, string href, string? hreflang = null)
    {
        var attributes = new Dictionary<string, string> { ["rel"] = rel, ["href"] = href };
        if (hreflang is not null)
        {
            attributes["hreflang"] = hreflang;
        }

        return Element("link", attributes);
    }

    public static DomNode Anchor(string href, string text) =>
        new("a", new Dictionary<string, string> { ["href"] = href }, Array.Empty<DomNode>(), text);

    /// <summary>A minimal page: html/head(title, metas, links)/body(content).</summary>
    public static DomNode Page(string? title, IReadOnlyList<DomNode>? head, IReadOnlyList<DomNode>? body)
    {
        var headChildren = new List<DomNode>();
        if (title is not null)
        {
            headChildren.Add(Text("title", title));
        }

        if (head is not null)
        {
            headChildren.AddRange(head);
        }

        return Element(
            "html",
            null,
            Element("head", null, headChildren.ToArray()),
            Element("body", null, body?.ToArray() ?? Array.Empty<DomNode>()));
    }
}