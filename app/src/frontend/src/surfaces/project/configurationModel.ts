/**
 * Configuration form model for the project surfaces (WP-09-07, UI-03/UI-04).
 *
 * The form value mirrors the REST `AuditConfiguration` wire DTO (Api
 * `AuditConfigurationWire`, camelCase) plus two ADDITIVE documents the engine
 * already consumes but the current REST DTO does not yet carry:
 *
 *  - `scopeRules`: the per-audit crawl-scope payload parsed by
 *    `CrawlScopeConfigParser` (CrawlForge.Crawler.Canonicalization) from
 *    `crawl_configurations.scope_rules_json`. Shape copied verbatim from the
 *    parser's documented contract (camelCase, every member optional, engine
 *    baselines reproduced here). The cookie-jar toggle (criterion #56) binds
 *    to `scopeRules.cookieJar.enabled` — the exact field the engine reads.
 *  - `enabledCategories`: the scoring snapshot convention
 *    (`AuditScoringService.ParseEnabledCategories`): absent/null = all
 *    categories enabled; otherwise the enabled enum-name keys.
 *  - `redirectChecks` (WP-10-05, CR-029): the four inappropriate-redirect
 *    toggles, bound to the LIVE wire field `redirectChecks` (WP-10-A1
 *    CaptureConfigurationFields; REST binding applied by the coordinator).
 *    Absent/null = all four checks ON; the form normalizes to required
 *    booleans and collapses all-enabled back to null on emit.
 *
 * Both additive fields are emitted on create/update payloads and tolerated on
 * read. Until WP-09-06/lead wires them through the Api DTO they are ignored by
 * the current backend model binder (additive JSON members are safe). See
 * WP-09-07-HANDOFF.md for the exact DTO-extension handoff.
 *
 * @module surfaces/project/configurationModel
 */

import type {
  AimdSettings,
  AuditConfiguration,
  CapturePatternOverride,
  CaptureSettings,
  ConfigurationCreateRequest,
  ConfigurationUpdateRequest,
  ConsentSettings,
  ContentScopeSettings,
  CrawlLimits,
  RedirectCheckSettings,
  RenderingSettings,
  RewriteSettings,
  ZoneWeightSettings
} from '../../contracts/api/projects';
import type { CaptureModeWire } from '../../contracts/api/vocabulary';

// ── Engine scope-rules document (CrawlScopeConfigParser shape) ──────────────

/** Canonicalization options (§5.2); defaults match CanonicalizationOptions. */
export interface CanonicalizationRules {
  readonly queryPolicy: 'keepAll' | 'keepWhitelist' | 'stripAll';
  readonly queryWhitelist: readonly string[];
  readonly trailingSlash: 'preserve' | 'append' | 'strip';
  readonly stripCookielessSessionSegments: boolean;
  readonly additionalSessionParameters: readonly string[];
}

/** Crawl scope options (§5.1/§5.13); defaults match CrawlScopeOptions. */
export interface ScopeRules {
  readonly mode: 'hostOnly' | 'includeSubdomains' | 'wholeDomain';
  readonly includePatterns: readonly string[];
  readonly excludePatterns: readonly string[];
  readonly pathPrefixes: readonly string[];
  readonly assetDomains: readonly string[];
  readonly checkExternalLinks: boolean;
  readonly regexTimeoutMs: number;
}

/** Directive-handling toggles (§5.12); defaults match DirectiveHandlingOptions. */
export interface DirectiveRules {
  readonly followPageNofollow: boolean;
  readonly followLinkNofollow: boolean;
  readonly crawlNoindexPages: boolean;
  readonly useCanonicalDiscovery: boolean;
  readonly useHreflangAndPaginationDiscovery: boolean;
  readonly followMetaRefresh: boolean;
  readonly followIframes: boolean;
}

/** Resource fetch toggles (§5.13); defaults match ResourceScopeOptions. */
export interface ResourceRules {
  readonly fetchScripts: boolean;
  readonly fetchStylesheets: boolean;
  readonly fetchImages: boolean;
  readonly fetchFonts: boolean;
  readonly fetchOtherFiles: boolean;
}

