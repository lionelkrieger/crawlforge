/**
 * Vocabulary mirror guard tests (WP-10-A1, D-021).
 *
 * Component-level proof that the frontend contracts accept every additive
 * value of the amendment: the three new AuditCategory members, the two new
 * WafChallengeClass tokens, IndexabilityFlags.canonicalElsewhere, the
 * capture-mode vocabularies, the CR-029 redirect toggles and the phase-09/10
 * finding rule keys. The C# FrontendMirrorParityTests lock these catalogs
 * against the backend assembly; these guards lock the consumer side.
 */

import { describe, expect, it } from 'vitest';
import {
  AUDIT_CATEGORY_LABELS,
  AUDIT_CATEGORY_MEMBERS,
  AUDIT_CATEGORY_WIRE_VALUES,
  CAPTURE_MODE_WIRE_VALUES,
  CAPTURE_SOURCE_WIRE_VALUES,
  CONTENT_ARTIFACT_KIND_WIRE_VALUES,
  DISCOVERY_SOURCE_WIRE_VALUES,
  FINDING_RULE_KEYS,
  FINDING_RULE_KEY_VALUES,
  INDEXABILITY_FLAG_BITS,
  PATTERN_CAPTURE_WIRE_VALUES,
  WAF_CHALLENGE_CLASS_WIRE_VALUES,
  hasIndexabilityFlag,
  isAuditCategoryMember,
  isAuditCategoryWire,
  isCaptureModeWire,
  isCaptureSourceWire,
  isContentArtifactKindWire,
  isDiscoverySourceWire,
  isKnownFindingRuleKey,
  isPatternCaptureWire,
  isSearchIndexableFlagSet,
  isWafChallengeClassWire
} from './vocabulary';
import type {
  AuditConfiguration,
  ConfigurationCreateRequest,
  ConfigurationUpdateRequest
} from './projects';
import type { UrlRecordSummary } from './audits';
import type { RenderTestResponse, RenderedNetworkEntry } from './tools';

describe('AuditCategory mirror (WP-10-A1)', () => {
  it('locks the full 14-member vocabulary with the three additive categories', () => {
    expect(AUDIT_CATEGORY_MEMBERS).toHaveLength(14);
    expect(AUDIT_CATEGORY_WIRE_VALUES).toHaveLength(14);
    expect(AUDIT_CATEGORY_MEMBERS).toContain('Images');
    expect(AUDIT_CATEGORY_MEMBERS).toContain('SecurityAndProtocol');
    expect(AUDIT_CATEGORY_MEMBERS).toContain('Internationalization');
    expect(AUDIT_CATEGORY_WIRE_VALUES).toContain('images');
    expect(AUDIT_CATEGORY_WIRE_VALUES).toContain('securityandprotocol');
    expect(AUDIT_CATEGORY_WIRE_VALUES).toContain('internationalization');
  });

  it('keeps every baseline member and wire value frozen', () => {
    const baseline = [
      'Indexability', 'Content', 'Linking', 'StructuredData', 'Accessibility',
      'Performance', 'Crawl', 'Custom', 'LogIntelligence', 'AiSearchGeo', 'AnalyticsTagging'
    ];
    expect(AUDIT_CATEGORY_MEMBERS.slice(0, 11)).toEqual(baseline);
    expect(AUDIT_CATEGORY_WIRE_VALUES.slice(0, 11)).toEqual(baseline.map((m) => m.toLowerCase()));
  });

  it('maps members to wire values by the lowercase convention', () => {
    AUDIT_CATEGORY_MEMBERS.forEach((member, index) => {
      expect(AUDIT_CATEGORY_WIRE_VALUES[index]).toBe(member.toLowerCase());
    });
  });

  it('labels the new categories with the §7.4 taxonomy names', () => {
    expect(AUDIT_CATEGORY_LABELS.images).toBe('Images');
    expect(AUDIT_CATEGORY_LABELS.securityandprotocol).toBe('Security & protocol');
    expect(AUDIT_CATEGORY_LABELS.internationalization).toBe('Internationalization');
  });

  it('guards accept the new values and reject unknown ones', () => {
    expect(isAuditCategoryWire('images')).toBe(true);
    expect(isAuditCategoryWire('securityandprotocol')).toBe(true);
    expect(isAuditCategoryWire('internationalization')).toBe(true);
    expect(isAuditCategoryWire('structureddata')).toBe(true);
    expect(isAuditCategoryWire('Security & protocol')).toBe(false);
    expect(isAuditCategoryWire('unknown-category')).toBe(false);
    expect(isAuditCategoryWire(null)).toBe(false);
    expect(isAuditCategoryMember('Images')).toBe(true);
    expect(isAuditCategoryMember('SecurityAndProtocol')).toBe(true);
    expect(isAuditCategoryMember('Internationalization')).toBe(true);
    expect(isAuditCategoryMember('images')).toBe(false);
  });
});

