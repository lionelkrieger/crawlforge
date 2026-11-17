using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.MarTech;

/// <summary>
/// Resolves the site's primary container for the analytics.missing-container
/// rule (FS-CRAWL-002 §14.3): the most frequent container ID across
/// indexable pages. Additive public surface — the pipeline/phase-05
/// aggregates detections audit-wide and feeds the result back through the
/// auditor constructor or the user override configuration field.
/// </summary>
public static class PrimaryContainerResolver
{
    /// <summary>
    /// Picks the most frequent non-null container ID (ordinal tie-break for
    /// determinism).
    /// </summary>
    /// <param name="detections">Detections across indexable pages.</param>
    /// <returns>The primary container ID, or null when none observed.</returns>
    public static string? ResolvePrimary(IEnumerable<AnalyticsDetectionDraft> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);
        return detections
            .Where(detection => !string.IsNullOrWhiteSpace(detection.ContainerId))
            .GroupBy(detection => detection.ContainerId!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
    }
}
