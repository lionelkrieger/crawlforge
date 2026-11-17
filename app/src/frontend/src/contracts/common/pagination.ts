/**
 * Pagination, filtering, and sorting contract types.
 *
 * Spec surface: FS-CRAWL-001 §10.1 — "List endpoints accept pagination
 * (offset/limit), filtering, and sorting." Server-side multi-column sorting
 * backs the virtualized grids (FS-CRAWL-002 §8.3).
 *
 * @module contracts/common/pagination
 */

/** Sort direction for server-side sorting. */
export type SortOrder = 'asc' | 'desc';

/** Offset/limit pagination query accepted by every list endpoint. */
export interface PaginationQuery {
  /** Zero-based row offset. Defaults to 0 when omitted. */
  readonly offset?: number;
  /** Maximum rows to return. The server applies its own upper bound. */
  readonly limit?: number;
}

/**
 * Base query for list endpoints: pagination plus single-column sort.
 * Domain-specific filters extend this interface. Multi-column sorting is
 * expressed by repeating the sort field server-side; the contract keeps the
 * canonical `sort`/`order` pair from §10.1.
 */
export interface ListQuery extends PaginationQuery {
  /** Field name to sort by (server-side execution). */
  readonly sort?: string;
  /** Sort direction; defaults to ascending when omitted. */
  readonly order?: SortOrder;
}

/**
 * Pagination metadata carried in the uniform response envelope alongside
 * list payloads (FS-CRAWL-001 §10.1).
 */
export interface PaginationMeta {
  /** Zero-based offset of the returned window. */
  readonly offset: number;
  /** Requested window size. */
  readonly limit: number;
  /** Total matching rows across all windows (server-windowed lists). */
  readonly total: number;
}
