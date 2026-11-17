using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Tests.Accessibility;

/// <summary>
/// Fixture helper: parses real HTML with AngleSharp (tolerant, like the
/// pipeline parser) and maps it onto the contract DOM model the analyzers
/// consume. Test-local — the product analyzers stay parser-free.
/// </summary>
public static class TestDom
{
    /// <summary>Parses HTML into a contract DomDocument.</summary>
    /// <param name="html">Fixture markup.</param>
    /// <param name="url">Source URL.</param>
    /// <returns>The contract document.</returns>
    public static DomDocument Parse(string html, string url = "https://target.test/")
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var root = Convert(document.DocumentElement);
        return new DomDocument(url, root, []);
    }

    private static DomNode Convert(IElement element) => new(
        element.TagName.ToLowerInvariant(),
        element.Attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value),
        element.Children.Select(Convert).ToList(),
        string.Concat(element.ChildNodes.OfType<IText>().Select(text => text.Text)));
}