/** Cookie jar toggle (§5.11); default ON (CookieJarOptions.Enabled = true). */
export interface CookieJarRules {
  readonly enabled: boolean;
}

/** User-agent configuration (§4.4/§5.1/§5.15); defaults match UserAgentOptions. */
export interface UserAgentRules {
  readonly customUserAgent: string | null;
  readonly simulateGooglebot: boolean;
}

/** UA comparison (§5.15); defaults match UserAgentComparisonOptions. */
export interface UserAgentComparisonRules {
  readonly enabled: boolean;
  /** null = all URLs; default sample 50. */
  readonly sampleSize: number | null;
}

/**
 * The full engine scope-rules document. Every member is optional on the wire
 * (the parser defaults absent members); the form model always carries the
 * resolved baseline so controls are never uncontrolled.
 */
export interface ScopeRulesDocument {
  readonly canonicalization: CanonicalizationRules;
  readonly scope: ScopeRules;
  readonly directives: DirectiveRules;
  readonly resources: ResourceRules;
  readonly cookieJar: CookieJarRules;
  readonly userAgent: UserAgentRules;
  readonly userAgentComparison: UserAgentComparisonRules;
  readonly headers: Readonly<Record<string, string>>;
  readonly cookies: Readonly<Record<string, string>>;
}

/** Baseline scope rules — reproduces CrawlScopeConfig.Default exactly. */
export const DEFAULT_SCOPE_RULES: ScopeRulesDocument = {
  canonicalization: {
    queryPolicy: 'keepAll',
    queryWhitelist: [],
    trailingSlash: 'preserve',
    stripCookielessSessionSegments: true,
    additionalSessionParameters: []
  },
  scope: {
    mode: 'hostOnly',
    includePatterns: [],
    excludePatterns: [],
    pathPrefixes: [],
    assetDomains: [],
    checkExternalLinks: false,
    regexTimeoutMs: 250
  },
  directives: {
    followPageNofollow: false,
    followLinkNofollow: false,
    crawlNoindexPages: true,
    useCanonicalDiscovery: true,
    useHreflangAndPaginationDiscovery: true,
    followMetaRefresh: true,
    followIframes: false
  },
  resources: {
    fetchScripts: false,
    fetchStylesheets: false,
    fetchImages: false,
    fetchFonts: false,
    fetchOtherFiles: false
  },
  cookieJar: { enabled: true },
  userAgent: { customUserAgent: null, simulateGooglebot: false },
  userAgentComparison: { enabled: false, sampleSize: 50 },
  headers: {},
  cookies: {}
};

// ── Form value ──────────────────────────────────────────────────────────────

/**
 * Configuration editor form value: the REST wire fields plus the additive
 * engine documents. `limits` members are null = unlimited (03/01 §4.3
 * sentinel — never a magic large number, never clamped).
 */
// ── CR-029 inappropriate-redirect checks (WP-10-05, UI-04 analysis tab) ──────

/**
 * Form-level redirect-check toggles: the four CR-029 cases as REQUIRED
 * booleans (the wire document's fields are optional; absent/null/missing
 * field = enabled). Mirrors the locked `RedirectCheckSettings` wire document
 * one-for-one; the engine maps it onto `RedirectInappropriatenessChecks`.
 */
export interface RedirectChecksFormValue {
  readonly crossHost: boolean;
  readonly temporaryStatus: boolean;
  readonly nonIndexableTarget: boolean;
  readonly httpsDowngrade: boolean;
}

/** All four checks enabled — the CR-029 default (absent/null on the wire). */
export const REDIRECT_CHECKS_ALL_ENABLED: RedirectChecksFormValue = {
  crossHost: true,
  temporaryStatus: true,
  nonIndexableTarget: true,
  httpsDowngrade: true
};

/** The four cases in report order (labels match the §7.4 rule definition). */
export const REDIRECT_CHECK_CASES: readonly {
  key: keyof RedirectChecksFormValue;
  label: string;
  description: string;
}[] = [
  { key: 'crossHost', label: 'Cross-host redirects', description: 'A hop redirects to a different host.' },
  { key: 'temporaryStatus', label: 'Temporary redirect status', description: 'A hop uses 302/303/307 where a permanent move is intended.' },
  { key: 'nonIndexableTarget', label: 'Non-indexable final target', description: 'The redirect target is not search-indexable per CR-024 (status, robots, noindex, canonical elsewhere).' },
  { key: 'httpsDowngrade', label: 'HTTPS-to-HTTP downgrade', description: 'An HTTPS URL redirects to HTTP.' }
];

