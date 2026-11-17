/**
 * Redirect migration matrix DTOs (additive, FS-CRAWL-002 §10.7).
 *
 * Routes: POST /api/audits/{id}/redirect-matrix (upload mapping; auto-seed
 * list-mode audit), GET /api/audits/{id}/redirect-matrix/results (per-row
 * verdicts, hop chains, canonical evidence).
 *
 * @module contracts/api/redirect-matrix
 */

import type { EntityId } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { RedirectHop } from './audits';

/** One migration mapping row: old URL -> expected new URL. */
export interface RedirectMatrixRow {
  readonly sourceUrl: string;
  readonly targetUrl: string;
}

/** POST /api/audits/{id}/redirect-matrix — upload migration mapping. */
export interface RedirectMatrixUploadRequest {
  readonly rows: readonly RedirectMatrixRow[];
  /** When true, auto-seed a list-mode audit from the source URLs. */
  readonly autoSeedAudit?: boolean;
}

/** Upload acceptance; the auto-seeded list-mode audit identifier when requested. */
export interface RedirectMatrixUploadResponse {
  readonly matrixId: EntityId;
  readonly rowCount: number;
  readonly auditId: EntityId | null;
}

/**
 * Per-row validation verdict. Verdict keys are local validator outcomes
 * (e.g. pass/fail/pending); the matrix-row-validated hub event carries the
 * same verdict values.
 */
export type MatrixRowVerdict = string;

/** One per-row result with hop chain and canonical evidence. */
export interface RedirectMatrixResult {
  readonly urlId: EntityId;
  readonly sourceUrl: string;
  readonly expectedTargetUrl: string;
  readonly verdict: MatrixRowVerdict;
  readonly hopChain: readonly RedirectHop[];
  readonly canonicalUrl: string | null;
  readonly evidence: string | null;
}

/** GET /api/audits/{id}/redirect-matrix/results — verdict filter, pagination. */
export interface RedirectMatrixResultsQuery extends ListQuery {
  readonly verdict?: MatrixRowVerdict;
}
