/**
 * Export job DTOs.
 *
 * Routes: POST /api/exports, GET /api/exports/{id}/file.
 * Additive datasets (FS-CRAWL-002 Appendix B): waf-blocks, ngrams,
 * redirect-matrix, semantic-opportunity, analytics.
 *
 * Clean-room: destinations are user-configured (exclusion #7 — user-configured
 * destinations are not vendor services); delivery is local/user-owned only.
 *
 * @module contracts/api/exports
 */

import type { EntityId } from '../common/primitives';

/** Export file formats (§10.2: spreadsheet, CSV, HTML bundle, PDF). */
export type ExportFormat = 'xlsx' | 'csv' | 'html' | 'pdf';

/**
 * Dataset selection keys. Baseline datasets cover the core report tables;
 * additive keys (FS-CRAWL-002): `waf-blocks`, `ngrams`, `redirect-matrix`,
 * `semantic-opportunity`, `analytics`.
 */
export type ExportDataset = string;

/** Export job lifecycle state. */
export type ExportJobState = 'queued' | 'running' | 'completed' | 'failed';

/** POST /api/exports — audit reference, format, dataset selection. */
export interface ExportCreateRequest {
  readonly auditId: EntityId;
  readonly format: ExportFormat;
  readonly datasets: readonly ExportDataset[];
  /** Optional user-configured delivery destination. */
  readonly destinationId?: EntityId;
}

/** Export job identifier; long operations return immediately. */
export interface ExportCreateResponse {
  readonly exportId: EntityId;
  readonly state: ExportJobState;
}

/**
 * GET /api/exports/{id}/file — local file stream (spreadsheet, CSV, HTML
 * bundle, or PDF). NOT JSON-enveloped; the typed client returns a Blob.
 */
export type ExportFileResponse = Blob;

/** Destination delivery states (§10.3 export-delivery event). */
export type ExportDeliveryState = 'delivered' | 'retrying' | 'failed';
