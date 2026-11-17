/**
 * Audit lifecycle and report DTOs.
 *
 * Routes: POST/GET /api/audits, GET /api/audits/{id}, POST .../pause|resume|cancel|reaudit,
 * GET .../urls, GET .../urls/{urlId}, GET .../links, GET .../findings, GET .../summary,
 * GET .../charts/{metric}, GET .../graph, GET /api/audits/compare, GET .../scores.
 *
 * Additive fields (FS-CRAWL-002 Appendix B): blockReason/wafProvider URL filters,
 * WAF detection / scope diagnostics / top n-grams / serp_metrics / header-conflict
 * detail / semantic relations on URL detail, WAF counts + throttle summary on the
 * dashboard summary, waf-blocks / ngram-top / link-equity-weighted chart metrics,
 * domZone filter + weighted-equity fields on links, hostMapping on compare,
 * urlList/listMode on audit start.
 *
 * @module contracts/api/audits
 */

import type { EntityId, IsoTimestamp, JsonValue } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { CrawlLimits } from './projects';
import type { WafDetection } from './waf';
import type { NgramRow } from './ngrams';
import type { SemanticRelation } from './semantics';

/** Audit lifecycle states (start/pause/resume/cancel + terminal outcomes). */
export type AuditState = 'queued' | 'running' | 'paused' | 'completed' | 'failed' | 'cancelled';

/** HTTP status class of a fetched URL (standard classes). */
export type HttpStatusClass = '1xx' | '2xx' | '3xx' | '4xx' | '5xx';

/**
 * Crawl counters shared by audit summaries, crawl-progress, and
 * audit-completed. `wafBlocked` is the additive WAF sub-count
 * (FS-CRAWL-002 Appendix C).
 */
export interface AuditCounters {
  readonly queued: number;
  readonly fetched: number;
  readonly error: number;
  readonly blocked: number;
  /** Additive: URLs blocked/challenged by a detected WAF (sub-count of blocked). */
  readonly wafBlocked?: number;
}

/** POST /api/audits — configuration reference, optional limit overrides; returns immediately. */
export interface AuditStartRequest {
  readonly configurationId: EntityId;
  readonly limitOverrides?: CrawlLimits;
  /** Additive: explicit URL list for list-mode ingestion. */
  readonly urlList?: readonly string[];
  /** Additive: enable list-mode audit semantics. */
  readonly listMode?: boolean;
}

/** Audit identifier and initial state; progress flows over the hub. */
export interface AuditStartResponse {
  readonly auditId: EntityId;
  readonly state: AuditState;
}

/** Audit summary row with counters (GET /api/audits). */
export interface AuditSummary {
  readonly id: EntityId;
  readonly projectId: EntityId;
  readonly configurationId: EntityId;
  readonly state: AuditState;
  /** Current pipeline phase identifier. */
  readonly phase: string;
  readonly startedAt: IsoTimestamp;
  readonly completedAt: IsoTimestamp | null;
  readonly counters: AuditCounters;
}

/** GET /api/audits — project filter, status filter, pagination. */
export interface AuditListQuery extends ListQuery {
  readonly projectId?: EntityId;
  readonly state?: AuditState;
}

/** GET /api/audits/{id} — audit detail, counters, phase. */
export interface AuditDetail extends AuditSummary {
  readonly elapsedSeconds: number;
  readonly throughputPerSecond: number;
  readonly estimatedRemainderSeconds: number | null;
}

/** POST /api/audits/{id}/pause|resume|cancel — new state. */
export interface AuditStateResponse {
  readonly auditId: EntityId;
  readonly state: AuditState;
}

/** POST /api/audits/{id}/reaudit — optional refresh flag. */
export interface ReauditRequest {
  /** When true, refresh stored content before re-auditing. */
  readonly refresh?: boolean;
}

/** New audit identifier linked to the same dataset. */
export interface ReauditResponse {
  readonly auditId: EntityId;
  readonly sourceAuditId: EntityId;
  readonly state: AuditState;
}

/** URL record summary row (GET /api/audits/{id}/urls). */
export interface UrlRecordSummary {
  readonly id: EntityId;
  readonly canonicalUrl: string;
  readonly statusCode: number;
  readonly statusClass: HttpStatusClass;
  readonly contentType: string | null;
  readonly depth: number;
  readonly indexable: boolean;
  readonly title: string | null;
  /** Additive: block reason for blocked entries. */
  readonly blockReason?: string | null;
  /** Additive: detected WAF provider for blocked entries. */
  readonly wafProvider?: string | null;
}

/**
 * GET /api/audits/{id}/urls filters: status class, content type, depth,
 * indexability, text search — plus additive blockReason/wafProvider filters.
 */
