using System.Globalization;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.XPath;
using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Scoping;

/// <summary>
/// Rehydrates the dependency-free contract DOM (<see cref="DomDocument"/>)
/// into an AngleSharp document so the standards-compliant CSS and XPath
/// engines can evaluate scope selectors. Every rehydrated element carries its
/// document-order contract-node index in a data attribute, so selector matches
/// map back onto the contract tree deterministically. Direct text content is
/// re-attached ahead of child elements (the contract model flattens direct
/// text); word counts and visibility are unaffected.
/// </summary>
internal static class DomRehydrator
{
    internal const string NodeIndexAttribute = "data-cf-node";

    /// <summary>Rehydrates a contract document for selector evaluation.</summary>
    /// <param name="document">Contract document.</param>
    /// <param name="indexedNodes">Contract nodes in document order (index = position).</param>
    /// <returns>An AngleSharp document mirroring the contract tree.</returns>
    public static IHtmlDocument Rehydrate(DomDocument document, out IReadOnlyList<DomNode> indexedNodes)
    {
        var context = BrowsingContext.New(Configuration.Default.WithXPath());
        var parser = new HtmlParser(new HtmlParserOptions(), context);
        var scratch = parser.ParseDocument("<html><head></head><body></body></html>");
        var nodes = new List<DomNode>();
        var scratchRoot = scratch.DocumentElement!;

        if (string.Equals(document.Root.TagName, "html", StringComparison.OrdinalIgnoreCase))
        {
            nodes.Add(document.Root);
            scratchRoot.SetAttribute(NodeIndexAttribute, "0");
            foreach (var (name, value) in document.Root.Attributes)
            {
                scratchRoot.SetAttribute(name, value);
            }

            foreach (var child in document.Root.Children)
            {
                scratchRoot.AppendChild(Build(scratch, child, nodes));
            }
        }
        else
        {
            scratch.Body!.AppendChild(Build(scratch, document.Root, nodes));
        }

        indexedNodes = nodes;
        return scratch;
    }

    /// <summary>Reads the contract-node index of a matched element.</summary>
    /// <param name="element">Matched AngleSharp element.</param>
    /// <returns>The contract-node index, or -1 when absent.</returns>
    public static int IndexOf(IElement element)
    {
        var raw = element.GetAttribute(NodeIndexAttribute);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : -1;
    }

    private static IElement Build(IDocument document, DomNode node, List<DomNode> nodes)
    {
        var index = nodes.Count;
        nodes.Add(node);

        var element = document.CreateElement(node.TagName);
        element.SetAttribute(NodeIndexAttribute, index.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, value) in node.Attributes)
        {
            element.SetAttribute(name, value);
        }

        if (!string.IsNullOrEmpty(node.TextContent))
        {
            element.AppendChild(document.CreateTextNode(node.TextContent));
        }

        foreach (var child in node.Children)
        {
            element.AppendChild(Build(document, child, nodes));
        }

        return element;
    }
}
