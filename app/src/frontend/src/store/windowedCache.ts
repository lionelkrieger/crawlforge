/**
 * Bounded LRU windowed-page cache (contracts/store/state WindowedSlice).
 * FS-CRAWL-002 §7.4: window caches are bounded; no slice ever holds a full
 * dataset. Pages are keyed by the canonical serialization of the list query.
 *
 * @module store/windowedCache
 */

import type { WindowedPage, WindowedSlice } from '../contracts/store/state';
import type { ListQuery } from '../contracts/common/pagination';

/** Maximum cached pages per windowed slice (bounded cache, §7.4). */
export const MAX_WINDOWED_PAGES = 24;

/** The empty windowed slice. */
export function emptyWindowedSlice<T>(): WindowedSlice<T> {
  return { pages: {}, activeKey: null };
}

/**
 * Canonical query key: stable JSON of the sorted query fields. Identical
 * queries (any field order) map to the same cache key.
 */
export function canonicalQueryKey(query: ListQuery | undefined): string {
  if (!query) return '{}';
  const entries = Object.entries(query as Record<string, unknown>)
    .filter(([, value]) => value !== undefined)
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  return JSON.stringify(Object.fromEntries(entries));
}

/**
 * Inserts a page under the LRU bound and marks it active. Evicts the
 * least-recently-used pages when the cache exceeds `maxPages`.
 */
export function putPage<T>(
  slice: WindowedSlice<T>,
  page: WindowedPage<T>,
  maxPages: number = MAX_WINDOWED_PAGES
): WindowedSlice<T> {
  const bound = Math.max(1, maxPages);
  const pages: Record<string, WindowedPage<T>> = {};
  // Re-insertion order = recency order; the new page goes last (most recent).
  for (const [key, existing] of Object.entries(slice.pages)) {
    if (key !== page.key) pages[key] = existing;
  }
  pages[page.key] = page;
  const keys = Object.keys(pages);
  while (keys.length > bound) {
    const evicted = keys.shift();
    if (evicted !== undefined) delete pages[evicted];
  }
  return { pages, activeKey: page.key };
}

/** Marks a cached page active (LRU touch); no-op when absent. */
export function touchPage<T>(slice: WindowedSlice<T>, key: string): WindowedSlice<T> {
  const page = slice.pages[key];
  if (!page || slice.activeKey === key) return slice;
  const pages: Record<string, WindowedPage<T>> = {};
  for (const [existingKey, existing] of Object.entries(slice.pages)) {
    if (existingKey !== key) pages[existingKey] = existing;
  }
  pages[key] = page;
  return { pages, activeKey: key };
}

/** Reads one cached page (null when absent). */
export function getPage<T>(slice: WindowedSlice<T>, key: string): WindowedPage<T> | null {
  return slice.pages[key] ?? null;
}

/** Builds a windowed page from a server list response. */
export function pageFromResponse<T>(
  key: string,
  items: readonly T[],
  pagination: { readonly offset: number; readonly limit: number; readonly total: number } | undefined,
  fetchedAt: string,
  fallbackLimit: number
): WindowedPage<T> {
  const limit = pagination?.limit ?? fallbackLimit;
  return {
    key,
    offset: pagination?.offset ?? 0,
    limit,
    total: pagination?.total ?? items.length,
    items: items.slice(0, limit),
    fetchedAt
  };
}