/** Normalizes a wire document (absent/null/missing fields = all ON). */
export function redirectChecksFromWire(wire: RedirectCheckSettings | null | undefined): RedirectChecksFormValue {
  return {
    crossHost: wire?.crossHost ?? true,
    temporaryStatus: wire?.temporaryStatus ?? true,
    nonIndexableTarget: wire?.nonIndexableTarget ?? true,
    httpsDowngrade: wire?.httpsDowngrade ?? true
  };
}

/**
 * Emits the wire document: all-enabled collapses to null (the CR-029
 * absent/null = all-ON convention, mirroring `enabledCategories`); any
 * disabled case emits an explicit four-field document.
 */
export function redirectChecksToWire(form: RedirectChecksFormValue): RedirectCheckSettings | null {
  if (form.crossHost && form.temporaryStatus && form.nonIndexableTarget && form.httpsDowngrade) {
    return null;
  }
  return {
    crossHost: form.crossHost,
    temporaryStatus: form.temporaryStatus,
    nonIndexableTarget: form.nonIndexableTarget,
    httpsDowngrade: form.httpsDowngrade
  };
}

/** Toggles one redirect case (immutable). */
export function toggleRedirectCheck(
  current: RedirectChecksFormValue,
  key: keyof RedirectChecksFormValue,
  enabled: boolean
): RedirectChecksFormValue {
  return { ...current, [key]: enabled };
}

// ── Capture mode + settings (WP-10-04 wiring, §5.6.1; WP-10-02 handoff §5.6) ──

/**
 * Capture-settings form group. The material-change threshold is edited as a
 * PERCENT (20) and mapped to the wire fraction (0.20); every other knob maps
 * 1:1. Defaults reproduce CrawlForge.Contracts.Crawl.CaptureSettings.Default.
 */
export interface CaptureSettingsFormValue {
  /** Adaptive sample size per URL-pattern group (spec range 3–5). */
  readonly adaptiveSampleSize: number;
  /** Material-change visible word-count threshold, percent (wire fraction ×100). */
  readonly materialThresholdPercent: number;
  /** Dual-mode stored-capture warning threshold in bytes — warn only, no hard cap. */
  readonly dualStorageThresholdBytes: number;
  readonly renderWaitQuietMs: number;
  readonly renderWaitHardCapMs: number;
  /** null = auto (half logical processors, minimum 2). */
  readonly renderingPoolSize: number | null;
  readonly patternOverrides: readonly CapturePatternOverride[];
}

export const DEFAULT_CAPTURE_SETTINGS: CaptureSettingsFormValue = {
  adaptiveSampleSize: 4,
  materialThresholdPercent: 20,
  dualStorageThresholdBytes: 1073741824,
  renderWaitQuietMs: 500,
  renderWaitHardCapMs: 20000,
  renderingPoolSize: null,
  patternOverrides: []
};

/** Normalizes a wire document (absent/null/missing fields = engine defaults). */
export function captureSettingsFromWire(wire: CaptureSettings | null | undefined): CaptureSettingsFormValue {
  return {
    adaptiveSampleSize: wire?.adaptiveSampleSizePerGroup ?? DEFAULT_CAPTURE_SETTINGS.adaptiveSampleSize,
    materialThresholdPercent: wire?.materialChangeWordCountThreshold !== undefined
      ? Math.round(wire.materialChangeWordCountThreshold * 100)
      : DEFAULT_CAPTURE_SETTINGS.materialThresholdPercent,
    dualStorageThresholdBytes: wire?.dualStorageWarningThresholdBytes ?? DEFAULT_CAPTURE_SETTINGS.dualStorageThresholdBytes,
    renderWaitQuietMs: wire?.renderWaitQuietMs ?? DEFAULT_CAPTURE_SETTINGS.renderWaitQuietMs,
    renderWaitHardCapMs: wire?.renderWaitHardCapMs ?? DEFAULT_CAPTURE_SETTINGS.renderWaitHardCapMs,
    renderingPoolSize: wire?.renderingPoolSize ?? null,
    patternOverrides: [...(wire?.patternOverrides ?? [])]
  };
}

