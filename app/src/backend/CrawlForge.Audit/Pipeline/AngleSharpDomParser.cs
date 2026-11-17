using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Html.Parser;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Tolerant HTML5 parser (FS-CRAWL-001 §7.2 step 2): AngleSharp's standards-
/// compliant parser always produces a document tree, even for malformed
/// markup; parse errors are collected as warnings instead of failing. Maps the
/// parser output onto the dependency-free contract DOM (<see cref="DomDocument"/>).
/// </summary>
public sealed class AngleSharpDomParser : ITolerantDomParser
{
    /// <inheritdoc />
    public DomDocument Parse(string html, string url)
    {
        var warnings = new List<string>();
        var parser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = false,
        });
        parser.Error += (_, e) =>
        {
            if (e is HtmlErrorEvent error)
            {
                warnings.Add($"{error.Message} (line {error.Position.Line}, column {error.Position.Column})");
            }
        };

        var document = parser.ParseDocument(html ?? string.Empty);
        var root = document.DocumentElement is { } htmlElement
            ? Map(htmlElement)
            : new DomNode("html", new Dictionary<string, string>(), [], string.Empty);

        return new DomDocument(url, root, warnings);
    }

    private static DomNode Map(IElement element)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes)
        {
            attributes[attribute.Name] = attribute.Value;
        }

        var children = new List<DomNode>(element.Children.Length);
        foreach (var child in element.Children)
        {
            children.Add(Map(child));
        }

        var directText = string.Concat(element.ChildNodes.OfType<IText>().Select(text => text.Text));

        return new DomNode(
            element.TagName.ToLowerInvariant(),
            attributes,
            children,
            directText);
    }
}
