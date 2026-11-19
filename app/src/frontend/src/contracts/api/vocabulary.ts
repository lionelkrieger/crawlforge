/**
 * Enum and rule-key vocabulary mirrors (amendment WP-10-A1, D-021).
 *
 * Frozen declarative catalogs mirroring the locked backend contracts
 * (CrawlForge.Contracts): AuditCategory, WafChallengeClass, IndexabilityFlags,
 * DiscoverySource, CaptureMode, PatternCaptureMode, CaptureSource and the
 * FindingRuleKeys constants. Wire names follow the Api lowercase convention
 * with the locked hyphenated exceptions (catch-up precedent):
 * managed-challenge, rate-limit-soft-block, non-js, non-rendered.
 *
 * The C# EnumVocabularyTests / FrontendMirrorParityTests assert byte-for-byte
 * parity between these catalogs and the backend assembly — extend both sides
 * together, never weaken either lock.
 *
 * Spec surface: FS-CRAWL-001 §5.6.1, §7.4, §7.18, §7.19; FS-CRAWL-002 §2.3.1;
 * CR-024, CR-029, CR-030 (all applied, D-020/D-021).
 *
 * @module contracts/api/vocabulary
 */

// ── AuditCategory (findings.category; scoring categories) ───────────────────

/** AuditCategory CLR member names (enabledCategories / scoring snapshot keys). */
export const AUDIT_CATEGORY_MEMBERS = [
  'Indexability',
  'Content',
  'Linking',
  'StructuredData',
  'Accessibility',
  'Performance',
  'Crawl',
  'Custom',
  'LogIntelligence',
  'AiSearchGeo',
  'AnalyticsTagging',
  'Images',
  'SecurityAndProtocol',
  'Internationalization'
] as const;

/** AuditCategory member name (PascalCase; the enabledCategories convention). */
export type AuditCategoryMember = (typeof AUDIT_CATEGORY_MEMBERS)[number];

/** AuditCategory REST wire values (lowercase; ApiEnumConverter convention). */
export const AUDIT_CATEGORY_WIRE_VALUES = [
  'indexability',
  'content',
  'linking',
  'structureddata',
  'accessibility',
  'performance',
  'crawl',
  'custom',
  'logintelligence',
  'aisearchgeo',
  'analyticstagging',
  'images',
  'securityandprotocol',
  'internationalization'
] as const;

/** AuditCategory wire value as carried by findings/scores REST payloads. */
export type AuditCategoryWire = (typeof AUDIT_CATEGORY_WIRE_VALUES)[number];

/**
 * Display labels keyed by wire value. The three WP-10-A1 categories use the
 * §7.4 taxonomy labels verbatim (Images; Security & protocol;
 * Internationalization); the baseline labels match the configuration editor.
 */
export const AUDIT_CATEGORY_LABELS: Readonly<Record<AuditCategoryWire, string>> = {
  indexability: 'Indexability',
  content: 'Content',
  linking: 'Linking',
  structureddata: 'Structured data',
  accessibility: 'Accessibility',
  performance: 'Performance',
  crawl: 'Crawl health',
  custom: 'Custom extraction and search',
  logintelligence: 'Log intelligence',
  aisearchgeo: 'AI search (GEO)',
  analyticstagging: 'Analytics and tagging',
  images: 'Images',
  securityandprotocol: 'Security & protocol',
  internationalization: 'Internationalization'
};

/** Narrows an unknown wire value to a known AuditCategory wire value. */
export function isAuditCategoryWire(value: unknown): value is AuditCategoryWire {
  return typeof value === 'string' && (AUDIT_CATEGORY_WIRE_VALUES as readonly string[]).includes(value);
}

/** Narrows an unknown value to a known AuditCategory member name. */
export function isAuditCategoryMember(value: unknown): value is AuditCategoryMember {
  return typeof value === 'string' && (AUDIT_CATEGORY_MEMBERS as readonly string[]).includes(value);
}

// ── WafChallengeClass (FS-CRAWL-002 §2.3.1) ─────────────────────────────────