/** Emits the explicit wire document (percent → fraction; empty overrides → []). */
export function captureSettingsToWire(form: CaptureSettingsFormValue): CaptureSettings {
  return {
    adaptiveSampleSizePerGroup: form.adaptiveSampleSize,
    materialChangeWordCountThreshold: form.materialThresholdPercent / 100,
    dualStorageWarningThresholdBytes: form.dualStorageThresholdBytes,
    renderWaitQuietMs: form.renderWaitQuietMs,
    renderWaitHardCapMs: form.renderWaitHardCapMs,
    renderingPoolSize: form.renderingPoolSize,
    patternOverrides: form.patternOverrides.length > 0 ? [...form.patternOverrides] : []
  };
}

export interface ConfigurationFormValue {
  readonly name: string;
  readonly description: string | null;
  readonly startUrls: readonly string[];
  readonly limits: CrawlLimits;
  readonly aimd: AimdSettings;
  readonly rendering: RenderingSettings;
  readonly respectRobotsTxt: boolean;
  readonly virtualRobotsTxt: string | null;
  readonly scopeRules: ScopeRulesDocument;
  /** null = all categories enabled (scoring snapshot convention). */
  readonly enabledCategories: readonly string[] | null;
  /** CR-029 inappropriate-redirect toggles (wire `redirectChecks`; null = all ON). */
  readonly redirectChecks: RedirectChecksFormValue;
  /** §5.6.1 capture mode (wire `captureMode`; absent/null = non-js). */
  readonly captureMode: CaptureModeWire;
  /** §5.6.1/§5.6.2 capture knobs (wire `captureSettings`). */
  readonly captureSettings: CaptureSettingsFormValue;
  readonly contentScope?: ContentScopeSettings;
  readonly rewrite?: RewriteSettings;
  readonly consent?: ConsentSettings;
  readonly zoneWeights?: ZoneWeightSettings;
}

/** Default AIMD parameters — ProjectWireMapper.DefaultAimd (2.0/0.5/0.5/4/0.2). */
export const DEFAULT_AIMD: AimdSettings = {
  initialRateR0: 2.0,
  alpha: 0.5,
  beta: 0.5,
  kappa: 4,
  ewmaTtfbWeight: 0.2
};

/** Unlimited-by-default limits (null sentinel, 03/01 §4.2/§4.3). */
export const DEFAULT_LIMITS: CrawlLimits = {
  maxUrls: null,
  maxDepth: null,
  maxDurationSeconds: null,
  maxResponseBytes: null
};

/** A fresh configuration form (engine baselines; one optional seed URL). */
export function defaultConfigurationForm(seedUrl?: string): ConfigurationFormValue {
  return {
    name: 'Default configuration',
    description: null,
    startUrls: seedUrl ? [seedUrl] : [],
    limits: { ...DEFAULT_LIMITS },
    aimd: { ...DEFAULT_AIMD },
    rendering: { enabled: false, deviceProfile: null, infiniteScroll: false, captureConsole: false },
    respectRobotsTxt: true,
    virtualRobotsTxt: null,
    // New configurations enable bounded external status checks by default so
    // the default Linking rule family (broken external links, CAP-B-047) can
    // report. The storage-level baseline (DEFAULT_SCOPE_RULES /
    // CrawlScopeConfig.Default) stays record-only and is unchanged.
    scopeRules: {
      ...DEFAULT_SCOPE_RULES,
      scope: { ...DEFAULT_SCOPE_RULES.scope, checkExternalLinks: true }
    },
    enabledCategories: null,
    redirectChecks: REDIRECT_CHECKS_ALL_ENABLED,
    captureMode: 'non-js',
    captureSettings: { ...DEFAULT_CAPTURE_SETTINGS, patternOverrides: [] },
    rewrite: { rules: [], hostMappings: [] }
  };
}

// ── Wire mapping (real producer → real consumer) ────────────────────────────

/**
 * REST configuration DTO as actually returned today, plus the additive
 * members this workpiece emits (tolerated on read, ignored by the current
 * backend until the WP-09-06 DTO extension lands).
 */
