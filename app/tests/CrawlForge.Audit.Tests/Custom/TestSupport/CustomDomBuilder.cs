using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Tests.Custom.TestSupport;

/// <summary>
/// Builds contract-model documents for custom-surface tests.
/// </summary>
internal static class CustomDomBuilder
{
    public static DomDocument Document(string url, DomNode root) =>
        new(url, root, Array.Empty<string>());

    public static DomNode Element(string tag, IReadOnlyDictionary<string, string>? attributes = null, params DomNode[] children) =>
        new(tag.ToLowerInvariant(), attributes ?? new Dictionary<string, string>(), children, string.Empty);

    public static DomNode Text(string tag, string text) =>
        new(tag.ToLowerInvariant(), new Dictionary<string, string>(), Array.Empty<DomNode>(), text);

    public static DomNode Meta(string name, string content) =>
        Element("meta", new Dictionary<string, string> { ["name"] = name, ["content"] = content });

    public static DomNode Anchor(string href, string text) =>
        new("a", new Dictionary<string, string> { ["href"] = href }, Array.Empty<DomNode>(), text);

    public static DomNode Div(string className, params DomNode[] children) =>
        Element("div", new Dictionary<string, string> { ["class"] = className }, children);

    /// <summary>A minimal page: html/head(title)/body(content).</summary>
    public static DomNode Page(string? title, IReadOnlyList<DomNode>? body)
    {
        var headChildren = new List<DomNode>();
        if (title is not null)
        {
            headChildren.Add(Text("title", title));
        }

        return Element(
            "html",
            null,
            Element("head", null, headChildren.ToArray()),
            Element("body", null, body?.ToArray() ?? Array.Empty<DomNode>()));
    }
}