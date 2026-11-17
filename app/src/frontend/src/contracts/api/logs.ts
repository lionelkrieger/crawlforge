/**
 * Server-log import / correlation DTOs (additive, FS-CRAWL-002 §11.7).
 *
 * Routes: POST /api/projects/{id}/logs/import, GET /api/projects/{id}/logs,
 * DELETE /api/logs/{id}, GET /api/audits/{id}/logs/correlation,
 * POST /api/logs/{id}/verify-dns.
 *
 * Clean-room (E8): bot CIDR ranges and agent profiles are bundled; DNS
 * verification is the only optional network action — user-triggered, off by
 * default, per-import consent, locally logged. No remote log enrichment.
 *
 * @module contracts/api/logs
 */

import type { EntityId, IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** Log import lifecycle state. */
export type LogImportState = 'queued' | 'running' | 'completed' | 'failed';

/** Streaming import counters (shared with the log-import hub events). */
export interface LogImportCounters {
  readonly linesParsed: number;
  readonly linesAccepted: number;
  readonly linesRejected: number;
}

/** Correlation summary counts (shared with log-import-completed). */
export interface LogCorrelationSummary {
  /** Bot hits on URLs the crawl never discovered (orphans). */
  readonly orphanCount: number;
  /** Bot hits on non-indexable/low-value URLs (budget waste). */
  readonly budgetWasteCount: number;
  /** Indexable URLs with bot hits that the crawl missed. */
  readonly uncrawledIndexableCount: number;
}

/**
 * POST /api/projects/{id}/logs/import — import a local log file. Bodies are
 * JSON (§10.1); the file is referenced by its local path chosen through the
 * native file dialog. Returns immediately; progress flows over the hub.
 */
export interface LogImportRequest {
  readonly filePath: string;
  /** Log format key (e.g. combined/common/w3c); auto-detected when omitted. */
  readonly format?: string;
}

/** Immediate import identifier. */
export interface LogImportStartResponse {
  readonly importId: EntityId;
  readonly state: LogImportState;
}

/** One log import record (GET /api/projects/{id}/logs). */
export interface LogImport {
  readonly id: EntityId;
  readonly projectId: EntityId;
  readonly fileName: string;
  readonly format: string | null;
  readonly state: LogImportState;
  readonly startedAt: IsoTimestamp;
  readonly completedAt: IsoTimestamp | null;
  readonly counters: LogImportCounters;
  readonly correlation: LogCorrelationSummary | null;
  /** Whether the user consented to DNS bot verification for this import. */
  readonly dnsVerificationConsented: boolean;
}

/** GET /api/projects/{id}/logs — pagination. */
export type LogImportListQuery = ListQuery;

/** Correlation view selector (orphans / budget waste / uncrawled indexable). */
export type LogCorrelationView = 'orphans' | 'budget-waste' | 'uncrawled-indexable';

/** GET /api/audits/{id}/logs/correlation — view + import filters. */
export interface LogCorrelationQuery extends ListQuery {
  readonly view?: LogCorrelationView;
  readonly importId?: EntityId;
}

/** One correlation row joining log hits with crawl coverage. */
export interface LogCorrelationRow {
  readonly url: string;
  readonly canonicalUrl: string | null;
  readonly urlId: EntityId | null;
  readonly hits: number;
  readonly botHits: number;
  readonly lastSeen: IsoTimestamp | null;
}

/** Correlation summary plus the rows of the selected view. */
export interface LogCorrelationResponse {
  readonly summary: LogCorrelationSummary;
  readonly rows: readonly LogCorrelationRow[];
}

/**
 * POST /api/logs/{id}/verify-dns — optional user-triggered DNS bot
 * verification. `consent` must be true (per-import consent, E8); the action
 * is off by default and locally logged.
 */
export interface DnsVerifyRequest {
  readonly consent: boolean;
}

/** Acceptance verdict for the consented DNS verification run. */
export interface DnsVerifyResponse {
  readonly importId: EntityId;
  readonly started: boolean;
  readonly message: string | null;
}
