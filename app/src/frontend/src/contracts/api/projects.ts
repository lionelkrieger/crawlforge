/**
 * Project, configuration, and suppression DTOs.
 *
 * Routes: GET/POST /api/projects, GET/PUT/DELETE /api/projects/{id},
 * GET/POST /api/projects/{id}/configurations, GET/PUT/DELETE /api/configurations/{id},
 * POST /api/projects/{id}/suppressions, DELETE /api/projects/{id}/suppressions/{key}.
 *
 * Additive fields (FS-CRAWL-002 Appendix B): contentScope, rewrite/host-mapping,
 * consent/beacon, and zone-weight/SERP fields on project update and on the
 * configuration routes. Configuration fields compile the §5.1 references
 * present in the REST contract (limits, AIMD parameters, rendering, robots).
 *
 * @module contracts/api/projects
 */

import type { EntityId, IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { CaptureModeWire, PatternCaptureWire } from './vocabulary';

/**
 * Crawl limit overrides. `null`/absent means unlimited — the Unlimited
 * Limits Resolver applies; limits never vary with license/account/remote
 * input (exclusion #8).
 */
export interface CrawlLimits {
  readonly maxUrls?: number | null;
  readonly maxDepth?: number | null;
  readonly maxDurationSeconds?: number | null;
  readonly maxResponseBytes?: number | null;
}

/**
 * AIMD politeness parameters (FS-CRAWL-002 §3): initial rate R0, additive
 * increase alpha, multiplicative decrease beta, concurrency cap kappa, and
 * the EWMA weight applied to TTFB samples.
 */
export interface AimdSettings {
  /** Initial requests-per-second rate (R0). */
  readonly initialRateR0: number;
  /** Additive increase step (alpha). */
  readonly alpha: number;
  /** Multiplicative decrease factor (beta), in (0, 1). */
  readonly beta: number;
  /** Concurrency cap (kappa). */
  readonly kappa: number;
  /** EWMA smoothing weight for TTFB samples, in (0, 1]. */
  readonly ewmaTtfbWeight: number;
}

/** Headless rendering options for the crawl (Chromium pool). */
export interface RenderingSettings {
  readonly enabled: boolean;
  /** Device profile identifier used for rendering. */
  readonly deviceProfile?: string | null;
  readonly infiniteScroll?: boolean;
  readonly captureConsole?: boolean;
}

/** One content-scope profile (Scoped Visible Text Tree include/exclude lists). */
export interface ContentScopeProfile {
  readonly id: EntityId;
  readonly name: string;
  readonly includeSelectors: readonly string[];
  readonly excludeSelectors: readonly string[];
}

/**
 * Content Area Scoping settings (FS-CRAWL-002 §4). Selectors are resilient
 * CSS selectors (Finder picker "send to" target).
 */
export interface ContentScopeSettings {
  readonly includeSelectors: readonly string[];
  readonly excludeSelectors: readonly string[];
  readonly profiles?: readonly ContentScopeProfile[];
}

/** One on-the-fly regex URL rewrite rule. */
export interface UrlRewriteRule {
  readonly pattern: string;
  readonly replacement: string;
  readonly enabled?: boolean;
}

/** One host mapping entry (migration / canonical host equivalence). */
export interface HostMapping {
  readonly fromHost: string;
  readonly toHost: string;
}

/** Rewrite / host-mapping settings (FS-CRAWL-002 §10.7). */
export interface RewriteSettings {
  readonly rules: readonly UrlRewriteRule[];
  readonly hostMappings: readonly HostMapping[];
}

/** Consent / beacon detection settings (MarTech, Consent Mode v2). */
export interface ConsentSettings {
  readonly consentModeV2Enabled: boolean;
  readonly beaconDetectionEnabled: boolean;
}

/**
 * DOM-zone link-equity weights and SERP metric options. Zone keys are the
 * analyzer's DOM-zone classification keys; weights scale link equity.
 */
export interface ZoneWeightSettings {
  readonly zoneWeights: Readonly<Record<string, number>>;
  readonly serpMetricsEnabled?: boolean;
}

/**
 * Explicit per-URL-pattern capture override (FS-CRAWL-001 §5.6.1; WP-10-A1).
 * Explicit settings take precedence over adaptive switching and apply on top
 * of every capture mode.
 */
export interface CapturePatternOverride {
  /** URL path pattern the override applies to (for example "/blog/*"). */
  readonly pattern: string;
  /** Rendered or non-rendered capture for matching URLs. */
  readonly capture: PatternCaptureWire;
}

/**
 * Capture-mode settings document (FS-CRAWL-001 §5.6.1/§5.6.2; WP-10-A1).
 * Absent/null applies the engine defaults documented per field. Mirrors
 * CrawlForge.Contracts.Crawl.CaptureSettings; every knob is configurable and
 * none is license-driven (exclusion #8). The dual storage threshold warns
 * only — there is no hard cap.
 */
export interface CaptureSettings {
  /** Adaptive sample size per URL-pattern group; spec range 3–5, default 4. */
  readonly adaptiveSampleSizePerGroup?: number;
  /** Material-change visible word-count fraction; default 0.20 (20%). */
  readonly materialChangeWordCountThreshold?: number;
  /** Dual-mode stored-capture warning threshold in bytes; default 1 GiB, no hard cap. */
  readonly dualStorageWarningThresholdBytes?: number;
  /** Render wait-rule quiet period in ms; default 500. */
  readonly renderWaitQuietMs?: number;
  /** Render wait-rule hard cap in ms; default 20000. */
  readonly renderWaitHardCapMs?: number;
  /** Concurrent Chromium contexts; null/absent = half logical processors, minimum 2. */
  readonly renderingPoolSize?: number | null;
  /** Explicit per-pattern overrides; absent/empty = none. */
  readonly patternOverrides?: readonly CapturePatternOverride[] | null;
}

/**
 * Inappropriate-redirect check toggles (CR-029; WP-10-A1). Four independent
 * cases, all ON by default — absent/null means every check enabled. Mirrors
 * CrawlForge.Contracts.Crawl.RedirectCheckSettings; each finding names the
 * case, the triggering hop and the final target.
 */
export interface RedirectCheckSettings {
  /** Case 1: a hop redirects to a different host. Default true. */
  readonly crossHost?: boolean;
  /** Case 2: a hop uses a temporary status 302/303/307. Default true. */
  readonly temporaryStatus?: boolean;
  /** Case 3: the final target is non-indexable per CR-024. Default true. */
  readonly nonIndexableTarget?: boolean;
  /** Case 4: an HTTPS URL redirects to HTTP. Default true. */
  readonly httpsDowngrade?: boolean;
}

/**
 * Additive extension settings shared by PUT /api/projects/{id} and the
 * configuration routes (FS-CRAWL-002 Appendix B).
 */
export interface ExtensionSettings {
  readonly contentScope?: ContentScopeSettings;
  readonly rewrite?: RewriteSettings;
  readonly consent?: ConsentSettings;
  readonly zoneWeights?: ZoneWeightSettings;
}

/** Project summary row (GET /api/projects). */
export interface ProjectSummary {
  readonly id: EntityId;
  readonly name: string;
  readonly description: string | null;
  readonly tags: readonly string[];
  readonly scopeSummary: string | null;
  readonly createdAt: IsoTimestamp;
  readonly updatedAt: IsoTimestamp;
}

/** Full project record, including additive extension settings. */
export interface Project extends ProjectSummary, ExtensionSettings {}

/** GET /api/projects — pagination and search text. */
export interface ProjectListQuery extends ListQuery {
  readonly search?: string;
}

/** POST /api/projects — name, description, tags, scope summary. */
export interface ProjectCreateRequest {
  readonly name: string;
  readonly description?: string;
  readonly tags?: readonly string[];
  readonly scopeSummary?: string;
}

/** PUT /api/projects/{id} — project fields plus additive extension settings. */
export interface ProjectUpdateRequest extends ExtensionSettings {
  readonly name?: string;
  readonly description?: string | null;
  readonly tags?: readonly string[];
  readonly scopeSummary?: string | null;
}

/** Configuration summary row (GET /api/projects/{id}/configurations). */
export interface ConfigurationSummary {
  readonly id: EntityId;
  readonly projectId: EntityId;
  readonly name: string;
  readonly description: string | null;
  readonly createdAt: IsoTimestamp;
  readonly updatedAt: IsoTimestamp;
}

/**
 * Additive capture-mode configuration fields (WP-10-A1, D-021) shared by the
 * configuration read/create/update wire shapes. Absent/null keeps the baseline
 * non-JS behaviour with engine-default settings and all CR-029 redirect checks
 * enabled. Mirrors the additive CrawlConfigurationDto members CaptureMode,
 * CaptureSettings and RedirectChecks.
 */
export interface CaptureConfigurationFields {
  /** Capture mode (§5.6.1); null/absent = baseline non-JS. New configurations default to non-js. */
  readonly captureMode?: CaptureModeWire | null;
  /** Capture-mode settings document (§5.6.1/§5.6.2 knobs); null/absent = engine defaults. */
  readonly captureSettings?: CaptureSettings | null;
  /** CR-029 inappropriate-redirect toggles; null/absent = all four checks on. */
  readonly redirectChecks?: RedirectCheckSettings | null;
}

/**
 * Full audit configuration. Fields compile the §5.1 configuration surface
 * referenced by the REST contract: seed URLs, limits, AIMD politeness,
 * rendering, robots handling — plus the additive extension settings.
 */
export interface AuditConfiguration extends ConfigurationSummary, ExtensionSettings, CaptureConfigurationFields {
  /** Seed URLs for the crawl. */
  readonly startUrls: readonly string[];
  readonly limits: CrawlLimits;
  readonly aimd: AimdSettings;
  readonly rendering: RenderingSettings;
  /** Honor robots.txt rules during the crawl. */
  readonly respectRobotsTxt: boolean;
  /** Virtual robots.txt body applied when the target serves none. */
  readonly virtualRobotsTxt?: string | null;
}

/** POST /api/projects/{id}/configurations — configuration fields (project from path). */
export interface ConfigurationCreateRequest extends ExtensionSettings, CaptureConfigurationFields {
  readonly name: string;
  readonly description?: string;
  readonly startUrls: readonly string[];
  readonly limits?: CrawlLimits;
  readonly aimd?: AimdSettings;
  readonly rendering?: RenderingSettings;
  readonly respectRobotsTxt?: boolean;
  readonly virtualRobotsTxt?: string | null;
}

/** PUT /api/configurations/{id} — any subset of configuration fields. */
export interface ConfigurationUpdateRequest extends ExtensionSettings, CaptureConfigurationFields {
  readonly name?: string;
  readonly description?: string | null;
  readonly startUrls?: readonly string[];
  readonly limits?: CrawlLimits;
  readonly aimd?: AimdSettings;
  readonly rendering?: RenderingSettings;
  readonly respectRobotsTxt?: boolean;
  readonly virtualRobotsTxt?: string | null;
}

/** Finding suppression scope (rule key and/or finding scope, §7.5). */
export interface SuppressionScope {
  readonly urlId?: EntityId;
  readonly urlPattern?: string;
}

/** POST /api/projects/{id}/suppressions — rule key and/or finding scope. */
export interface SuppressionCreateRequest {
  readonly ruleKey?: string;
  readonly scope?: SuppressionScope;
}

/** Suppression state returned by create/delete (section 7.5). */
export interface SuppressionState {
  /** Stable suppression key (path parameter for unsuppress). */
  readonly key: string;
  readonly projectId: EntityId;
  readonly ruleKey: string | null;
  readonly scope: SuppressionScope | null;
  readonly suppressed: boolean;
  readonly createdAt: IsoTimestamp;
}

/** Generic deletion confirmation for DELETE routes. */
export interface DeletionConfirmation {
  readonly id: EntityId;
  readonly deleted: boolean;
}