describe('WafChallengeClass mirror (WP-10-A1)', () => {
  it('appends the two §2.3.1 classes without touching baseline tokens', () => {
    expect(WAF_CHALLENGE_CLASS_WIRE_VALUES).toEqual([
      'unknown', 'captcha', 'interstitial', 'botblock', 'managed-challenge', 'rate-limit-soft-block'
    ]);
  });

  it('guards accept the hyphenated spec tokens', () => {
    expect(isWafChallengeClassWire('managed-challenge')).toBe(true);
    expect(isWafChallengeClassWire('rate-limit-soft-block')).toBe(true);
    expect(isWafChallengeClassWire('botblock')).toBe(true);
    expect(isWafChallengeClassWire('managedchallenge')).toBe(false);
    expect(isWafChallengeClassWire('js-interstitial')).toBe(false);
  });
});

describe('capture-mode vocabularies (§5.6.1)', () => {
  it('locks captureMode wire values with the hyphenated non-js token', () => {
    expect(CAPTURE_MODE_WIRE_VALUES).toEqual(['non-js', 'js', 'dual', 'adaptive']);
    expect(isCaptureModeWire('non-js')).toBe(true);
    expect(isCaptureModeWire('js')).toBe(true);
    expect(isCaptureModeWire('dual')).toBe(true);
    expect(isCaptureModeWire('adaptive')).toBe(true);
    expect(isCaptureModeWire('nonJs')).toBe(false);
  });

  it('locks the per-pattern override vocabulary', () => {
    expect(PATTERN_CAPTURE_WIRE_VALUES).toEqual(['non-rendered', 'rendered']);
    expect(isPatternCaptureWire('non-rendered')).toBe(true);
    expect(isPatternCaptureWire('rendered')).toBe(true);
    expect(isPatternCaptureWire('js')).toBe(false);
  });

  it('locks the capture-source vocabulary', () => {
    expect(CAPTURE_SOURCE_WIRE_VALUES).toEqual(['raw', 'rendered', 'both']);
    expect(isCaptureSourceWire('raw')).toBe(true);
    expect(isCaptureSourceWire('rendered')).toBe(true);
    expect(isCaptureSourceWire('both')).toBe(true);
    expect(isCaptureSourceWire('dual')).toBe(false);
  });

  it('appends discovered-by-render to the discovery sources', () => {
    expect(DISCOVERY_SOURCE_WIRE_VALUES).toEqual(['seed', 'link', 'sitemap', 'redirect', 'list', 'render']);
    expect(isDiscoverySourceWire('render')).toBe(true);
    expect(isDiscoverySourceWire('list')).toBe(true);
    expect(isDiscoverySourceWire('crawler')).toBe(false);
  });

  it('appends the network-waterfall artifact kind without moving baseline kinds', () => {
    expect(CONTENT_ARTIFACT_KIND_WIRE_VALUES).toEqual(['body', 'screenshot', 'rendereddom', 'networkwaterfall']);
    expect(isContentArtifactKindWire('networkwaterfall')).toBe(true);
    expect(isContentArtifactKindWire('rendereddom')).toBe(true);
    expect(isContentArtifactKindWire('rendered-dom')).toBe(false);
  });
});

describe('rendered network waterfall mirror (§5.6.2, CR-031)', () => {
  it('accepts waterfall entries on the render-test response additively', () => {
    const entry: RenderedNetworkEntry = {
      url: 'http://127.0.0.1:47311/assets/hero.jpg',
      resourceType: 'image',
      method: 'GET',
      statusCode: 200,
      mimeType: 'image/jpeg',
      transferredBytes: 204_800,
      encodedDataLength: 204_800,
      timingJson: '{"requestStart":12,"ttfb":40,"end":95}',
      fromCache: false,
      fromServiceWorker: false
    };
    const response: RenderTestResponse = {
      screenshotBase64: null,
      renderedHtml: '<html></html>',
      consoleMessages: [],
      networkWaterfall: [entry]
    };
    expect(response.networkWaterfall?.[0].transferredBytes).toBe(204_800);
    expect(response.networkWaterfall?.[0].fromServiceWorker).toBe(false);

    // Baseline render responses stay valid without a waterfall.
    const baseline: RenderTestResponse = {
      screenshotBase64: null,
      renderedHtml: '<html></html>',
      consoleMessages: []
    };
    expect(baseline.networkWaterfall).toBeUndefined();

    // Unobserved optional facts may be omitted.
    const minimal: RenderedNetworkEntry = {
      url: 'http://127.0.0.1:47311/',
      resourceType: 'document',
      method: 'GET',
      statusCode: 200
    };
    expect(minimal.transferredBytes).toBeUndefined();
    expect(minimal.fromCache).toBeUndefined();
  });
});

