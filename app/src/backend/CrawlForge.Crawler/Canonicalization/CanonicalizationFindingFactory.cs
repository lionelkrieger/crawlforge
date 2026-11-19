using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Canonicalization;

/// <summary>
/// Produces reportable findings for material canonicalization changes
/// (FS-CRAWL-001 §5.2 as amended by CR-009). The fixture trap
/// <c>cookieless-session</c> requires the removed session segment to be
/// reported, not silently dropped: the engine calls this factory with the
/// <see cref="UrlCanonicalizer.CanonicalizationOutcome"/> of each discovered
/// URL and persists the returned draft through the finding emitter.
/// </summary>
public static class CanonicalizationFindingFactory
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the session-segment-removal notice when canonicalization removed
    /// at least one cookieless session segment; otherwise null (no finding).
    /// </summary>
    /// <param name="urlId">URL record the canonicalization belongs to.</param>
    /// <param name="outcome">The canonicalization outcome.</param>
    /// <returns>A Notice finding draft, or null when nothing was removed.</returns>
    public static FindingDraft? CreateSessionSegmentFinding(long urlId, UrlCanonicalizer.CanonicalizationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RemovedSessionSegments.Count == 0)
        {
            return null;
        }

        var evidence = JsonSerializer.Serialize(new
        {
            originalUrl = outcome.InputUrl,
            canonicalUrl = outcome.CanonicalUrl,
            removedSegments = outcome.RemovedSessionSegments,
        }, EvidenceOptions);

        return new FindingDraft(
            CrawlFindingRuleKeys.SessionSegmentRemoved,
            AuditCategory.Indexability,
            FindingSeverity.Notice,
            urlId,
            evidence,
            RecommendationKey: null);
    }
}
