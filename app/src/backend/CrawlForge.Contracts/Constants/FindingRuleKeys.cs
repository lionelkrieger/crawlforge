namespace CrawlForge.Contracts.Constants;

/// <summary>
/// Finding rule keys introduced by the extension specification
/// (FS-CRAWL-002 Appendix A, findings row). Rule keys are stable strings;
/// additive keys may be appended, existing keys are never repurposed.
/// </summary>
public static class FindingRuleKeys
{
    /// <summary>A WAF challenge page was detected on a response.</summary>
    public const string WafChallengeDetected = "waf-challenge-detected";

    /// <summary>Site-wide WAF block rate exceeded the reporting threshold.</summary>
    public const string WafBlockRate = "waf-block-rate";

    /// <summary>Notice annotation: a challenge was resolved by rendering.</summary>
    public const string ChallengeResolvedByRender = "challenge-resolved-by-render";

    /// <summary>Notice annotation: a response is a suspected challenge.</summary>
    public const string SuspectedChallenge = "suspected-challenge";

    /// <summary>Keyword cannibalization across pages (n-gram analysis).</summary>
    public const string ContentKeywordCannibalization = "content.keyword-cannibalization";

    /// <summary>Topical gap detected by the n-gram analyzer.</summary>
    public const string ContentTopicalGap = "content.topical-gap";

    /// <summary>Redirect-matrix row passed.</summary>
    public const string RedirectMatrixPass = "redirect-matrix.pass";

    /// <summary>Redirect-matrix row final target mismatch.</summary>
    public const string RedirectMatrixTargetMismatch = "redirect-matrix.target-mismatch";

    /// <summary>Redirect-matrix row chain exceeded the hop limit.</summary>
    public const string RedirectMatrixChainExceeded = "redirect-matrix.chain-exceeded";

    /// <summary>Redirect-matrix row final status was not 200.</summary>
    public const string RedirectMatrixFinalNon200 = "redirect-matrix.final-non-200";

    /// <summary>Redirect-matrix row canonical mismatch.</summary>
    public const string RedirectMatrixCanonicalMismatch = "redirect-matrix.canonical-mismatch";

    /// <summary>True orphan: indexable URL hit by bots but absent from the crawl graph.</summary>
    public const string LogsTrueOrphan = "logs.true-orphan";

    /// <summary>Crawl budget wasted on non-indexable URLs according to server logs.</summary>
    public const string LogsCrawlBudgetWaste = "logs.crawl-budget-waste";

    /// <summary>Indexable URL hit by search bots but never crawled.</summary>
    public const string LogsUncrawledIndexable = "logs.uncrawled-indexable";

    /// <summary>Semantic cannibalization detected by local embeddings.</summary>
    public const string ContentSemanticCannibalization = "content.semantic-cannibalization";

    /// <summary>Internal link whose relevance is below the semantic threshold.</summary>
    public const string LinkingLowRelevanceLink = "linking.low-relevance-link";

    /// <summary>llms.txt is missing for a host.</summary>
    public const string GeoLlmsTxtMissing = "geo.llms-txt-missing";

    /// <summary>llms.txt is malformed.</summary>
    public const string GeoLlmsTxtMalformed = "geo.llms-txt-malformed";

    /// <summary>llms.txt contains a broken link.</summary>
    public const string GeoLlmsTxtBrokenLink = "geo.llms-txt-broken-link";

    /// <summary>Conflicting agent-governance directives detected.</summary>
    public const string GeoGovernanceConflict = "geo.governance-conflict";

    /// <summary>Expected analytics container is missing.</summary>
    public const string AnalyticsMissingContainer = "analytics.missing-container";

    /// <summary>Analytics container is duplicated.</summary>
    public const string AnalyticsDuplicateContainer = "analytics.duplicate-container";

    /// <summary>dataLayer payload is malformed.</summary>
    public const string AnalyticsDataLayerMalformed = "analytics.datalayer-malformed";

    /// <summary>Consent Mode v2 is missing.</summary>
    public const string AnalyticsConsentModeMissing = "analytics.consent-mode-missing";

    /// <summary>No analytics hits were observed.</summary>
    public const string AnalyticsNoHitsObserved = "analytics.no-hits-observed";

    /// <summary>Title exceeds the SERP pixel-width budget.</summary>
    public const string TitlePixelTruncation = "title-pixel-truncation";

    /// <summary>Meta description exceeds the SERP pixel-width budget.</summary>
    public const string DescriptionPixelTruncation = "description-pixel-truncation";

    /// <summary>Indexability directive conflict between HTTP header and HTML.</summary>
    public const string IndexabilityHeaderHtmlConflict = "indexability.header-html-conflict";

    /// <summary>Canonical directive conflict between HTTP header and HTML.</summary>
    public const string CanonicalHeaderHtmlConflict = "canonical.header-html-conflict";

    /// <summary>hreflang directive conflict between HTTP header and HTML.</summary>
    public const string HreflangHeaderHtmlConflict = "hreflang.header-html-conflict";
}
