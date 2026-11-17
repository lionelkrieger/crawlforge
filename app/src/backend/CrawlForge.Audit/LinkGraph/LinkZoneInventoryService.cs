using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.LinkGraph;

/// <summary>
/// One zone-classified link extracted from a document, ready for link_edges
/// persistence (canonicalization and URL resolution happen in the crawl pipeline).
/// </summary>
/// <param name="Href">Raw href attribute value.</param>
/// <param name="AnchorText">Visible anchor text.</param>
/// <param name="RelationFlags">Parsed rel flags.</param>
/// <param name="Zone">Classified DOM zone.</param>
/// <param name="ZoneEvidence">Evidence supporting the classification.</param>
public sealed record ZonedLink(
    string Href,
    string AnchorText,
    LinkRelationFlags RelationFlags,
    DomZone Zone,
    string ZoneEvidence);

/// <summary>
/// Combined output of zone-aware link inventory: the classified links plus the
/// element inventory produced by the pipeline extractor (pass-through for
/// url_elements persistence).
/// </summary>
/// <param name="Links">Zone-classified links in document order.</param>
/// <param name="Inventory">Element inventory items from the pipeline extractor.</param>
public sealed record ZonedLinkInventory(
    IReadOnlyList<ZonedLink> Links,
    IReadOnlyList<ElementInventoryItem> Inventory);

/// <summary>
/// Zone-aware link inventory service (FS-CRAWL-002 §15.1: classification runs at
/// link-inventory time). Consumes the locked <see cref="IElementInventoryExtractor"/>
/// contract for the element inventory (implemented by the pipeline workpiece;
/// constructor-injected, doubled in tests) and the locked
/// <see cref="ILinkZoneClassifier"/> for per-anchor zone classification with
/// evidence. Anchors are walked iteratively in document order.
/// </summary>
public sealed class LinkZoneInventoryService
{
    private readonly ILinkZoneClassifier _classifier;
    private readonly IElementInventoryExtractor _inventoryExtractor;

    /// <summary>Creates the service.</summary>
    /// <param name="classifier">The DOM-zone classifier.</param>
    /// <param name="inventoryExtractor">The pipeline element inventory extractor.</param>
    public LinkZoneInventoryService(ILinkZoneClassifier classifier, IElementInventoryExtractor inventoryExtractor)
    {
        _classifier = classifier;
        _inventoryExtractor = inventoryExtractor;
    }

    /// <summary>
    /// Extracts the element inventory and classifies every anchor in the document.
    /// </summary>
    /// <param name="document">Parsed document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Zone-classified links plus the pass-through inventory.</returns>
    public async Task<ZonedLinkInventory> ExtractZonedLinksAsync(DomDocument document, CancellationToken cancellationToken = default)
    {
        var inventory = await _inventoryExtractor.ExtractAsync(document, cancellationToken);

        var links = new List<ZonedLink>();
        var stack = new Stack<DomNode>();
        stack.Push(document.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.TagName == "a" && node.Attributes.TryGetValue("href", out var href))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var classification = _classifier.Classify(document, node);
                links.Add(new ZonedLink(
                    href,
                    node.TextContent,
                    ParseRelationFlags(node.Attributes.TryGetValue("rel", out var rel) ? rel : null),
                    classification.Zone,
                    classification.Evidence));
            }

            // Push children in reverse so document order is preserved on pop.
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return new ZonedLinkInventory(links, inventory);
    }

    /// <summary>Parses a rel attribute into composable relation flags.</summary>
    /// <param name="rel">The rel attribute value, when present.</param>
    /// <returns>The observed flags.</returns>
    public static LinkRelationFlags ParseRelationFlags(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel))
        {
            return LinkRelationFlags.None;
        }

        var flags = LinkRelationFlags.None;
        foreach (var token in rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            flags |= token.ToLowerInvariant() switch
            {
                "nofollow" => LinkRelationFlags.Nofollow,
                "noopener" => LinkRelationFlags.Noopener,
                "noreferrer" => LinkRelationFlags.Noreferrer,
                "ugc" => LinkRelationFlags.Ugc,
                "sponsored" => LinkRelationFlags.Sponsored,
                "alternate" => LinkRelationFlags.Alternate,
                "canonical" => LinkRelationFlags.Canonical,
                _ => LinkRelationFlags.None
            };
        }

        return flags;
    }
}
