using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Audit;

/// <summary>
/// DOM-zone link classifier (FS-CRAWL-002 §15): classifies each link into one
/// of the six DOM zones with zone evidence (additive link_edges.dom_zone).
/// </summary>
public interface ILinkZoneClassifier
{
    /// <summary>Classifies the DOM zone of an anchor node.</summary>
    /// <param name="document">Parsed document containing the anchor.</param>
    /// <param name="anchorNode">Anchor node to classify.</param>
    /// <returns>Zone classification with evidence.</returns>
    LinkZoneResult Classify(DomDocument document, DomNode anchorNode);
}

/// <summary>
/// Link zone classification result.
/// </summary>
/// <param name="Zone">Classified DOM zone.</param>
/// <param name="Evidence">Evidence supporting the classification.</param>
public sealed record LinkZoneResult(DomZone Zone, string Evidence);

/// <summary>
/// Weighted link-equity calculator (FS-CRAWL-002 §15): weighted PageRank over
/// the internal link graph using per-zone weights.
/// </summary>
public interface IWeightedLinkEquityCalculator
{
    /// <summary>Computes weighted link equity for every URL of an audit.</summary>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="zoneWeights">Per-zone weight multipliers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Equity scores per URL.</returns>
    Task<IReadOnlyList<LinkEquityScore>> ComputeAsync(
        long auditId,
        IReadOnlyDictionary<DomZone, double> zoneWeights,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Weighted link equity of one URL.
/// </summary>
/// <param name="UrlId">URL record identifier.</param>
/// <param name="Equity">Weighted equity score.</param>
public sealed record LinkEquityScore(long UrlId, double Equity);