export interface UrlListQuery extends ListQuery {
  readonly statusClass?: HttpStatusClass;
  readonly contentType?: string;
  readonly depth?: number;
  readonly indexable?: boolean;
  readonly search?: string;
  /** Additive filter (FS-CRAWL-002 §2.7). */
  readonly blockReason?: string;
  /** Additive filter (FS-CRAWL-002 §2.7). */
  readonly wafProvider?: string;
}

/** One hop in a redirect chain. */
export interface RedirectHop {
  readonly url: string;
  readonly statusCode: number;
  readonly location: string | null;
}

/** Per-element-type counts of the page element inventory. */
export interface ElementInventory {
  readonly counts: Readonly<Record<string, number>>;
  readonly totalElements: number;
}

/** Evidence attached to a finding (local observation only). */
export interface FindingEvidence {
  readonly snippet?: string | null;
  readonly selector?: string | null;
  readonly details?: Readonly<Record<string, JsonValue>>;
}

/**
 * One audit finding. Severity/category/ruleKey are local rule catalogue
 * keys (string-typed; the catalogue is bundled, never remote).
 */
export interface Finding {
  readonly id: EntityId;
  readonly ruleKey: string;
  readonly severity: string;
  readonly category: string;
  readonly urlId: EntityId | null;
  readonly message: string;
  readonly evidence?: FindingEvidence | null;
}

/** GET /api/audits/{id}/findings — severity, category, rule key, URL filters. */
export interface FindingListQuery extends ListQuery {
  readonly severity?: string;
  readonly category?: string;
  readonly ruleKey?: string;
  readonly urlId?: EntityId;
}

/** Scope diagnostics for a URL (additive, FS-CRAWL-002 §4). */
export interface ScopeDiagnostics {
  readonly included: boolean;
  readonly matchedIncludeSelectors: readonly string[];
  readonly matchedExcludeSelectors: readonly string[];
  readonly scopedWordCount: number;
  readonly totalWordCount: number;
}

/** SERP preview metrics for a URL (additive `serp_metrics` field). */
export interface SerpMetrics {
  readonly titlePixelWidth: number;
  readonly descriptionPixelWidth: number;
  readonly titleTruncated: boolean;
  readonly descriptionTruncated: boolean;
}

/** Header-vs-HTML conflict detail (additive, indexability equivalency). */
export interface HeaderConflictDetail {
  readonly headerName: string;
  readonly headerValue: string;
  readonly htmlValue: string | null;
  readonly conflict: boolean;
}

/** Link edge row (GET /api/audits/{id}/links). */
export interface LinkEdge {
  readonly sourceUrlId: EntityId;
  readonly targetUrlId: EntityId;
  readonly sourceUrl: string;
  readonly targetUrl: string;
  readonly internal: boolean;
  readonly anchorText: string | null;
  /** Additive: DOM-zone classification key of the link. */
  readonly domZone?: string;
  /** Additive: zone weight applied to this edge. */
  readonly equityWeight?: number;
  /** Additive: weighted link-equity value. */
  readonly weightedEquity?: number;
}

/** GET /api/audits/{id}/links — source/target filters, internal/external, domZone (additive). */
export interface LinkListQuery extends ListQuery {
  readonly sourceUrlId?: EntityId;
  readonly targetUrlId?: EntityId;
  readonly internal?: boolean;
  /** Additive filter (FS-CRAWL-002 Appendix B). */
  readonly domZone?: string;
}

/**
 * GET /api/audits/{id}/urls/{urlId} — full record, element inventory,
 * findings, inbound/outbound links, redirect chain, plus additive detail:
 * WAF detection, scope diagnostics, top n-grams, serp_metrics,
 * header-conflict detail, semantic relations.
 */
export interface UrlDetail extends UrlRecordSummary {
  readonly responseHeaders: Readonly<Record<string, string>>;
  readonly elementInventory: ElementInventory;
  readonly findings: readonly Finding[];
  readonly inboundLinks: readonly LinkEdge[];
  readonly outboundLinks: readonly LinkEdge[];
  readonly redirectChain: readonly RedirectHop[];
  /** Additive: WAF/challenge detection for this URL. */
  readonly wafDetection?: WafDetection | null;
  /** Additive: content-scope diagnostics. */
  readonly scopeDiagnostics?: ScopeDiagnostics | null;
  /** Additive: top n-grams for this URL. */
  readonly topNgrams?: readonly NgramRow[];
  /** Additive: SERP preview metrics (`serp_metrics`). */
  readonly serpMetrics?: SerpMetrics | null;
  /** Additive: header-vs-HTML conflict detail. */
  readonly headerConflicts?: readonly HeaderConflictDetail[];
  /** Additive: semantic relations from local embeddings. */
  readonly semanticRelations?: readonly SemanticRelation[];
}

