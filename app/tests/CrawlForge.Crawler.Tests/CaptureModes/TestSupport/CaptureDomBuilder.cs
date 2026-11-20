using CrawlForge.Contracts.Audit;

namespace CrawlForge.Crawler.Tests.CaptureModes.TestSupport;

/// <summary>
/// Builds contract-model documents for capture-mode component tests without an
/// HTML parser dependency (the Crawler test project does not reference Audit).
/// The real HTML→DOM fidelity is proven in the Audit RenderedComparison
/// conformance tests with the production AngleSharp parser.
/// </summary>
internal static class CaptureDomBuilder
{
    public static DomDocument Document(string url, DomNode root) => new(url, root, Array.Empty<string>());

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

    public static DomNode Script(string text) => Text("script", text);

    /// <summary>A minimal page: html/head(title, head extras)/body(content).</summary>
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

    /// <summary>A page with a title and a body of N whitespace-separated words.</summary>
    public static DomDocument WordsPage(string url, string title, int words, params DomNode[] extraBody)
    {
        string text = string.Join(' ', Enumerable.Range(0, words).Select(i => "word" + i));
        var body = new List<DomNode> { Text("p", text) };
        body.AddRange(extraBody);
        return Document(url, Page(title, null, body));
    }
}
