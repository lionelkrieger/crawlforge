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

    // ── WP-10-A1 (D-021): phase-09 builder-local keys (WIRING-NOTES gap 6) ────
    // Stable strings promoted verbatim from the phase-09 builder folders so the
    // finding catalogue, suppressions and exports reference one vocabulary.
    // Values are frozen: they equal the builder-local constants byte for byte.

    /// <summary>A crawler-trap pattern was detected, throttled and reported
    /// (FS-CRAWL-001 §5.14; Crawler CrawlerTrapRuleKeys.TrapDetected).</summary>
    public const string CrawlerTrapDetected = "crawler-trap.detected";

    /// <summary>Notice: an ASP.NET cookieless session segment was removed during
    /// canonicalization (CR-009; Crawler CrawlFindingRuleKeys.SessionSegmentRemoved).</summary>
    public const string CanonicalizationSessionSegmentRemoved = "canonicalization.session-segment-removed";

    /// <summary>Warning: a cookieless crawl hit a redirect loop that a cookie jar
    /// would have resolved (Crawler CrawlFindingRuleKeys.CookielessRedirectLoop).</summary>
    public const string CrawlCookielessRedirectLoop = "crawl.cookieless-redirect-loop";

    /// <summary>A URL was blocked from fetching by robots.txt. Shared stable key
    /// of crawl-time emission (Crawler CrawlFindingRuleKeys.RobotsBlocked) and the
    /// offline indexability analyzer (Audit IndexabilityRuleKeys.RobotsBlocked).</summary>
    public const string IndexabilityRobotsBlocked = "indexability.robots-blocked";

    /// <summary>Warning: material crawler-vs-browser differences (possible cloaking,
    /// dynamic rendering or bot blocking; FS-CRAWL-001 §5.15;
    /// Crawler CrawlFindingRuleKeys.UaComparisonMaterialDifference).</summary>
    public const string UaComparisonMaterialDifference = "ua-comparison.material-difference";

    /// <summary>A redirect chain revisits a URL (loop). Default severity: Error
    /// (Crawler RedirectRuleKeys.Loop).</summary>
    public const string RedirectsLoop = "redirects.loop";

    /// <summary>A redirect matches an inappropriate-redirect check (CAP-B-051).
    /// Generic companion key kept for the phase-09 provisional emission; the four
    /// CR-029 case keys below name the specific case (Crawler RedirectRuleKeys.Inappropriate).</summary>
    public const string RedirectsInappropriate = "redirects.inappropriate";

    /// <summary>A multi-hop redirect chain (2+ hops) recorded hop by hop;
    /// chain-cap violations reuse this key with Error severity
    /// (Crawler RedirectRuleKeys.Chain).</summary>
    public const string RedirectsChain = "redirects.chain";

    /// <summary>Two or more pages share an identical title
    /// (Audit ContentQualityRuleKeys.DuplicateTitle).</summary>
    public const string ContentDuplicateTitle = "content.duplicate-title";

    /// <summary>Two or more pages share an identical meta description
    /// (Audit ContentQualityRuleKeys.DuplicateDescription).</summary>
    public const string ContentDuplicateDescription = "content.duplicate-description";

    /// <summary>Exact duplicate visible body content across pages (normalized-text
    /// SHA-256 equality; Audit ContentQualityRuleKeys.DuplicateContent).</summary>
    public const string ContentDuplicateContent = "content.duplicate-content";

    /// <summary>A URL carries session parameters (Audit UrlStructureRuleKeys.SessionParameters).</summary>
    public const string UrlSessionParameters = "url.session-parameters";

    /// <summary>A URL carries tracking parameters (Audit UrlStructureRuleKeys.TrackingParameters).</summary>
    public const string UrlTrackingParameters = "url.tracking-parameters";

    /// <summary>Inconsistent trailing-slash usage across the URL set
    /// (Audit UrlStructureRuleKeys.TrailingSlashInconsistency).</summary>
    public const string UrlTrailingSlashInconsistency = "url.trailing-slash-inconsistency";

    /// <summary>Distinct URLs resolve to duplicate content addresses
    /// (Audit UrlStructureRuleKeys.DuplicateUrls).</summary>
    public const string UrlDuplicateUrls = "url.duplicate-urls";

    /// <summary>An image's transferred payload exceeds the configured byte threshold
    /// (FS-CRAWL-001 §7.4 Images row; Audit ImageRuleKeys.Oversized).</summary>
    public const string ImageOversized = "image.oversized";

    /// <summary>An image uses a non-web or legacy format
    /// (FS-CRAWL-001 §7.4 Images row; Audit ImageRuleKeys.Format).</summary>
    public const string ImageFormat = "image.format";

    /// <summary>Notice: a typed structured-data block is missing recommended
    /// (advisory) vocabulary properties (CAP-B2-076;
    /// Audit StructuredDataRuleKeys.StructuredDataMissingRecommended).</summary>
    public const string StructuredDataMissingRecommended = "structured-data.missing-recommended";

    // ── WP-10-A1 (D-021): CR-029 inappropriate-redirect cases ─────────────────
    // Four independently toggleable cases, all on by default (CR-029, applied;
    // FS-CRAWL-001 §7.4 Indexability & status row). Each finding names the case,
    // the triggering hop and the final target. Case suffixes match the phase-09
    // evaluator's check names (cross-host, temporary-status) extended with the
    // two CR-029 additions.

    /// <summary>Inappropriate redirect: a hop targets a different host
    /// (CR-029 case 1). Toggle: redirectChecks.crossHost.</summary>
    public const string RedirectsInappropriateCrossHost = "redirects.inappropriate-cross-host";

    /// <summary>Inappropriate redirect: a hop uses a temporary status 302/303/307
    /// (CR-029 case 2). Toggle: redirectChecks.temporaryStatus.</summary>
    public const string RedirectsInappropriateTemporaryStatus = "redirects.inappropriate-temporary-status";

    /// <summary>Inappropriate redirect: the final target is non-indexable per
    /// CR-024 (CR-029 case 3). Toggle: redirectChecks.nonIndexableTarget.</summary>
    public const string RedirectsInappropriateNonIndexableTarget = "redirects.inappropriate-non-indexable-target";

    /// <summary>Inappropriate redirect: an HTTPS URL redirects to HTTP
    /// (CR-029 case 4). Toggle: redirectChecks.httpsDowngrade.</summary>
    public const string RedirectsInappropriateHttpsDowngrade = "redirects.inappropriate-https-downgrade";

    // ── WP-10-A1 (D-021): CR-030 deprecated structured-data terms ─────────────

    /// <summary>A structured-data term is deprecated: superseded in the bundled
    /// vocabulary or on the bundled retired/restricted rich-result list (CR-030,
    /// applied; FS-CRAWL-001 §7.13). The finding names the term, the reason and
    /// the replacement where one exists.</summary>
    public const string StructuredDataDeprecatedTerm = "structured-data.deprecated-term";

    // ── WP-10-A1 (D-021): phase-10 capture-mode findings ──────────────────────

    /// <summary>Adaptive capture switched a URL-pattern group to rendered capture
    /// because rendering materially changed the sample — the "JavaScript-dependent
    /// template" finding (FS-CRAWL-001 §5.6.1). Evidence carries the sample URLs
    /// and the material-change facts. The key lives in the content.* family
    /// (ratified acceptance-harness measurement vocabulary, WP-10-A1).</summary>
    public const string ContentJavascriptDependentTemplate = "content.javascript-dependent-template";

    // ── WP-10-A1 (D-021): response-vs-rendered comparison (§7.18) ─────────────
    // The seven delta keys are promoted verbatim from the phase-09
    // ResponseRenderedComparer (Audit IndexabilityRuleKeys.Render*Delta); the
    // canonical / meta-robots / noindex deltas complete the §7.18 high-severity
    // directive comparisons. A noindex present in only one capture is critical
    // (FindingSeverity.Error, the top of the locked severity vocabulary).

    /// <summary>Status differs between the raw fetch and the final rendered outcome.</summary>
    public const string RenderStatusDelta = "render.status-delta";

    /// <summary>The title differs between raw response and rendered document
    /// (high severity per §7.18).</summary>
    public const string RenderTitleDelta = "render.title-delta";

    /// <summary>Metadata was altered by script between response and render.</summary>
    public const string RenderMetadataDelta = "render.metadata-delta";

    /// <summary>Content differs between raw response and rendered document.</summary>
    public const string RenderTextDelta = "render.text-delta";

    /// <summary>Links are present only after rendering.</summary>
    public const string RenderLinksDelta = "render.links-delta";

    /// <summary>Structured data differs between response and render.</summary>
    public const string RenderStructuredDataDelta = "render.structured-data-delta";

    /// <summary>Resource sets differ between response and render.</summary>
    public const string RenderResourcesDelta = "render.resources-delta";

    /// <summary>The canonical directive differs between raw response and rendered
    /// document (high severity per §7.18).</summary>
    public const string RenderCanonicalDelta = "render.canonical-delta";

    /// <summary>The meta-robots directives differ between raw response and
    /// rendered document (high severity per §7.18).</summary>
    public const string RenderMetaRobotsDelta = "render.meta-robots-delta";

    /// <summary>A noindex directive exists in only one capture (rendered-only or
    /// raw-only). Critical: emit with FindingSeverity.Error (§7.18).</summary>
    public const string RenderNoindexDelta = "render.noindex-delta";

    // ── WP-10-A1 (D-021): site-type / rendering informational findings ────────

    /// <summary>Notice: a service worker registration was observed; every render
    /// bypasses it (FS-CRAWL-001 §7.19 site-type rules; fixture trap service-worker).</summary>
    public const string RenderServiceWorkerRegistered = "render.service-worker-registered";
}
