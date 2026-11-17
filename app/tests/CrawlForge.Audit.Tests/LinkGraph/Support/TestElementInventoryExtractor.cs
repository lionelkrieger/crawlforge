using CrawlForge.Contracts.Audit;

namespace CrawlForge.Audit.Tests.LinkGraph.Support;

/// <summary>
/// Builder-owned test double for the locked <see cref="IElementInventoryExtractor"/>
/// contract (the production extractor belongs to the pipeline workpiece). Returns
/// scripted inventory items and records call counts.
/// </summary>
public sealed class TestElementInventoryExtractor : IElementInventoryExtractor
{
    /// <summary>Inventory items returned by ExtractAsync.</summary>
    public IReadOnlyList<ElementInventoryItem> Items { get; set; } = [];

    /// <summary>Number of ExtractAsync calls received.</summary>
    public int CallCount { get; private set; }

    /// <summary>The document passed to the most recent call.</summary>
    public DomDocument? LastDocument { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ElementInventoryItem>> ExtractAsync(DomDocument document, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastDocument = document;
        return Task.FromResult(Items);
    }
}

/// <summary>
/// Concise builders for the contract DOM model used by zone-classification tests.
/// </summary>
public static class DomFactory
{
    /// <summary>Creates an element node.</summary>
    /// <param name="tag">Lowercase tag name.</param>
    /// <param name="attributes">Attributes, when present.</param>
    /// <param name="children">Child nodes in document order.</param>
    /// <param name="text">Direct text content.</param>
    /// <returns>The node.</returns>
    public static DomNode Element(
        string tag,
        IReadOnlyDictionary<string, string>? attributes = null,
        IReadOnlyList<DomNode>? children = null,
        string text = "") =>
        new(tag, attributes ?? new Dictionary<string, string>(), children ?? [], text);

    /// <summary>Creates an anchor node.</summary>
    /// <param name="href">Href attribute value.</param>
    /// <param name="text">Anchor text.</param>
    /// <param name="rel">Rel attribute value, when present.</param>
    /// <returns>The anchor node.</returns>
    public static DomNode Anchor(string href, string text = "link", string? rel = null)
    {
        var attributes = new Dictionary<string, string> { ["href"] = href };
        if (rel is not null)
        {
            attributes["rel"] = rel;
        }

        return Element("a", attributes, text: text);
    }

    /// <summary>Wraps body children into an html/body document.</summary>
    /// <param name="bodyChildren">Body child nodes.</param>
    /// <returns>The document.</returns>
    public static DomDocument Document(params DomNode[] bodyChildren) =>
        new(
            "https://example.com/page",
            Element("html", children: [Element("body", children: bodyChildren)]),
            []);
}