export type ConfigurationWireDto = AuditConfiguration & {
  readonly scopeRules?: Partial<ScopeRulesDocument> | null;
  readonly enabledCategories?: readonly string[] | null;
};

function mergeScopeRules(partial: Partial<ScopeRulesDocument> | null | undefined): ScopeRulesDocument {
  if (!partial) return DEFAULT_SCOPE_RULES;
  return {
    canonicalization: { ...DEFAULT_SCOPE_RULES.canonicalization, ...partial.canonicalization },
    scope: { ...DEFAULT_SCOPE_RULES.scope, ...partial.scope },
    directives: { ...DEFAULT_SCOPE_RULES.directives, ...partial.directives },
    resources: { ...DEFAULT_SCOPE_RULES.resources, ...partial.resources },
    cookieJar: { ...DEFAULT_SCOPE_RULES.cookieJar, ...partial.cookieJar },
    userAgent: { ...DEFAULT_SCOPE_RULES.userAgent, ...partial.userAgent },
    userAgentComparison: { ...DEFAULT_SCOPE_RULES.userAgentComparison, ...partial.userAgentComparison },
    headers: { ...partial.headers },
    cookies: { ...partial.cookies }
  };
}

/** Reads a REST configuration DTO into the form model (tolerant of absent additive fields). */
export function configurationFromWire(dto: ConfigurationWireDto): ConfigurationFormValue {
  return {
    name: dto.name,
    description: dto.description ?? null,
    startUrls: [...(dto.startUrls ?? [])],
    limits: {
      maxUrls: dto.limits?.maxUrls ?? null,
      maxDepth: dto.limits?.maxDepth ?? null,
      maxDurationSeconds: dto.limits?.maxDurationSeconds ?? null,
      maxResponseBytes: dto.limits?.maxResponseBytes ?? null
    },
    aimd: { ...DEFAULT_AIMD, ...dto.aimd },
    rendering: {
      enabled: dto.rendering?.enabled ?? false,
      deviceProfile: dto.rendering?.deviceProfile ?? null,
      infiniteScroll: dto.rendering?.infiniteScroll ?? false,
      captureConsole: dto.rendering?.captureConsole ?? false
    },
    respectRobotsTxt: dto.respectRobotsTxt ?? true,
    virtualRobotsTxt: dto.virtualRobotsTxt ?? null,
    scopeRules: mergeScopeRules(dto.scopeRules),
    enabledCategories: dto.enabledCategories ?? null,
    redirectChecks: redirectChecksFromWire(dto.redirectChecks),
    captureMode: dto.captureMode ?? 'non-js',
    captureSettings: captureSettingsFromWire(dto.captureSettings),
    ...(dto.contentScope ? { contentScope: dto.contentScope } : {}),
    ...(dto.rewrite ? { rewrite: dto.rewrite } : {}),
    ...(dto.consent ? { consent: dto.consent } : {}),
    ...(dto.zoneWeights ? { zoneWeights: dto.zoneWeights } : {})
  };
}

/** Create payload: wire fields + additive engine documents. */
export function configurationToCreateRequest(value: ConfigurationFormValue): ConfigurationCreateRequest & {
  limits: CrawlLimits;
  aimd: AimdSettings;
  rendering: RenderingSettings;
  scopeRules: ScopeRulesDocument;
  enabledCategories: readonly string[] | null;
} {
  return {
    name: value.name,
    ...(value.description !== null ? { description: value.description } : {}),
    startUrls: [...value.startUrls],
    limits: { ...value.limits },
    aimd: { ...value.aimd },
    rendering: { ...value.rendering },
    respectRobotsTxt: value.respectRobotsTxt,
    virtualRobotsTxt: value.virtualRobotsTxt,
    scopeRules: value.scopeRules,
    enabledCategories: value.enabledCategories,
    redirectChecks: redirectChecksToWire(value.redirectChecks),
    captureMode: value.captureMode,
    captureSettings: captureSettingsToWire(value.captureSettings),
    ...(value.contentScope ? { contentScope: value.contentScope } : {}),
    ...(value.rewrite ? { rewrite: value.rewrite } : {}),
    ...(value.consent ? { consent: value.consent } : {}),
    ...(value.zoneWeights ? { zoneWeights: value.zoneWeights } : {})
  };
}

