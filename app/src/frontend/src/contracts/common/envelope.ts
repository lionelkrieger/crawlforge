/**
 * Uniform REST response envelope.
 *
 * Spec surface: FS-CRAWL-001 §10.1 — "Uniform response envelope: a data
 * payload, optional pagination metadata, and an optional error object
 * (machine-readable code plus human-readable message)." §10.4 — validation
 * errors return field-level reasons in the error envelope.
 *
 * Exceptions (not JSON-enveloped): GET /metrics returns Prometheus-class text
 * and GET /api/exports/{id}/file returns a binary stream; the route catalog
 * marks both with `enveloped: false`.
 *
 * @module contracts/common/envelope
 */

import type { PaginationMeta } from './pagination';

/**
 * Machine-readable error plus human-readable message.
 * `code` is a stable local error identifier; no vendor/remote codes exist.
 */
export interface ApiError {
  /** Machine-readable error code (stable, local). */
  readonly code: string;
  /** Human-readable error message (localizable). */
  readonly message: string;
  /**
   * Field-level validation reasons keyed by field name
   * (FS-CRAWL-001 §10.4). Present on validation errors only.
   */
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
}

/**
 * Uniform response envelope for every JSON route.
 * On success `error` is absent; on failure `data` is null and `error` is set.
 */
export interface ApiEnvelope<TData> {
  /** Response payload; null when the request failed. */
  readonly data: TData | null;
  /** Present on paginated list responses. */
  readonly pagination?: PaginationMeta;
  /** Present when the request failed. */
  readonly error?: ApiError;
}

/** Envelope narrowed to a successful, non-null payload (no error object). */
export type ApiSuccessEnvelope<TData> = ApiEnvelope<TData> & {
  readonly data: TData;
  readonly error?: undefined;
};

/** Envelope narrowed to a failure: null data plus a required error object. */
export type ApiErrorEnvelope = ApiEnvelope<null> & {
  readonly data: null;
  readonly error: ApiError;
};
