using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Internationalization analyzer (FS-CRAWL-001 §7.13): set-level hreflang
/// validation across BOTH directive sources — HTML link elements and HTTP Link
/// headers. Checks language/region code validity, self-reference, duplicates,
/// and conflicting mappings, plus html lang annotation.
/// </summary>
/// <remarks>
/// Boundary: the header-vs-HTML equivalency finding keys
/// (<c>hreflang.header-html-conflict</c>, <c>canonical.header-html-conflict</c>,
/// <c>indexability.header-html-conflict</c>) are exclusively owned by the
/// equivalency analyzer (WP-03-02). Cross-source discrepancies observed during
/// set validation are recorded as evidence inside this analyzer's own
/// <c>i18n.hreflang-conflict</c> findings, never under the equivalency keys.
/// Site-wide reciprocity is validated offline by
/// <see cref="HreflangReciprocityValidator"/>.
/// </remarks>
public sealed class HreflangAnalyzer : II18nAnalyzer
{
    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var findings = new List<FindingDraft>();
        var pageUrl = context.Url.CanonicalUrl;
        var document = context.Document ?? context.RenderedDocument;

        var annotations = new List<HreflangAnnotation>();
        annotations.AddRange(HreflangCollector.FromDocument(document, pageUrl));
        annotations.AddRange(HreflangCollector.FromLinkHeaders(context.Fetch?.Headers, pageUrl));

        ValidateHtmlLang(document, context.Url.Id, findings);

        if (annotations.Count > 0)
        {
            ValidateSet(annotations, pageUrl, context.Url.Id, findings);
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>(findings);
    }

    private static void ValidateSet(List<HreflangAnnotation> annotations, string pageUrl, long urlId, List<FindingDraft> findings)
    {
        var normalizedPage = HreflangCollector.NormalizeUrl(pageUrl, pageUrl);
        var hasSelfReference = false;
        var issuesFound = false;

        // Group by hreflang value preserving first-appearance order.
        var groups = new Dictionary<string, List<HreflangAnnotation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var annotation in annotations)
        {
            if (!HreflangCodes.IsValidHreflang(annotation.Hreflang))
            {
                issuesFound = true;
                findings.Add(Finding(StructuredDataRuleKeys.HreflangInvalidCode, FindingSeverity.Warning, urlId, new
                {
                    hreflang = annotation.Hreflang,
                    href = annotation.Href,
                    source = annotation.Source,
                    reason = "expected a valid ISO 639-1 language tag with optional script/region, or x-default",
                }));
            }

            if (string.Equals(annotation.Href, normalizedPage, StringComparison.Ordinal))
            {
                hasSelfReference = true;
            }

            if (!groups.TryGetValue(annotation.Hreflang, out var group))
            {
                group = [];
                groups[annotation.Hreflang] = group;
            }

            group.Add(annotation);
        }

        foreach (var (hreflang, group) in groups)
        {
            var distinctTargets = group.Select(a => a.Href).Distinct(StringComparer.Ordinal).ToList();
            if (distinctTargets.Count > 1)
            {
                issuesFound = true;
                findings.Add(Finding(StructuredDataRuleKeys.HreflangConflict, FindingSeverity.Error, urlId, new
                {
                    hreflang,
                    targets = group.Select(a => new { href = a.Href, source = a.Source }).ToList(),
                    reason = "one hreflang value maps to multiple target URLs across sources",
                }));
            }
            else if (group.Count > 1)
            {
                issuesFound = true;
                findings.Add(Finding(StructuredDataRuleKeys.HreflangDuplicate, FindingSeverity.Warning, urlId, new
                {
                    hreflang,
                    href = distinctTargets[0],
                    occurrences = group.Count,
                    sources = group.Select(a => a.Source).ToList(),
                }));
            }
        }

        if (!hasSelfReference)
        {
            issuesFound = true;
            findings.Add(Finding(StructuredDataRuleKeys.HreflangMissingSelfReference, FindingSeverity.Warning, urlId, new
            {
                pageUrl = normalizedPage,
                declaredTargets = annotations.Select(a => a.Href).Distinct(StringComparer.Ordinal).ToList(),
                reason = "the hreflang set must include a self-referencing annotation for this page",
            }));
        }

        if (!issuesFound)
        {
            findings.Add(Finding(StructuredDataRuleKeys.HreflangValid, FindingSeverity.Pass, urlId, new
            {
                pageUrl = normalizedPage,
                annotationCount = annotations.Count,
                languages = groups.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            }));
        }
    }

    private static void ValidateHtmlLang(DomDocument? document, long urlId, List<FindingDraft> findings)
    {
        if (document is null)
        {
            return;
        }

        var html = Walk(document.Root).FirstOrDefault(n => n.TagName == "html");
        if (html is null)
        {
            return;
        }

        if (!html.Attributes.TryGetValue("lang", out var lang) || string.IsNullOrWhiteSpace(lang))
        {
            findings.Add(Finding(StructuredDataRuleKeys.HtmlLangMissing, FindingSeverity.Warning, urlId, new
            {
                reason = "the html element has no lang attribute",
            }));
        }
        else if (!HreflangCodes.IsValidLanguageTag(lang))
        {
            findings.Add(Finding(StructuredDataRuleKeys.HtmlLangInvalid, FindingSeverity.Warning, urlId, new
            {
                lang,
                reason = "expected a valid ISO 639-1 language tag with optional region",
            }));
        }
    }

    // D-021 (phase 10): hreflang/html-lang findings belong to the §7.4
    // Internationalization taxonomy row — the locked AuditCategory member
    // added by WP-10-A1 (wire "internationalization").
    private static FindingDraft Finding(string ruleKey, FindingSeverity severity, long urlId, object evidence) =>
        new(ruleKey, AuditCategory.Internationalization, severity, urlId, JsonSerializer.Serialize(evidence), null);

    private static IEnumerable<DomNode> Walk(DomNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
}
