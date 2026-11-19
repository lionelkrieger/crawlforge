using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;
using CrawlForge.Crawler.Canonicalization;

namespace CrawlForge.Crawler.Politeness;

/// <summary>
/// Reports the cookieless redirect-loop case of the <c>cookie-gate</c> fixture
/// trap (FS-CRAWL-001 §5.11): when the jar is off, a site that gates content
/// behind a Set-Cookie + self-redirect loops until the redirect hop ceiling.
/// This diagnostic turns that loop into an actionable finding — the loop URL,
/// the hop chain, and the cookie names the site tried to set — instead of a
/// bare transport failure. Detection and reporting only.
/// </summary>
public static class CookielessRedirectLoopDiagnostic
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Finds the first URL that repeats in a redirect chain (the loop point).
    /// </summary>
    /// <param name="chain">Recorded redirect hops in order.</param>
    /// <param name="loopUrl">The repeating URL, when a loop exists.</param>
    /// <returns>True when the chain loops.</returns>
    public static bool TryFindLoop(IReadOnlyList<RedirectHop> chain, out string? loopUrl)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hop in chain)
        {
            if (!seen.Add(hop.FromUrl))
            {
                loopUrl = hop.FromUrl;
                return true;
            }
        }

        loopUrl = null;
        return false;
    }

    /// <summary>
    /// Creates the cookieless redirect-loop finding when the jar was off, the
    /// chain loops, and the site attempted to set cookies (the loop a jar would
    /// have resolved); otherwise null.
    /// </summary>
    /// <param name="urlId">URL record of the looping URL.</param>
    /// <param name="url">The requested URL.</param>
    /// <param name="chain">Recorded redirect hops.</param>
    /// <param name="observedSetCookieNames">Cookie names observed in Set-Cookie headers along the chain.</param>
    /// <param name="cookieJarEnabled">Whether the per-audit jar was enabled.</param>
    /// <returns>A Warning finding draft, or null when the case does not apply.</returns>
    public static FindingDraft? Create(
        long urlId,
        string url,
        IReadOnlyList<RedirectHop> chain,
        IReadOnlyList<string> observedSetCookieNames,
        bool cookieJarEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(observedSetCookieNames);
        if (cookieJarEnabled || observedSetCookieNames.Count == 0 || !TryFindLoop(chain, out var loopUrl))
        {
            return null;
        }

        var evidence = JsonSerializer.Serialize(new
        {
            url,
            loopUrl,
            cookieJarEnabled,
            setCookieNames = observedSetCookieNames,
            hops = chain.Select(h => new { from = h.FromUrl, to = h.ToUrl, status = h.StatusCode }),
            guidance = "Enable the per-audit cookie jar (default) so the site's gate cookie is retained across the redirect.",
        }, EvidenceOptions);

        return new FindingDraft(
            CrawlFindingRuleKeys.CookielessRedirectLoop,
            AuditCategory.Indexability,
            FindingSeverity.Warning,
            urlId,
            evidence,
            RecommendationKey: null);
    }
}
