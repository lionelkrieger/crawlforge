/**
 * N-gram / topical density DTOs (additive, FS-CRAWL-002 §5.7).
 *
 * Routes: GET /api/audits/{id}/ngrams, GET /api/audits/{id}/ngrams/cannibalization,
 * GET /api/audits/{id}/ngrams/gaps. The analyzer is the local 4-zone n-gram
 * engine; all computation is offline.
 *
 * @module contracts/api/ngrams
 */

import type { EntityId } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/**
 * One n-gram row. `zone` is the analyzer zone key (4-zone model); `urlId`
 * is null/absent for site-summary rows and set for per-URL rows.
 * `density` is the topical density ratio in [0, 1] when computed.
 */
export interface NgramRow {
  readonly ngram: string;
  /** Gram size (1..n). */
  readonly n: number;
  readonly zone: string;
  readonly count: number;
  readonly density?: number;
  readonly urlId?: EntityId | null;
}

/**
 * GET /api/audits/{id}/ngrams — site-summary rows when `urlId` is omitted,
 * per-URL rows when set.
 */
export interface NgramListQuery extends ListQuery {
  readonly urlId?: EntityId;
  readonly zone?: string;
  /** Gram size filter. */
  readonly n?: number;
  readonly minCount?: number;
}

/** One page participating in an n-gram cannibalization cluster. */
export interface CannibalizationPage {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly occurrences: number;
}

/** GET /api/audits/{id}/ngrams/cannibalization — one cluster per contested n-gram. */
export interface NgramCannibalizationCluster {
  readonly ngram: string;
  readonly pages: readonly CannibalizationPage[];
}

/**
 * GET /api/audits/{id}/ngrams/gaps — topical gap row. `gapScore` expresses
 * the shortfall between expected and observed coverage (higher = larger gap).
 */
export interface TopicalGap {
  readonly ngram: string;
  readonly observedCount: number;
  readonly expectedCount: number;
  readonly gapScore: number;
}

/** Filters for cannibalization / gap views. */
export interface NgramAnalysisQuery extends ListQuery {
  readonly minCount?: number;
  readonly zone?: string;
}
