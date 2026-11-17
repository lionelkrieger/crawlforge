/**
 * Semantic / embedding DTOs (additive, FS-CRAWL-002 §12.8).
 *
 * Routes: POST /api/audits/{id}/semantics/run, GET .../semantics/similar,
 * GET .../semantics/link-opportunities, GET .../semantics/clusters,
 * POST /api/settings/embedding/test.
 *
 * Clean-room (E7): embeddings come only from the bundled offline ONNX model
 * or a user-configured loopback-only daemon; non-loopback endpoints are
 * rejected at configuration. No remote embedding/vector/LLM APIs.
 *
 * @module contracts/api/semantics
 */

import type { EntityId } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** Embedding provider selection (E7: bundled ONNX or loopback daemon only). */
export type EmbeddingProvider = 'onnx' | 'loopback-daemon';

/** Lifecycle state of a local embedding job. */
export type EmbeddingJobState = 'queued' | 'running' | 'completed' | 'failed';

/** POST /api/audits/{id}/semantics/run — run or re-run the local embedding job. */
export interface SemanticsRunRequest {
  /** When true, re-embed pages that already have vectors. */
  readonly rerun?: boolean;
}

/** Immediate identifier response; progress flows over the hub. */
export interface SemanticsRunResponse {
  readonly auditId: EntityId;
  readonly jobId: EntityId;
  readonly state: EmbeddingJobState;
}

/** GET .../semantics/similar — top-K similar URLs by cosine similarity. */
export interface SemanticsSimilarQuery {
  /** Reference URL record. */
  readonly urlId: EntityId;
  /** Maximum neighbours to return. */
  readonly k?: number;
  /** Cosine similarity floor in [0, 1]. */
  readonly minSimilarity?: number;
}

/** One cosine-similarity neighbour. `similarity` is in [0, 1]. */
export interface SimilarUrl {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly similarity: number;
}

/** GET .../semantics/link-opportunities — semantic internal-link recommendations. */
export interface LinkOpportunityQuery extends ListQuery {
  readonly urlId?: EntityId;
  readonly minSimilarity?: number;
}

/** One recommended internal link, derived from local vectors only. */
export interface LinkOpportunity {
  readonly sourceUrlId: EntityId;
  readonly sourceCanonicalUrl: string;
  readonly targetUrlId: EntityId;
  readonly targetCanonicalUrl: string;
  readonly similarity: number;
  readonly anchorSuggestion?: string | null;
}

/** One member of a semantic cannibalization cluster. */
export interface SemanticClusterMember {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly similarity: number;
}

/** GET .../semantics/clusters — semantic cannibalization clusters. */
export interface SemanticCluster {
  readonly clusterId: EntityId;
  readonly label?: string | null;
  readonly centroidUrlId?: EntityId | null;
  readonly members: readonly SemanticClusterMember[];
}

/** Filters for the cluster view. */
export interface SemanticClusterQuery extends ListQuery {
  readonly minSimilarity?: number;
}

/**
 * POST /api/settings/embedding/test — loopback provider connection/model
 * test. `isLoopback` must be true for the provider to be acceptable (E7).
 */
export interface EmbeddingTestRequest {
  /** Candidate daemon endpoint; must resolve to loopback. */
  readonly endpoint: string;
  readonly model?: string;
}

/** Connection/model verdict for the loopback embedding provider. */
export interface EmbeddingTestResponse {
  readonly isLoopback: boolean;
  readonly reachable: boolean;
  readonly modelAvailable: boolean;
  readonly message?: string | null;
}

/**
 * Semantic relation attached to a URL detail record (additive field on
 * GET /api/audits/{id}/urls/{urlId}).
 */
export interface SemanticRelation {
  readonly relatedUrlId: EntityId;
  readonly relatedCanonicalUrl: string;
  readonly similarity: number;
  /** Relation kind, e.g. similar / cluster-mate / link-opportunity. */
  readonly kind: string;
}
