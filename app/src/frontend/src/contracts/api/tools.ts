/**
 * Standalone tool DTOs (section 8.7 + additive tools).
 *
 * Routes: POST /api/tools/robots-test, /api/tools/url-check,
 * /api/tools/structured-data-check, /api/tools/render-test, /api/tools/sitemap-generate;
 * additive: /api/tools/scope-preview (§4.6), /api/tools/rewrite-preview (§10.7),
 * /api/tools/llms-generate and /api/tools/llms-validate (§13.5).
 *
 * Clean-room: GEO generation writes local files only (E9); no remote
 * registries or publishing. Render preview loads only the crawled target.
 *
 * @module contracts/api/tools
 */

import type { EntityId } from '../common/primitives';
import type { HttpStatusClass, RedirectHop, ScopeDiagnostics } from './audits';
import type { ContentScopeSettings, HostMapping, UrlRewriteRule } from './projects';
import type { WafDetection } from './waf';

/** POST /api/tools/robots-test — robots body or URL; test URLs; user-agent. */
export interface RobotsTestRequest {
  /** Robots.txt body (mutually exclusive with robotsUrl). */
  readonly robotsBody?: string;
  /** Robots.txt URL to fetch from the crawl target. */
  readonly robotsUrl?: string;
  readonly testUrls: readonly string[];
  readonly userAgent: string;
}

/** Allow/disallow verdict and matched rule for one test URL. */
export interface RobotsTestResult {
  readonly url: string;
  readonly allowed: boolean;
  readonly matchedRule: string | null;
}

/** Per-URL robots verdicts. */
export interface RobotsTestResponse {
  readonly results: readonly RobotsTestResult[];
}

/** POST /api/tools/url-check — target URL. */
export interface UrlCheckRequest {
  readonly url: string;
}

/**
 * Status, redirect chain, headers, indexability — plus additive WAF verdict
 * and scope diagnostics (FS-CRAWL-002 Appendix B).
 */
export interface UrlCheckResponse {
  readonly statusCode: number;
  readonly statusClass: HttpStatusClass;
  readonly redirectChain: readonly RedirectHop[];
  readonly headers: Readonly<Record<string, string>>;
  readonly indexable: boolean;
  /** Additive: WAF/challenge verdict. */
  readonly wafVerdict?: WafDetection | null;
  /** Additive: content-scope diagnostics. */
  readonly scopeDiagnostics?: ScopeDiagnostics | null;
}

/** Structured-data markup formats handled by the checker (JSON-LD/Microdata). */
export type StructuredDataFormat = 'json-ld' | 'microdata';

/** POST /api/tools/structured-data-check — markup or URL. */
export interface StructuredDataCheckRequest {
  readonly markup?: string;
  readonly url?: string;
}

/** One parsed structured-data block with validation/rich-result outcome. */
export interface StructuredDataBlock {
  readonly format: StructuredDataFormat;
  /** Schema type (e.g. the @type value). */
  readonly type: string;
  readonly properties: Readonly<Record<string, unknown>>;
  readonly valid: boolean;
  readonly richResultEligible: boolean;
  readonly issues: readonly string[];
}

/** Parsed blocks with validation/rich-result outcomes. */
export interface StructuredDataCheckResponse {
  readonly blocks: readonly StructuredDataBlock[];
}

/** Console message captured during rendering. */
export interface ConsoleMessage {
  readonly level: 'log' | 'info' | 'warn' | 'error' | 'debug';
  readonly text: string;
  readonly timestamp: string | null;
}

/** POST /api/tools/render-test — target URL; device profile; additive picker support. */
export interface RenderTestRequest {
  readonly url: string;
  readonly deviceProfile?: string;
  /** Additive: enable the CSS/XPath selector picker in the preview. */
  readonly picker?: boolean;
}

/**
 * Screenshot, rendered document, console messages — plus additive
 * post-render WAF verdict and picker support flag.
 */
export interface RenderTestResponse {
  /** PNG screenshot as base64 (no data-URL prefix). */
  readonly screenshotBase64: string | null;
  readonly renderedHtml: string;
  readonly consoleMessages: readonly ConsoleMessage[];
  /** Additive: post-render WAF/challenge verdict. */
  readonly wafVerdict?: WafDetection | null;
  /** Additive: whether the selector picker is active for this render. */
  readonly pickerActive?: boolean;
}

/** Sitemap change-frequency values (sitemap protocol). */
export type SitemapChangeFrequency =
  | 'always'
  | 'hourly'
  | 'daily'
  | 'weekly'
  | 'monthly'
  | 'yearly'
  | 'never';

/** POST /api/tools/sitemap-generate — audit reference; filters; change-frequency/priority options. */
export interface SitemapGenerateRequest {
  readonly auditId: EntityId;
  /** URL filters (status class, indexability, depth, ...). */
  readonly filters?: {
    readonly statusClass?: HttpStatusClass;
    readonly indexable?: boolean;
    readonly depth?: number;
    readonly search?: string;
  };
  readonly changeFrequency?: SitemapChangeFrequency;
  /** Priority in [0, 1]. */
  readonly priority?: number;
  readonly includeLastmod?: boolean;
}

/** Generated sitemap file (or index); written locally (E9). */
export interface SitemapGenerateResponse {
  readonly fileName: string;
  readonly filePath: string;
  readonly urlCount: number;
  readonly isIndex: boolean;
}

/** POST /api/tools/scope-preview — scope preview against a sample URL/artifact. */
export interface ScopePreviewRequest {
  /** Sample URL to fetch and preview (mutually exclusive with artifactId). */
  readonly url?: string;
  /** Stored rendered artifact identifier. */
  readonly artifactId?: EntityId;
  readonly contentScope: ContentScopeSettings;
}

/** Scope preview outcome for the sample. */
export interface ScopePreviewResponse {
  readonly included: boolean;
  readonly matchedIncludeSelectors: readonly string[];
  readonly matchedExcludeSelectors: readonly string[];
  readonly scopedTextPreview: string | null;
}

/** POST /api/tools/rewrite-preview — sample URL rewriting before/after. */
export interface RewritePreviewRequest {
  readonly urls: readonly string[];
  /** Candidate rules; when omitted, the project's configured rules apply. */
  readonly rules?: readonly UrlRewriteRule[];
  readonly hostMappings?: readonly HostMapping[];
}

/** Before/after result for one sample URL. */
export interface RewritePreviewResult {
  readonly originalUrl: string;
  readonly rewrittenUrl: string;
  readonly appliedRulePatterns: readonly string[];
}

/** Sample URL rewriting results. */
export interface RewritePreviewResponse {
  readonly results: readonly RewritePreviewResult[];
}

/** llms.txt generation variants (FS-CRAWL-002 §13.5). */
export type LlmsVariant = 'llms-txt' | 'llms-full-txt';

/** POST /api/tools/llms-generate — generate llms.txt / llms-full.txt (local export). */
export interface LlmsGenerateRequest {
  readonly auditId: EntityId;
  readonly variant: LlmsVariant;
  /** Restrict generation to these hosts. */
  readonly hosts?: readonly string[];
}

/** Generation result; the file is written locally only (E9). */
export interface LlmsGenerateResponse {
  readonly variant: LlmsVariant;
  readonly fileName: string;
  readonly filePath: string;
}

/** POST /api/tools/llms-validate — validate llms.txt body or URL. */
export interface LlmsValidateRequest {
  readonly body?: string;
  readonly url?: string;
}

/** Validation outcome with issue list. */
export interface LlmsValidateResponse {
  readonly valid: boolean;
  readonly issues: readonly string[];
}