describe('IndexabilityFlags mirror (CR-024)', () => {
  it('freezes the baseline bits and appends canonicalElsewhere = 16', () => {
    expect(INDEXABILITY_FLAG_BITS).toEqual({
      none: 0,
      noindex: 1,
      nofollow: 2,
      robotsBlocked: 4,
      nonIndexableStatus: 8,
      canonicalElsewhere: 16
    });
  });

  it('composes flags and applies the search-indexable semantics', () => {
    const flags = INDEXABILITY_FLAG_BITS.noindex | INDEXABILITY_FLAG_BITS.canonicalElsewhere;
    expect(hasIndexabilityFlag(flags, 'canonicalElsewhere')).toBe(true);
    expect(hasIndexabilityFlag(flags, 'noindex')).toBe(true);
    expect(hasIndexabilityFlag(flags, 'nofollow')).toBe(false);
    expect(isSearchIndexableFlagSet(INDEXABILITY_FLAG_BITS.none)).toBe(true);
    expect(isSearchIndexableFlagSet(INDEXABILITY_FLAG_BITS.canonicalElsewhere)).toBe(false);
    expect(isSearchIndexableFlagSet(flags)).toBe(false);
  });
});

describe('FindingRuleKeys mirror (gap 6 + CR-029/CR-030 + phase 10)', () => {
  it('mirrors every new key with its exact stable string', () => {
    expect(FINDING_RULE_KEYS.CrawlerTrapDetected).toBe('crawler-trap.detected');
    expect(FINDING_RULE_KEYS.CanonicalizationSessionSegmentRemoved).toBe('canonicalization.session-segment-removed');
    expect(FINDING_RULE_KEYS.CrawlCookielessRedirectLoop).toBe('crawl.cookieless-redirect-loop');
    expect(FINDING_RULE_KEYS.UaComparisonMaterialDifference).toBe('ua-comparison.material-difference');
    expect(FINDING_RULE_KEYS.RedirectsChain).toBe('redirects.chain');
    expect(FINDING_RULE_KEYS.RedirectsLoop).toBe('redirects.loop');
    expect(FINDING_RULE_KEYS.RedirectsInappropriate).toBe('redirects.inappropriate');
    expect(FINDING_RULE_KEYS.ContentDuplicateTitle).toBe('content.duplicate-title');
    expect(FINDING_RULE_KEYS.ContentDuplicateDescription).toBe('content.duplicate-description');
    expect(FINDING_RULE_KEYS.ContentDuplicateContent).toBe('content.duplicate-content');
    expect(FINDING_RULE_KEYS.UrlSessionParameters).toBe('url.session-parameters');
    expect(FINDING_RULE_KEYS.UrlTrackingParameters).toBe('url.tracking-parameters');
    expect(FINDING_RULE_KEYS.UrlTrailingSlashInconsistency).toBe('url.trailing-slash-inconsistency');
    expect(FINDING_RULE_KEYS.UrlDuplicateUrls).toBe('url.duplicate-urls');
    expect(FINDING_RULE_KEYS.ImageOversized).toBe('image.oversized');
    expect(FINDING_RULE_KEYS.ImageFormat).toBe('image.format');
    expect(FINDING_RULE_KEYS.StructuredDataMissingRecommended).toBe('structured-data.missing-recommended');
    expect(FINDING_RULE_KEYS.RedirectsInappropriateCrossHost).toBe('redirects.inappropriate-cross-host');
    expect(FINDING_RULE_KEYS.RedirectsInappropriateTemporaryStatus).toBe('redirects.inappropriate-temporary-status');
    expect(FINDING_RULE_KEYS.RedirectsInappropriateNonIndexableTarget).toBe('redirects.inappropriate-non-indexable-target');
    expect(FINDING_RULE_KEYS.RedirectsInappropriateHttpsDowngrade).toBe('redirects.inappropriate-https-downgrade');
    expect(FINDING_RULE_KEYS.StructuredDataDeprecatedTerm).toBe('structured-data.deprecated-term');
    expect(FINDING_RULE_KEYS.ContentJavascriptDependentTemplate).toBe('content.javascript-dependent-template');
    expect(FINDING_RULE_KEYS.RenderTitleDelta).toBe('render.title-delta');
    expect(FINDING_RULE_KEYS.RenderCanonicalDelta).toBe('render.canonical-delta');
    expect(FINDING_RULE_KEYS.RenderMetaRobotsDelta).toBe('render.meta-robots-delta');
    expect(FINDING_RULE_KEYS.RenderNoindexDelta).toBe('render.noindex-delta');
    expect(FINDING_RULE_KEYS.RenderServiceWorkerRegistered).toBe('render.service-worker-registered');
  });

  it('keeps the baseline keys frozen and the catalog duplicate-free', () => {
    expect(FINDING_RULE_KEYS.WafChallengeDetected).toBe('waf-challenge-detected');
    expect(FINDING_RULE_KEYS.RedirectMatrixPass).toBe('redirect-matrix.pass');
    expect(FINDING_RULE_KEYS.HreflangHeaderHtmlConflict).toBe('hreflang.header-html-conflict');
    expect(FINDING_RULE_KEY_VALUES).toHaveLength(65);
    expect(new Set(FINDING_RULE_KEY_VALUES).size).toBe(FINDING_RULE_KEY_VALUES.length);
  });

  it('recognizes mirrored keys while the taxonomy stays open', () => {
    expect(isKnownFindingRuleKey('redirects.inappropriate-https-downgrade')).toBe(true);
    expect(isKnownFindingRuleKey('render.noindex-delta')).toBe(true);
    expect(isKnownFindingRuleKey('future.unlisted-key')).toBe(false);
  });
});