/** Update payload: full-field PUT (the Api replaces the configuration fields it receives). */
export function configurationToUpdateRequest(value: ConfigurationFormValue): ConfigurationUpdateRequest & {
  limits: CrawlLimits;
  aimd: AimdSettings;
  rendering: RenderingSettings;
  scopeRules: ScopeRulesDocument;
  enabledCategories: readonly string[] | null;
} {
  return {
    name: value.name,
    description: value.description,
    startUrls: [...value.startUrls],
    limits: { ...value.limits },
    aimd: { ...value.aimd },
    rendering: { ...value.rendering },
    respectRobotsTxt: value.respectRobotsTxt,
    virtualRobotsTxt: value.virtualRobotsTxt,
    scopeRules: value.scopeRules,
    enabledCategories: value.enabledCategories,
    redirectChecks: redirectChecksToWire(value.redirectChecks),
    captureMode: value.captureMode,
    captureSettings: captureSettingsToWire(value.captureSettings),
    ...(value.contentScope ? { contentScope: value.contentScope } : {}),
    ...(value.rewrite ? { rewrite: value.rewrite } : {}),
    ...(value.consent ? { consent: value.consent } : {}),
    ...(value.zoneWeights ? { zoneWeights: value.zoneWeights } : {})
  };
}

// ── Tabs, categories, presets ───────────────────────────────────────────────

/** The 13 configuration-editor tabs, VERBATIM (06/04 §1; ui_walker CONFIG_EDITOR_TABS). */
export const CONFIGURATION_TABS = [
  'Scope',
  'Crawl as and capture',
  'Limits',
  'Rules',
  'Directives',
  'Resources and external scope',
  'Authentication and cookies',
  'Click discovery and site types',
  'Consent states',
  'Analysis categories',
  'Thresholds',
  'Extraction and search',
  'Advanced'
] as const;

export type ConfigurationTab = (typeof CONFIGURATION_TABS)[number];

/** Stable tab ids (Mantine Tabs values). */
export const TAB_IDS: readonly string[] = [
  'scope', 'crawl-as', 'limits', 'rules', 'directives', 'resources', 'auth',
  'click-discovery', 'consent', 'categories', 'thresholds', 'extraction', 'advanced'
];

/** Analysis categories (AuditCategory enum names; scoring snapshot keys).
 * WP-10-A1 (D-021): the three §7.4 taxonomy categories appended by the
 * contract amendment — labels match AUDIT_CATEGORY_LABELS verbatim. */
export const ANALYSIS_CATEGORIES: readonly { readonly key: string; readonly label: string }[] = [
  { key: 'Indexability', label: 'Indexability' },
  { key: 'Content', label: 'Content' },
  { key: 'Linking', label: 'Linking' },
  { key: 'StructuredData', label: 'Structured data' },
  { key: 'Accessibility', label: 'Accessibility' },
  { key: 'Performance', label: 'Performance' },
  { key: 'Crawl', label: 'Crawl health' },
  { key: 'Custom', label: 'Custom extraction and search' },
  { key: 'LogIntelligence', label: 'Log intelligence' },
  { key: 'AiSearchGeo', label: 'AI search (GEO)' },
  { key: 'AnalyticsTagging', label: 'Analytics and tagging' },
  { key: 'Images', label: 'Images' },
  { key: 'SecurityAndProtocol', label: 'Security & protocol' },
  { key: 'Internationalization', label: 'Internationalization' }
];

/** "Crawl as" presets (§8.8): bundle user agent + capture identity. */
export const CRAWL_AS_PRESETS = [
  'CrawlForge default',
  'Googlebot Smartphone',
  'Googlebot Desktop',
  'Chrome mobile',
  'Chrome desktop',
  'Custom'
] as const;

export type CrawlAsPreset = (typeof CRAWL_AS_PRESETS)[number];

/** Bundled UA strings (local data; engine UserAgentProfiles + §8.8 presets). */
export const UA_STRINGS = {
  googlebotSmartphone:
    'Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.6422.76 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
  googlebotDesktop:
    'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
  chromeMobile:
    'Mozilla/5.0 (Linux; Android 10; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36',
  chromeDesktop:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36'
} as const;