/** Crawl health card of the dashboard summary. */
export interface CrawlHealth {
  readonly totalUrls: number;
  readonly fetched: number;
  readonly errorCount: number;
  readonly blockedCount: number;
  readonly averageTtfbMs: number | null;
}

/** Additive throttle summary on the dashboard (FS-CRAWL-002 §3.7). */
export interface ThrottleSummary {
  readonly throttledHostCount: number;
  readonly meanEffectiveRate: number;
  readonly adjustmentCount: number;
}

/**
 * GET /api/audits/{id}/summary — metric cards: counts by status/severity/
 * category, crawl health; additive WAF counts and throttle summary.
 */
export interface AuditDashboardSummary {
  readonly countsByStatus: Readonly<Record<string, number>>;
  readonly countsBySeverity: Readonly<Record<string, number>>;
  readonly countsByCategory: Readonly<Record<string, number>>;
  readonly crawlHealth: CrawlHealth;
  /** Additive: WAF provider/class counts and block rate. */
  readonly waf?: {
    readonly totalBlocked: number;
    readonly blockRate: number;
    readonly byProvider: Readonly<Record<string, number>>;
    readonly byChallengeClass: Readonly<Record<string, number>>;
  };
  /** Additive: host-throttle rollup. */
  readonly throttle?: ThrottleSummary;
}

/**
 * Chart metric identifier. Additive metrics (FS-CRAWL-002 Appendix B):
 * `waf-blocks`, `ngram-top`, `link-equity-weighted`; baseline metrics are
 * the aggregate families of §8.3 (status/severity histograms, CWV, scores).
 */
export type ChartMetric = string;

/** GET /api/audits/{id}/charts/{metric} — dimension filters, bucketing options. */
export interface ChartSeriesQuery {
  readonly dimensions?: Readonly<Record<string, string>>;
  readonly bucketSize?: number;
  readonly bucketBy?: string;
}

/** One pre-bucketed series point (label, value, dimensions). */
export interface ChartPoint {
  readonly label: string;
  readonly value: number;
  readonly dimensions?: Readonly<Record<string, string | number>>;
}

/** Chart series response; data arrives pre-bucketed for ECharts. */
export interface ChartSeriesResponse {
  readonly metric: ChartMetric;
  readonly points: readonly ChartPoint[];
}

/** Graph types enumerated by the REST contract (§10.2). */
export type GraphType = 'structure-tree' | 'link-network' | 'redirect-chains';

/** GET /api/audits/{id}/graph — graph type, depth/size limits. */
export interface GraphQuery {
  readonly type: GraphType;
  readonly maxDepth?: number;
  readonly maxNodes?: number;
  readonly maxEdges?: number;
}

/** Graph node; sizing by link-equity percentile, coloring by status/depth. */
export interface GraphNode {
  readonly id: EntityId;
  readonly url: string;
  readonly label: string | null;
  readonly statusClass: HttpStatusClass | null;
  readonly depth: number;
  readonly linkEquityPercentile: number | null;
}

/** Graph edge. */
export interface GraphEdge {
  readonly source: EntityId;
  readonly target: EntityId;
  readonly weight: number | null;
}

/**
 * Nodes and edges with full totals so the UI can show "showing X of Y"
 * when the server aggregates/samples beyond the render cap.
 */
export interface GraphData {
  readonly nodes: readonly GraphNode[];
  readonly edges: readonly GraphEdge[];
  readonly totalNodeCount: number;
  readonly totalEdgeCount: number;
  readonly truncated: boolean;
}

/** GET /api/audits/compare — two audit identifiers; additive hostMapping flag. */
export interface AuditCompareQuery {
  readonly auditIdA: EntityId;
  readonly auditIdB: EntityId;
  /** Additive: apply host mapping when aligning URLs (FS-CRAWL-002 §10.7). */
  readonly hostMapping?: boolean;
}

/** One metric delta between two audits. */
export interface MetricDelta {
  readonly metric: string;
  readonly before: number;
  readonly after: number;
  readonly delta: number;
}

/** New/resolved findings and metric deltas. */
export interface AuditCompareResponse {
  readonly newFindings: readonly Finding[];
  readonly resolvedFindings: readonly Finding[];
  readonly metricDeltas: readonly MetricDelta[];
}

/** One category score (section 8.1); scores are 0..100. */
export interface CategoryScore {
  readonly category: string;
  readonly score: number;
  readonly findingCount: number;
}

/** GET /api/audits/{id}/scores — per-category and overall scores. */
export interface AuditScores {
  readonly overall: number;
  readonly categories: readonly CategoryScore[];
}
