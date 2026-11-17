using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.StructuredData;

/// <summary>One page's hreflang set for site-wide validation.</summary>
/// <param name="Url">The page's canonical URL.</param>
/// <param name="Annotations">All hreflang annotations observed on the page (HTML + headers).</param>
public sealed record PageHreflangSet(string Url, IReadOnlyList<HreflangAnnotation> Annotations);

/// <summary>
/// Site-wide hreflang reciprocity validation (FS-CRAWL-001 §7.13): when page A
/// declares page B as a language alternate, page B must declare page A back.
/// Runs fully offline over collected per-page sets (re-runnable from stored
/// bodies/DOM on re-audit); targets outside the collected set are skipped —
/// no remote fetch is ever performed (exclusions E2/E4).
/// </summary>
public sealed class HreflangReciprocityValidator
{
    /// <summary>
    /// Validates reciprocity across the collected page sets. Findings are
    /// site-wide (url_id null) with page/target URLs in evidence, because the
    /// validator operates over URL-keyed sets rather than persisted URL rows.
    /// </summary>
    /// <param name="pages">Per-page hreflang sets collected during the audit.</param>
    /// <param name="auditId">Owning audit reference (evidence only).</param>
    /// <returns>Missing-reciprocity findings in deterministic page order.</returns>
    public IReadOnlyList<FindingDraft> Validate(IReadOnlyList<PageHreflangSet> pages, long auditId)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var findings = new List<FindingDraft>();
        var byUrl = new Dictionary<string, PageHreflangSet>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            var normalized = HreflangCollector.NormalizeUrl(page.Url, page.Url);
            byUrl.TryAdd(normalized, page);
        }

        foreach (var page in pages)
        {
            var sourceNormalized = HreflangCollector.NormalizeUrl(page.Url, page.Url);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var annotation in page.Annotations)
            {
                if (string.Equals(annotation.Hreflang, HreflangCodes.XDefault, StringComparison.OrdinalIgnoreCase))
                {
                    // x-default is a fallback selector, not a language alternate;
                    // reciprocity is not required for it.
                    continue;
                }

                var target = HreflangCollector.NormalizeUrl(annotation.Href, page.Url);
                if (string.Equals(target, sourceNormalized, StringComparison.Ordinal) ||
                    !byUrl.TryGetValue(target, out var targetPage) ||
                    !reported.Add(target))
                {
                    continue;
                }

                var returns = targetPage.Annotations.Any(a =>
                    string.Equals(HreflangCollector.NormalizeUrl(a.Href, targetPage.Url), sourceNormalized, StringComparison.Ordinal));
                if (!returns)
                {
                    findings.Add(new FindingDraft(
                        StructuredDataRuleKeys.HreflangMissingReciprocity,
                        AuditCategory.StructuredData,
                        FindingSeverity.Warning,
                        null,
                        JsonSerializer.Serialize(new
                        {
                            auditId,
                            pageUrl = sourceNormalized,
                            targetUrl = target,
                            hreflang = annotation.Hreflang,
                            reason = "the alternate page does not declare a return hreflang annotation to this page",
                        }),
                        null));
                }
            }
        }

        return findings;
    }
}