/**
 * WafChallengeClass REST wire values. The baseline tokens are frozen
 * (unknown/captcha/interstitial/botblock); WP-10-A1 appends the two remaining
 * §2.3.1 catalogue classes with their hyphenated spec tokens.
 */
export const WAF_CHALLENGE_CLASS_WIRE_VALUES = [
  'unknown',
  'captcha',
  'interstitial',
  'botblock',
  'managed-challenge',
  'rate-limit-soft-block'
] as const;

/** WafChallengeClass wire value (WAF detections, signatures, hub payloads). */
export type WafChallengeClassWire = (typeof WAF_CHALLENGE_CLASS_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known WafChallengeClass wire value. */
export function isWafChallengeClassWire(value: unknown): value is WafChallengeClassWire {
  return typeof value === 'string' && (WAF_CHALLENGE_CLASS_WIRE_VALUES as readonly string[]).includes(value);
}

// ── CaptureMode / PatternCaptureMode / CaptureSource (§5.6.1) ───────────────

/** CaptureMode REST wire values; new configurations default to non-js. */
export const CAPTURE_MODE_WIRE_VALUES = ['non-js', 'js', 'dual', 'adaptive'] as const;

/** Capture mode of a configuration (captureMode wire field). */
export type CaptureModeWire = (typeof CAPTURE_MODE_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known CaptureMode wire value. */
export function isCaptureModeWire(value: unknown): value is CaptureModeWire {
  return typeof value === 'string' && (CAPTURE_MODE_WIRE_VALUES as readonly string[]).includes(value);
}

/** PatternCaptureMode REST wire values (explicit per-pattern overrides). */
export const PATTERN_CAPTURE_WIRE_VALUES = ['non-rendered', 'rendered'] as const;

/** Explicit per-URL-pattern capture (rendered or non-rendered). */
export type PatternCaptureWire = (typeof PATTERN_CAPTURE_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known PatternCaptureMode wire value. */
export function isPatternCaptureWire(value: unknown): value is PatternCaptureWire {
  return typeof value === 'string' && (PATTERN_CAPTURE_WIRE_VALUES as readonly string[]).includes(value);
}

/** CaptureSource REST wire values (which capture produced a URL record). */
export const CAPTURE_SOURCE_WIRE_VALUES = ['raw', 'rendered', 'both'] as const;

/** Capture provenance of a URL record (captureSource wire field). */
export type CaptureSourceWire = (typeof CAPTURE_SOURCE_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known CaptureSource wire value. */
export function isCaptureSourceWire(value: unknown): value is CaptureSourceWire {
  return typeof value === 'string' && (CAPTURE_SOURCE_WIRE_VALUES as readonly string[]).includes(value);
}

// ── DiscoverySource (url_records.discovery_source) ──────────────────────────

/** DiscoverySource REST wire values; WP-10-A1 appends render (discovered-by-render). */
export const DISCOVERY_SOURCE_WIRE_VALUES = ['seed', 'link', 'sitemap', 'redirect', 'list', 'render'] as const;

/** How a URL entered the crawl (discoverySource wire field). */
export type DiscoverySourceWire = (typeof DISCOVERY_SOURCE_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known DiscoverySource wire value. */
export function isDiscoverySourceWire(value: unknown): value is DiscoverySourceWire {
  return typeof value === 'string' && (DISCOVERY_SOURCE_WIRE_VALUES as readonly string[]).includes(value);
}

// ── ContentArtifactKind (content-store artifact kinds) ──────────────────────

/**
 * ContentArtifactKind REST wire values (lowercase convention). WP-10-A1
 * appends networkwaterfall: the rendered-capture network waterfall stored per
 * rendered URL (§5.6.2; WP-10-01 render result, CR-031 image sizes).
 */
export const CONTENT_ARTIFACT_KIND_WIRE_VALUES = ['body', 'screenshot', 'rendereddom', 'networkwaterfall'] as const;

/** Content-store artifact kind wire value. */
export type ContentArtifactKindWire = (typeof CONTENT_ARTIFACT_KIND_WIRE_VALUES)[number];

/** Narrows an unknown wire value to a known ContentArtifactKind wire value. */
export function isContentArtifactKindWire(value: unknown): value is ContentArtifactKindWire {
  return typeof value === 'string' && (CONTENT_ARTIFACT_KIND_WIRE_VALUES as readonly string[]).includes(value);
}

// ── IndexabilityFlags (url_records indexability bitmask; CR-024) ────────────

/**
 * IndexabilityFlags bit values, mirroring the locked [Flags] enum. WP-10-A1
 * appends canonicalElsewhere = 16 (CR-024: a canonical pointing elsewhere makes
 * the URL non-search-indexable). Existing bits are frozen.
 */
export const INDEXABILITY_FLAG_BITS = {
  none: 0,
  noindex: 1,
  nofollow: 2,
  robotsBlocked: 4,
  nonIndexableStatus: 8,
  canonicalElsewhere: 16
} as const;

/** Indexability flag name (camelCase mirror of the CLR member). */
export type IndexabilityFlagName = keyof typeof INDEXABILITY_FLAG_BITS;

/** True when the bitmask carries the named flag. */
export function hasIndexabilityFlag(flags: number, flag: IndexabilityFlagName): boolean {
  return (flags & INDEXABILITY_FLAG_BITS[flag]) !== 0;
}

/**
 * True when any restriction flag is set — the CR-024 search-indexable
 * semantics (none = indexable; every other bit restricts indexing).
 */
export function isSearchIndexableFlagSet(flags: number): boolean {
  return flags === INDEXABILITY_FLAG_BITS.none;
}

// ── FindingRuleKeys (findings.rule_key; open taxonomy, frozen catalog) ──────

/**
 * Mirror of the locked CrawlForge.Contracts.Constants.FindingRuleKeys catalog
 * (member name → stable key). Rule keys remain open strings on the wire; this
 * catalog is the frozen vocabulary for labels, suppressions and parity locks.
 * WP-10-A1 appends the phase-09 builder-local keys (WIRING-NOTES gap 6), the
 * CR-029 redirect cases, the CR-030 deprecated-term key and the phase-10
 * capture/comparison/service-worker keys. Existing keys are never repurposed.
 */
export const FINDING_RULE_KEYS = {
  WafChallengeDetected: 'waf-challenge-detected',
  WafBlockRate: 'waf-block-rate',
  ChallengeResolvedByRender: 'challenge-resolved-by-render',
  SuspectedChallenge: 'suspected-challenge',
  ContentKeywordCannibalization: 'content.keyword-cannibalization',
  ContentTopicalGap: 'content.topical-gap',
  RedirectMatrixPass: 'redirect-matrix.pass',
  RedirectMatrixTargetMismatch: 'redirect-matrix.target-mismatch',
  RedirectMatrixChainExceeded: 'redirect-matrix.chain-exceeded',
  RedirectMatrixFinalNon200: 'redirect-matrix.final-non-200',
  RedirectMatrixCanonicalMismatch: 'redirect-matrix.canonical-mismatch',
  LogsTrueOrphan: 'logs.true-orphan',
  LogsCrawlBudgetWaste: 'logs.crawl-budget-waste',
  LogsUncrawledIndexable: 'logs.uncrawled-indexable',
  ContentSemanticCannibalization: 'content.semantic-cannibalization',
  LinkingLowRelevanceLink: 'linking.low-relevance-link',
  GeoLlmsTxtMissing: 'geo.llms-txt-missing',
  GeoLlmsTxtMalformed: 'geo.llms-txt-malformed',
  GeoLlmsTxtBrokenLink: 'geo.llms-txt-broken-link',
  GeoGovernanceConflict: 'geo.governance-conflict',
  AnalyticsMissingContainer: 'analytics.missing-container',
  AnalyticsDuplicateContainer: 'analytics.duplicate-container',
  AnalyticsDataLayerMalformed: 'analytics.datalayer-malformed',
  AnalyticsConsentModeMissing: 'analytics.consent-mode-missing',
  AnalyticsNoHitsObserved: 'analytics.no-hits-observed',
  TitlePixelTruncation: 'title-pixel-truncation',
  DescriptionPixelTruncation: 'description-pixel-truncation',
  IndexabilityHeaderHtmlConflict: 'indexability.header-html-conflict',
  CanonicalHeaderHtmlConflict: 'canonical.header-html-conflict',
  HreflangHeaderHtmlConflict: 'hreflang.header-html-conflict',
  // WP-10-A1: phase-09 builder-local keys (WIRING-NOTES notation gap 6).
  CrawlerTrapDetected: 'crawler-trap.detected',
  CanonicalizationSessionSegmentRemoved: 'canonicalization.session-segment-removed',
  CrawlCookielessRedirectLoop: 'crawl.cookieless-redirect-loop',
  IndexabilityRobotsBlocked: 'indexability.robots-blocked',
  UaComparisonMaterialDifference: 'ua-comparison.material-difference',
  RedirectsLoop: 'redirects.loop',
  RedirectsInappropriate: 'redirects.inappropriate',
  RedirectsChain: 'redirects.chain',
  ContentDuplicateTitle: 'content.duplicate-title',
  ContentDuplicateDescription: 'content.duplicate-description',
  ContentDuplicateContent: 'content.duplicate-content',
  UrlSessionParameters: 'url.session-parameters',
  UrlTrackingParameters: 'url.tracking-parameters',
  UrlTrailingSlashInconsistency: 'url.trailing-slash-inconsistency',
  UrlDuplicateUrls: 'url.duplicate-urls',
  ImageOversized: 'image.oversized',
  ImageFormat: 'image.format',
  StructuredDataMissingRecommended: 'structured-data.missing-recommended',
  // WP-10-A1: CR-029 inappropriate-redirect cases (all toggles default on).
  RedirectsInappropriateCrossHost: 'redirects.inappropriate-cross-host',
  RedirectsInappropriateTemporaryStatus: 'redirects.inappropriate-temporary-status',
  RedirectsInappropriateNonIndexableTarget: 'redirects.inappropriate-non-indexable-target',
  RedirectsInappropriateHttpsDowngrade: 'redirects.inappropriate-https-downgrade',
  // WP-10-A1: CR-030 deprecated structured-data terms.
  StructuredDataDeprecatedTerm: 'structured-data.deprecated-term',
  // WP-10-A1: adaptive capture switch finding (§5.6.1; ratified content.* key).
  ContentJavascriptDependentTemplate: 'content.javascript-dependent-template',
  // WP-10-A1: response-vs-rendered comparison findings (§7.18).
  RenderStatusDelta: 'render.status-delta',
  RenderTitleDelta: 'render.title-delta',
  RenderMetadataDelta: 'render.metadata-delta',
  RenderTextDelta: 'render.text-delta',
  RenderLinksDelta: 'render.links-delta',
  RenderStructuredDataDelta: 'render.structured-data-delta',
  RenderResourcesDelta: 'render.resources-delta',
  RenderCanonicalDelta: 'render.canonical-delta',
  RenderMetaRobotsDelta: 'render.meta-robots-delta',
  RenderNoindexDelta: 'render.noindex-delta',
  // WP-10-A1: service-worker registration informational finding (§7.19).
  RenderServiceWorkerRegistered: 'render.service-worker-registered'
} as const;

/** FindingRuleKeys member name (mirror of the C# constant identifier). */
export type FindingRuleKeyName = keyof typeof FINDING_RULE_KEYS;

/** Frozen list of every mirrored rule-key string (parity lock surface). */
export const FINDING_RULE_KEY_VALUES: readonly string[] = Object.values(FINDING_RULE_KEYS);

/** True when the value is one of the mirrored rule keys (open taxonomy: unknown keys stay legal). */
export function isKnownFindingRuleKey(value: unknown): value is (typeof FINDING_RULE_KEYS)[FindingRuleKeyName] {
  return typeof value === 'string' && FINDING_RULE_KEY_VALUES.includes(value);
}