describe('configuration wire shapes accept the additive fields', () => {
  const baselineConfiguration: AuditConfiguration = {
    id: '7',
    projectId: '3',
    name: 'Baseline',
    description: null,
    createdAt: '2026-11-19T00:00:00Z',
    updatedAt: '2026-11-19T00:00:00Z',
    startUrls: ['http://127.0.0.1:47311/'],
    limits: {},
    aimd: { initialRateR0: 2, alpha: 0.5, beta: 0.5, kappa: 4, ewmaTtfbWeight: 0.2 },
    rendering: { enabled: false },
    respectRobotsTxt: true
  };

  it('stays valid without any capture fields (additive optionality)', () => {
    expect(baselineConfiguration.captureMode).toBeUndefined();
    expect(baselineConfiguration.captureSettings).toBeUndefined();
    expect(baselineConfiguration.redirectChecks).toBeUndefined();
  });

  it('accepts captureMode, captureSettings and redirectChecks on read/create/update', () => {
    const read: AuditConfiguration = {
      ...baselineConfiguration,
      captureMode: 'adaptive',
      captureSettings: {
        adaptiveSampleSizePerGroup: 4,
        materialChangeWordCountThreshold: 0.2,
        dualStorageWarningThresholdBytes: 1_073_741_824,
        renderWaitQuietMs: 500,
        renderWaitHardCapMs: 20_000,
        renderingPoolSize: null,
        patternOverrides: [{ pattern: '/blog/*', capture: 'rendered' }]
      },
      redirectChecks: { crossHost: true, temporaryStatus: true, nonIndexableTarget: true, httpsDowngrade: false }
    };
    const create: ConfigurationCreateRequest = {
      name: 'Dual probe',
      startUrls: ['http://127.0.0.1:47311/'],
      captureMode: 'dual',
      captureSettings: { patternOverrides: [{ pattern: '/product/*', capture: 'non-rendered' }] },
      redirectChecks: { httpsDowngrade: false }
    };
    const update: ConfigurationUpdateRequest = {
      captureMode: 'js',
      captureSettings: null,
      redirectChecks: null
    };
    expect(isCaptureModeWire(read.captureMode)).toBe(true);
    expect(read.captureSettings?.patternOverrides?.[0].capture).toBe('rendered');
    expect(read.redirectChecks?.httpsDowngrade).toBe(false);
    expect(create.captureMode).toBe('dual');
    expect(update.captureMode).toBe('js');
    // Null documents restore the defaults (all redirect checks on, engine knobs).
    expect(update.redirectChecks).toBeNull();
  });

  it('accepts captureSource and discoverySource on URL rows', () => {
    const row: UrlRecordSummary = {
      id: 'u1',
      canonicalUrl: 'http://127.0.0.1:47311/pages/js-content.html',
      statusCode: 200,
      statusClass: '2xx',
      contentType: 'text/html',
      depth: 1,
      indexable: true,
      title: null,
      captureSource: 'both',
      discoverySource: 'render'
    };
    expect(isCaptureSourceWire(row.captureSource)).toBe(true);
    expect(isDiscoverySourceWire(row.discoverySource)).toBe(true);
    const baselineRow: UrlRecordSummary = { ...row, captureSource: undefined, discoverySource: undefined };
    expect(baselineRow.captureSource).toBeUndefined();
  });
});