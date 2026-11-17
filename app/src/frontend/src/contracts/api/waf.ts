/**
 * WAF / challenge detection DTOs (additive, FS-CRAWL-002 §2.7).
 *
 * Routes: GET /api/audits/{id}/waf-blocks, GET /api/audits/{id}/waf-blocks/summary,
 * GET/POST /api/waf-signatures, PUT/DELETE /api/waf-signatures/{key}.
 *
 * Clean-room: detection and reporting only (E3) — verdicts derive from the
 * bundled local catalogue plus observed responses (E4); no challenge solving,
 * no remote signature services (E2).
 *
 * @module contracts/api/waf
 */

import type { EntityId, IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** Evidence supporting a WAF/challenge detection (local observation only). */
export interface WafEvidence {
  /** Response body snippet that matched the signature. */
  readonly snippet?: string | null;
  /** Observed response headers relevant to the verdict. */
  readonly headers?: Readonly<Record<string, string>>;
  /** Catalogue key of the matched signature, when signature-based. */
  readonly signatureKey?: string | null;
}

/**
 * A WAF/challenge detection against a fetched URL. 200-OK challenge pages
 * are fingerprinted by the local pass; `confidence` is in [0, 1].
 */
export interface WafDetection {
  /** Detected provider name (e.g. the WAF vendor identified locally). */
  readonly provider: string;
  /** Challenge class (e.g. JS challenge, interstitial, block page). */
  readonly challengeClass: string;
  /** Detection confidence in [0, 1]. */
  readonly confidence: number;
  /** Matched catalogue signature key, when applicable. */
  readonly signatureKey?: string | null;
  /** Supporting evidence from the observed response. */
  readonly evidence?: WafEvidence | null;
}

/** One blocked-URL row in GET /api/audits/{id}/waf-blocks. */
export interface WafBlock extends WafDetection {
  /** URL record identifier within the audit. */
  readonly urlId: EntityId;
  /** Canonical URL that was blocked/challenged. */
  readonly canonicalUrl: string;
  /** UTC detection time. */
  readonly detectedAt: IsoTimestamp;
}

/** Filters for the WAF block list (additive route). */
export interface WafBlockListQuery extends ListQuery {
  readonly provider?: string;
  readonly challengeClass?: string;
  readonly urlId?: EntityId;
}

/**
 * GET /api/audits/{id}/waf-blocks/summary — provider/class counts and block
 * rate. `blockRate` is blocked / fetched, in [0, 1].
 */
export interface WafBlocksSummary {
  readonly totalBlocked: number;
  readonly blockRate: number;
  readonly byProvider: Readonly<Record<string, number>>;
  readonly byChallengeClass: Readonly<Record<string, number>>;
}

/** Origin of a WAF signature: bundled catalogue or local user entry. */
export type WafSignatureSource = 'bundled' | 'user';

/**
 * One effective WAF signature. Bundled entries ship with the app (E2);
 * user entries are local additions/overrides.
 */
export interface WafSignature {
  /** Stable signature key (path parameter for update/delete). */
  readonly key: string;
  readonly provider: string;
  readonly challengeClass: string;
  /** Detection pattern (local catalogue format). */
  readonly pattern: string;
  readonly source: WafSignatureSource;
  readonly enabled: boolean;
  readonly description?: string | null;
}

/** GET /api/waf-signatures — effective catalogue (bundled version + user entries). */
export interface WafSignatureCatalogue {
  /** Version of the bundled catalogue shipped with the app. */
  readonly bundledVersion: string;
  readonly signatures: readonly WafSignature[];
}

/** Filters for the effective signature catalogue. */
export interface WafSignatureListQuery extends ListQuery {
  readonly provider?: string;
  readonly source?: WafSignatureSource;
}

/** POST /api/waf-signatures — create a local user signature. */
export interface WafSignatureCreateRequest {
  readonly key: string;
  readonly provider: string;
  readonly challengeClass: string;
  readonly pattern: string;
  readonly description?: string;
}

/** PUT /api/waf-signatures/{key} — update a user signature (partial). */
export interface WafSignatureUpdateRequest {
  readonly provider?: string;
  readonly challengeClass?: string;
  readonly pattern?: string;
  readonly enabled?: boolean;
  readonly description?: string | null;
}

/**
 * DELETE /api/waf-signatures/{key} — removing a user override restores the
 * bundled default when one exists for the key.
 */
export interface WafSignatureDeleteResponse {
  readonly key: string;
  readonly deleted: boolean;
  readonly bundledDefaultRestored: boolean;
}
