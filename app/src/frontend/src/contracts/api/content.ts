/**
 * Custom extraction, search, snippet, technology, and content-compare DTOs.
 *
 * Routes: POST /api/analysis/single-page, GET /api/audits/compare/content,
 * GET /api/audits/{id}/extracted-content (+ /summary), GET .../search-results,
 * GET .../snippet-results, GET .../technologies.
 *
 * Additive (FS-CRAWL-002 Appendix B): WAF verdict + scope diagnostics on
 * single-page analysis; hostMapping on content compare.
 *
 * @module contracts/api/content
 */

import type { EntityId } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { ElementInventory, Finding, ScopeDiagnostics } from './audits';
import type { WafDetection } from './waf';

/** POST /api/analysis/single-page — target URL, optional rendering flag, optional rule filters. */
export interface SinglePageAnalysisRequest {
  readonly url: string;
  readonly render?: boolean;
  /** Restrict analysis to these rule keys. */
  readonly ruleKeys?: readonly string[];
}

/**
 * Findings and element inventory for the page, plus additive WAF verdict
 * and scope diagnostics.
 */
export interface SinglePageAnalysisResponse {
  readonly findings: readonly Finding[];
  readonly elementInventory: ElementInventory;
  /** Additive: post-fetch WAF/challenge verdict. */
  readonly wafVerdict?: WafDetection | null;
  /** Additive: content-scope diagnostics. */
  readonly scopeDiagnostics?: ScopeDiagnostics | null;
}

/** GET /api/audits/compare/content — two audit identifiers; canonical URL; additive hostMapping. */
export interface ContentCompareQuery {
  readonly auditIdA: EntityId;
  readonly auditIdB: EntityId;
  readonly canonicalUrl: string;
  /** Additive: apply host mapping when aligning the page (FS-CRAWL-002 §10.7). */
  readonly hostMapping?: boolean;
}

/** Side-by-side difference of one content field. */
export interface ContentDiff {
  readonly before: string | null;
  readonly after: string | null;
  readonly changed: boolean;
}

/** One metadata field change between the two audits. */
export interface MetadataChange {
  readonly field: string;
  readonly before: string | null;
  readonly after: string | null;
}

/** Title/heading/text differences and metadata changes. */
export interface ContentCompareResponse {
  readonly canonicalUrl: string;
  readonly title: ContentDiff;
  readonly headings: readonly ContentDiff[];
  readonly text: ContentDiff;
  readonly metadataChanges: readonly MetadataChange[];
}

/**
 * Dynamic-column extraction value. Multi-valued cells expand to the busiest
 * page in the grid (FS-CRAWL-002 §8.3); values stay JSON scalars/arrays.
 */
export type ExtractionValue = string | number | boolean | null | readonly string[];

/** One dynamic-column row of custom extraction values (section 7.9). */
export interface ExtractedContentRow {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly ruleKey: string;
  /** Column name -> captured value(s). */
  readonly values: Readonly<Record<string, ExtractionValue>>;
}

/** GET /api/audits/{id}/extracted-content — rule filter, URL filter, column selection. */
export interface ExtractedContentQuery extends ListQuery {
  readonly ruleKey?: string;
  readonly urlId?: EntityId;
  /** Column selection; omitted = all columns. */
  readonly columns?: readonly string[];
}

/** Dynamic-column rows plus the effective column order. */
export interface ExtractedContentResponse {
  readonly columns: readonly string[];
  readonly rows: readonly ExtractedContentRow[];
}

/** Per-rule extraction summary (pages matched, values captured, discards, truncations). */
export interface ExtractionRuleSummary {
  readonly ruleKey: string;
  readonly pagesMatched: number;
  readonly valuesCaptured: number;
  readonly discards: number;
  readonly truncations: number;
}

/** GET /api/audits/{id}/extracted-content/summary response. */
export interface ExtractionSummaryResponse {
  readonly rules: readonly ExtractionRuleSummary[];
}

/** Per-item overview of custom search matches (section 7.16). */
export interface SearchItemOverview {
  readonly item: string;
  readonly matchCount: number;
  readonly urlCount: number;
}

/** One custom search match. */
export interface SearchMatch {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly item: string;
  readonly snippet: string | null;
  readonly occurrences: number;
}

/** GET /api/audits/{id}/search-results — item filter, URL filter, pagination. */
export interface SearchResultsQuery extends ListQuery {
  readonly item?: string;
  readonly urlId?: EntityId;
}

/** Custom search matches and per-item overview. */
export interface SearchResultsResponse {
  readonly overview: readonly SearchItemOverview[];
  readonly matches: readonly SearchMatch[];
}

/** One dynamic-column snippet output row (section 7.17). */
export interface SnippetResultRow {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly snippetKey: string;
  readonly values: Readonly<Record<string, ExtractionValue>>;
}

/** GET /api/audits/{id}/snippet-results — snippet filter, URL filter, column selection. */
export interface SnippetResultsQuery extends ListQuery {
  readonly snippetKey?: string;
  readonly urlId?: EntityId;
  readonly columns?: readonly string[];
}

/** Dynamic-column snippet outputs plus the effective column order. */
export interface SnippetResultsResponse {
  readonly columns: readonly string[];
  readonly rows: readonly SnippetResultRow[];
}

/** One technology detection with confidence (section 7.15); confidence in [0, 1]. */
export interface TechnologyDetection {
  readonly name: string;
  readonly category: string;
  readonly confidence: number;
  readonly version: string | null;
  readonly urlId: EntityId | null;
  readonly evidence: string | null;
}

/** GET /api/audits/{id}/technologies — category filter, pagination. */
export interface TechnologyListQuery extends ListQuery {
  readonly category?: string;
}
