using System.Text.Json;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Fetching;

namespace CrawlForge.Crawler.Sitemaps;

/// <summary>
/// Sitemap-vs-crawl correlation (CAP-B-052/053/054): compares the sitemap URL
/// set against crawl facts and emits findings for missing, non-indexable, and
/// redirecting listed URLs. Pure component: facts in, <see cref="FindingDto"/>
/// drafts out (Id 0); the engine persists them (WP-09-11 wiring).
/// </summary>
/// <remarks>
/// <para>
/// Non-indexable uses CR-024 SEARCH-indexability semantics: final HTTP 200,
/// robots-allowed, no noindex directive (meta robots or X-Robots-Tag, both
/// carried by <see cref="IndexabilityFlags.Noindex"/>), and a canonical that
/// is self-referential or absent.
/// </para>
/// <para>
/// Finding precedence per listed URL: missing (not crawled) → redirecting
/// (chain observed) → non-indexable. A redirecting URL is reported under the
/// more specific <c>sitemap.redirecting</c> rule only, not double-flagged as
/// non-indexable (its 3xx source status is implied by the redirect finding).
/// </para>
/// <para>
/// Stateless; safe as a singleton and under concurrent analyzer execution.
/// </para>
/// </remarks>
public sealed class SitemapCorrelationAnalyzer
{
    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Correlates sitemap-listed URLs against crawl facts.
    /// </summary>
    /// <param name="auditId">Owning audit identifier.</param>
    /// <param name="sitemapCanonicalUrls">
    /// Canonical sitemap URL keys (from <see cref="SitemapIngestionResult.Urls"/>
    /// <c>CanonicalUrl</c>); duplicates are ignored.
    /// </param>
    /// <param name="facts">Crawl facts keyed by canonical URL (one per crawled URL).</param>
    /// <returns>Finding drafts in sitemap declaration order.</returns>
    public IReadOnlyList<FindingDto> Analyze(
        long auditId,
        IReadOnlyList<string> sitemapCanonicalUrls,
        IReadOnlyList<SitemapUrlCrawlFact> facts)
    {
        ArgumentNullException.ThrowIfNull(sitemapCanonicalUrls);
        ArgumentNullException.ThrowIfNull(facts);

        var factByKey = new Dictionary<string, SitemapUrlCrawlFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            factByKey.TryAdd(fact.CanonicalUrl, fact);
        }

        var findings = new List<FindingDto>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in sitemapCanonicalUrls)
        {
            if (!visited.Add(url))
            {
                continue;
            }

            if (!factByKey.TryGetValue(url, out var fact) || !fact.WasCrawled)
            {
                findings.Add(Finding(auditId, SitemapRuleKeys.MissingUrl, urlId: null, JsonSerializer.Serialize(new
                {
                    url,
                    reason = "not-crawled",
                }, EvidenceJson)));
                continue;
            }

            if (fact.Chain.Count > 0 || fact.StatusClass == CrawlStatusClass.Redirect)
            {
                findings.Add(Finding(auditId, SitemapRuleKeys.Redirecting, fact.UrlId, JsonSerializer.Serialize(new
                {
                    url,
                    urlId = fact.UrlId,
                    hopCount = fact.Chain.Count,
                    chain = JsonSerializer.Deserialize<JsonElement>(RedirectChainJson.SerializeHops(fact.Chain)),
                    finalUrl = fact.Chain.Count > 0 ? fact.Chain[^1].ToUrl : null,
                    finalStatusCode = fact.FinalStatusCode,
                }, EvidenceJson)));
                continue;
            }

            var reasons = NonIndexableReasons(fact);
            if (reasons.Count > 0)
            {
                findings.Add(Finding(auditId, SitemapRuleKeys.NonIndexable, fact.UrlId, JsonSerializer.Serialize(new
                {
                    url,
                    urlId = fact.UrlId,
                    reasons,
                    finalStatusCode = fact.FinalStatusCode,
                    robotsState = fact.RobotsState.ToString(),
                    indexabilityFlags = (int)fact.IndexabilityFlags,
                    canonicalTarget = fact.CanonicalTargetUrl,
                }, EvidenceJson)));
            }
        }

        return findings;
    }

    /// <summary>
    /// CR-024 search-indexability evaluation: final 200, robots-allowed, no
    /// noindex, canonical self-or-absent. Exposed for reuse by wiring/tests.
    /// </summary>
    /// <param name="fact">Crawl facts of one URL.</param>
    /// <returns>True when the URL is search-indexable.</returns>
    public static bool IsSearchIndexable(SitemapUrlCrawlFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return NonIndexableReasons(fact).Count == 0;
    }

    private static List<string> NonIndexableReasons(SitemapUrlCrawlFact fact)
    {
        var reasons = new List<string>();
        if (fact.FinalStatusCode != 200)
        {
            reasons.Add("non-200-status");
        }

        if (fact.RobotsState == RobotsState.Disallowed)
        {
            reasons.Add("robots-disallowed");
        }

        if (fact.IndexabilityFlags.HasFlag(IndexabilityFlags.Noindex))
        {
            reasons.Add("noindex");
        }

        if (fact.CanonicalTargetUrl is not null
            && !string.Equals(fact.CanonicalTargetUrl, fact.CanonicalUrl, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("canonical-mismatch");
        }

        return reasons;
    }

    private static FindingDto Finding(long auditId, string ruleKey, long? urlId, string evidenceJson) =>
        new(
            Id: 0,
            AuditId: auditId,
            RuleKey: ruleKey,
            Category: AuditCategory.Indexability,
            Severity: FindingSeverity.Warning,
            UrlId: urlId,
            EvidenceJson: evidenceJson,
            RecommendationKey: null,
            IsSuppressed: false);
}