/** Resolves the preset selection from the user-agent rules. */
export function presetFromUserAgent(rules: UserAgentRules): CrawlAsPreset {
  if (rules.customUserAgent) {
    if (rules.customUserAgent === UA_STRINGS.googlebotDesktop) return 'Googlebot Desktop';
    if (rules.customUserAgent === UA_STRINGS.chromeMobile) return 'Chrome mobile';
    if (rules.customUserAgent === UA_STRINGS.chromeDesktop) return 'Chrome desktop';
    return 'Custom';
  }
  return rules.simulateGooglebot ? 'Googlebot Smartphone' : 'CrawlForge default';
}

/** Applies a preset to the user-agent rules. */
export function userAgentForPreset(preset: CrawlAsPreset, current: UserAgentRules): UserAgentRules {
  switch (preset) {
    case 'CrawlForge default':
      return { customUserAgent: null, simulateGooglebot: false };
    case 'Googlebot Smartphone':
      return { customUserAgent: null, simulateGooglebot: true };
    case 'Googlebot Desktop':
      return { customUserAgent: UA_STRINGS.googlebotDesktop, simulateGooglebot: false };
    case 'Chrome mobile':
      return { customUserAgent: UA_STRINGS.chromeMobile, simulateGooglebot: false };
    case 'Chrome desktop':
      return { customUserAgent: UA_STRINGS.chromeDesktop, simulateGooglebot: false };
    case 'Custom':
      return { ...current, simulateGooglebot: false, customUserAgent: current.customUserAgent ?? '' };
  }
}

// ── Validation (client-side mirror of 03/01 §3.3/§4.3) ──────────────────────

/** AIMD validation errors keyed by field (empty = valid). */
export function validateAimd(aimd: AimdSettings): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!(aimd.initialRateR0 > 0)) errors.initialRateR0 = 'Initial rate R0 must be positive.';
  if (!(aimd.alpha > 0)) errors.alpha = 'Additive increase alpha must be positive.';
  if (!(aimd.beta > 0 && aimd.beta < 1)) errors.beta = 'Multiplicative decrease beta must be in (0, 1).';
  if (!Number.isInteger(aimd.kappa) || aimd.kappa < 1) errors.kappa = 'Concurrency cap kappa must be a positive integer.';
  if (!(aimd.ewmaTtfbWeight > 0 && aimd.ewmaTtfbWeight <= 1)) errors.ewmaTtfbWeight = 'EWMA weight must be in (0, 1].';
  return errors;
}

/** Limit override validation: null = unlimited; otherwise a positive integer (never clamped). */
export function validateLimit(value: number | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  if (!Number.isInteger(value) || value <= 0) return 'Override must be a positive integer, or unlimited.';
  return null;
}

/** True when the form is saveable (name, at least one seed, valid AIMD + limits). */
export function configurationFormErrors(value: ConfigurationFormValue): Record<string, string> {
  const errors: Record<string, string> = { ...validateAimd(value.aimd) };
  if (!value.name.trim()) errors.name = 'Configuration name is required.';
  if (value.startUrls.length === 0) errors.startUrls = 'At least one start URL is required.';
  for (const [key, limit] of Object.entries(value.limits)) {
    const problem = validateLimit(limit as number | null);
    if (problem) errors[`limits.${key}`] = problem;
  }
  return errors;
}

// ── Small shared helpers ────────────────────────────────────────────────────

/** Splits a textarea body into a trimmed, non-empty line list. */
export function splitLines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0);
}

/** Joins a line list for textarea display. */
export function joinLines(lines: readonly string[]): string {
  return lines.join('\n');
}

/** Toggles one analysis category; all-enabled collapses back to null. */
export function toggleCategory(current: readonly string[] | null, key: string, enabled: boolean): readonly string[] | null {
  const all = ANALYSIS_CATEGORIES.map((category) => category.key);
  const set = new Set(current ?? all);
  if (enabled) set.add(key);
  else set.delete(key);
  if (set.size === all.length) return null;
  return all.filter((category) => set.has(category));
}
