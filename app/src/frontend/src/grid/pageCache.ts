/**
 * Bounded LRU grid page cache (FS-CRAWL-002 §7.1 server-side windowing).
 *
 * Reuses the store's windowed-cache mechanism (canonical query keys, LRU
 * eviction) with the grid's tighter bounds: page size 200, ≤20 resident
 * pages (≈4,000 rows). The full dataset is NEVER held client-side — total
 * row count comes from pagination metadata, rows only from fetched windows.
 *
 * In-flight fetches are deduplicated per (source, query signature, page) so
 * rapid scrolling never storms the backend; aborted/stale signatures are
 * discarded on arrival.
 *
 * @module grid/pageCache
 */

import { canonicalQueryKey } from '../store/windowedCache';
import {
  GRID_MAX_PAGES,
  GRID_PAGE_SIZE,
  type GridDataSource,
  type GridFetchResult,
  type GridPage,
  type ServerListQuery
} from './types';
import { offsetForPage, pageForIndex } from './windowMath';

/** Signature partitioning cache entries by data source + non-pagination query. */
export function querySignature(sourceKey: string, query: ServerListQuery): string {
  const { offset: _offset, limit: _limit, ...rest } = query;
  return `${sourceKey}|${canonicalQueryKey(rest as Record<string, unknown>)}`;
}

interface CacheEntry<TRow> {
  readonly page: GridPage<TRow>;
  readonly signature: string;
}

/**
 * Per-explorer page cache. One instance lives with the grid engine; it is
 * discarded on unmount (no cross-surface retention).
 */
export class GridPageCache<TRow> {
  /** Insertion order = LRU recency order (oldest first). */
  private readonly entries = new Map<string, CacheEntry<TRow>>();
  private readonly inFlight = new Map<string, Promise<GridFetchResult<TRow>>>();
  /** Monotonic signature epoch: bump invalidates stale arrivals. */
  private epoch = 0;
  private currentSignature = '';

  constructor(
    private readonly source: GridDataSource<TRow>,
    private readonly pageSize: number = GRID_PAGE_SIZE,
    private readonly maxPages: number = GRID_MAX_PAGES
  ) {}

  /** Current resident page count (bounded by maxPages). */
  get size(): number {
    return this.entries.size;
  }

  /** Total rows reported by the most recent page of the active signature. */
  totalRows(): number {
    for (const entry of [...this.entries.values()].reverse()) {
      if (entry.signature === this.currentSignature) return entry.page.total;
    }
    return 0;
  }

  /**
   * Switches the active query signature (sort/filter/tree change). Resident
   * pages of other signatures stay until LRU-evicted — they remain valid
   * windows of their own query — but totalRows/fetches follow the new one.
   */
  setSignature(query: ServerListQuery): string {
    const signature = querySignature(this.source.sourceKey, query);
    if (signature !== this.currentSignature) {
      this.currentSignature = signature;
      this.epoch += 1;
    }
    return signature;
  }

  /** Reads a resident row by global index under the active signature. */
  getRow(globalIndex: number): TRow | undefined {
    const page = this.getPage(pageForIndex(globalIndex, this.pageSize));
    if (!page) return undefined;
    return page.rows[globalIndex - page.offset];
  }

  /** Reads a resident page (null when absent or signature-mismatched). */
  getPage(pageIndex: number): GridPage<TRow> | null {
    const key = this.entryKey(this.currentSignature, pageIndex);
    const entry = this.entries.get(key);
    if (!entry) return null;
    // LRU touch: re-insert at the recency tail.
    this.entries.delete(key);
    this.entries.set(key, entry);
    return entry.page;
  }

  /** True when the page is resident under the active signature. */
  hasPage(pageIndex: number): boolean {
    return this.entries.has(this.entryKey(this.currentSignature, pageIndex));
  }

  /**
   * Ensures a page is resident: returns immediately when cached, otherwise
   * fetches through the data source with in-flight dedup. Never throws —
   * transport/envelope failures resolve to error results.
   */
  async ensurePage(pageIndex: number, query: ServerListQuery, signal?: AbortSignal): Promise<GridFetchResult<TRow>> {
    const signature = this.setSignature(query);
    const cached = this.getPage(pageIndex);
    if (cached) return { ok: true, page: cached };

    const key = this.entryKey(signature, pageIndex);
    const pending = this.inFlight.get(key);
    if (pending) return pending;

    const epochAtStart = this.epoch;
    const offset = offsetForPage(pageIndex, this.pageSize);
    const promise = this.source
      .fetchPage({ ...query, offset, limit: this.pageSize }, signal)
      .then((result): GridFetchResult<TRow> => {
        this.inFlight.delete(key);
        // Discard arrivals for superseded queries (stale sort/filter).
        if (epochAtStart !== this.epoch || !result.ok) return result;
        this.insert(key, { page: result.page, signature });
        return result;
      })
      .catch((error: unknown): GridFetchResult<TRow> => {
        this.inFlight.delete(key);
        return {
          ok: false,
          errorCode: 'transport',
          errorMessage: error instanceof Error ? error.message : 'Transport failure'
        };
      });
    this.inFlight.set(key, promise);
    return promise;
  }

  /** Drops every resident page and in-flight dedup entry (unmount/reset). */
  clear(): void {
    this.entries.clear();
    this.inFlight.clear();
    this.epoch += 1;
    this.currentSignature = '';
  }

  private entryKey(signature: string, pageIndex: number): string {
    return `${signature}#${pageIndex}`;
  }

  private insert(key: string, entry: CacheEntry<TRow>): void {
    this.entries.delete(key);
    this.entries.set(key, entry);
    while (this.entries.size > Math.max(1, this.maxPages)) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) break;
      this.entries.delete(oldest);
    }
  }
}
