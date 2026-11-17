/**
 * Pure virtualization window math (FS-CRAWL-002 §7.1, criterion #40).
 *
 * Every function here is deterministic and side-effect free so the row-cap
 * invariant, overscan bounds, page mapping, and horizontal column window can
 * be unit-proven without a browser. Real-browser 60 FPS proof is phase-07
 * E2E; these proofs establish the mechanism that makes it possible:
 *
 *  - mounted DOM rows never exceed MAX_DOM_ROWS (50), any viewport/overscan;
 *  - scroll ↔ global-index mapping stays stable under measurement updates
 *    (page fetch decisions use the estimate, never measured heights);
 *  - horizontal windows mount only visible columns + overscan, pinned
 *    columns always mounted, pin count bounded.
 *
 * @module grid/windowMath
 */

import {
  DEFAULT_OVERSCAN,
  GRID_PAGE_SIZE,
  MAX_DOM_ROWS,
  MAX_MEASURED_ROWS,
  MAX_PINNED_COLUMNS
} from './types';

/** A vertical row window over the global row index space. */
export interface RowWindow {
  /** First global row index to mount (inclusive). */
  readonly startIndex: number;
  /** Last global row index to mount (exclusive). */
  readonly endIndex: number;
  /** Overscan rows actually applied above the viewport. */
  readonly overscanAbove: number;
  /** Overscan rows actually applied below the viewport. */
  readonly overscanBelow: number;
  /** Viewport row count before overscan. */
  readonly visibleCount: number;
}

/**
 * Clamps the requested overscan so viewport + 2·overscan can never exceed
 * the DOM row cap. Large viewports shrink overscan to zero rather than
 * mounting more than MAX_DOM_ROWS rows.
 */
export function clampOverscan(viewportHeight: number, rowHeight: number, requested: number = DEFAULT_OVERSCAN): number {
  const height = Math.max(1, rowHeight);
  const visible = Math.max(1, Math.ceil(Math.max(0, viewportHeight) / height));
  const headroom = Math.max(0, MAX_DOM_ROWS - visible);
  return Math.max(0, Math.min(Math.floor(requested), Math.floor(headroom / 2)));
}

/**
 * Computes the mounted row window for a scroll position. The result always
 * satisfies endIndex − startIndex ≤ MAX_DOM_ROWS and endIndex ≤ totalRows.
 * Indices are global (server offset space), never page-local.
 */
export function computeRowWindow(
  scrollTop: number,
  viewportHeight: number,
  rowHeight: number,
  totalRows: number,
  requestedOverscan: number = DEFAULT_OVERSCAN
): RowWindow {
  const total = Math.max(0, Math.floor(totalRows));
  if (total === 0 || viewportHeight <= 0 || rowHeight <= 0) {
    return { startIndex: 0, endIndex: 0, overscanAbove: 0, overscanBelow: 0, visibleCount: 0 };
  }
  const height = rowHeight;
  const overscan = clampOverscan(viewportHeight, height, requestedOverscan);
  const firstVisible = Math.max(0, Math.floor(Math.max(0, scrollTop) / height));
  const visibleCount = Math.max(1, Math.ceil(viewportHeight / height));
  const lastVisible = Math.min(total, firstVisible + visibleCount);

  let startIndex = Math.max(0, firstVisible - overscan);
  let endIndex = Math.min(total, lastVisible + overscan);

  // Hard invariant: never mount more than the cap, even for pathological
  // inputs (fractional heights, enormous viewports). Trim from the top so
  // the viewport bottom — where the eye is during downward scroll — stays.
  if (endIndex - startIndex > MAX_DOM_ROWS) {
    startIndex = endIndex - MAX_DOM_ROWS;
  }
  return {
    startIndex,
    endIndex,
    overscanAbove: firstVisible - startIndex,
    overscanBelow: endIndex - lastVisible,
    visibleCount
  };
}

/** Page index containing a global row index. */
export function pageForIndex(globalIndex: number, pageSize: number = GRID_PAGE_SIZE): number {
  return Math.floor(Math.max(0, globalIndex) / Math.max(1, pageSize));
}

/** Offset of a page's first row. */
export function offsetForPage(pageIndex: number, pageSize: number = GRID_PAGE_SIZE): number {
  return Math.max(0, pageIndex) * Math.max(1, pageSize);
}

/** The distinct page indices covering [startIndex, endIndex). */
export function pagesForRange(startIndex: number, endIndex: number, pageSize: number = GRID_PAGE_SIZE): number[] {
  if (endIndex <= startIndex) return [];
  const first = pageForIndex(startIndex, pageSize);
  const last = pageForIndex(endIndex - 1, pageSize);
  const pages: number[] = [];
  for (let page = first; page <= last; page += 1) pages.push(page);
  return pages;
}

/**
 * Stable scroll-offset → global-index mapping used for PAGE FETCH decisions.
 * Deliberately estimate-based: dynamic measurement refines rendering geometry
 * but never shifts which server pages are requested (§7.1 row geometry:
 * "scroll-position ↔ offset mapping stays stable under measurement updates").
 */
export function stableIndexForOffset(scrollTop: number, estimatedRowHeight: number): number {
  return Math.max(0, Math.floor(Math.max(0, scrollTop) / Math.max(1, estimatedRowHeight)));
}

/**
 * Bounded dynamic-measurement cache. Measured heights refine the virtualizer's
 * size estimates; the cache evicts oldest entries past MAX_MEASURED_ROWS so
 * measurement memory stays bounded at any dataset scale.
 */
export class MeasurementCache {
  private readonly heights = new Map<number, number>();

  measure(globalIndex: number, height: number): void {
    if (!Number.isFinite(height) || height <= 0) return;
    // Re-insertion refreshes recency (Map iteration order = insertion order).
    this.heights.delete(globalIndex);
    this.heights.set(globalIndex, height);
    while (this.heights.size > MAX_MEASURED_ROWS) {
      const oldest = this.heights.keys().next().value;
      if (oldest === undefined) break;
      this.heights.delete(oldest);
    }
  }

  get(globalIndex: number): number | undefined {
    return this.heights.get(globalIndex);
  }

  get size(): number {
    return this.heights.size;
  }

  clear(): void {
    this.heights.clear();
  }
}

/** One column's horizontal geometry. */
export interface ColumnGeometry {
  readonly id: string;
  readonly width: number;
  readonly pinned: 'left' | 'right' | null;
}

/** A horizontal column window: which columns mount at a scroll position. */
export interface ColumnWindow {
  /** Unpooled visible column ids (left→right), including overscan. */
  readonly visibleIds: readonly string[];
  /** Pinned-left ids (always mounted, in pin order). */
  readonly pinnedLeftIds: readonly string[];
  /** Pinned-right ids (always mounted, in pin order). */
  readonly pinnedRightIds: readonly string[];
  /** Total width of the unpooled column track. */
  readonly trackWidth: number;
  /** Scroll offset of the first visible unpooled column. */
  readonly trackOffset: number;
}

/**
 * Horizontal virtualization for wide dynamic-column datasets (§7.1): only
 * visible columns plus a small overscan mount; pinned columns always mount.
 * Pin count is bounded by MAX_PINNED_COLUMNS — excess pins are demoted to
 * unpooled (first-come keeps the pin).
 */
export function computeColumnWindow(
  columns: readonly ColumnGeometry[],
  scrollLeft: number,
  viewportWidth: number,
  overscanColumns = 2
): ColumnWindow {
  const pinnedLeft: ColumnGeometry[] = [];
  const pinnedRight: ColumnGeometry[] = [];
  const unpooled: ColumnGeometry[] = [];
  let pinBudget = MAX_PINNED_COLUMNS;
  for (const column of columns) {
    if (column.pinned && pinBudget > 0) {
      pinBudget -= 1;
      (column.pinned === 'left' ? pinnedLeft : pinnedRight).push(column);
    } else {
      unpooled.push(column);
    }
  }

  const trackWidth = unpooled.reduce((sum, column) => sum + column.width, 0);
  const pinnedLeftWidth = pinnedLeft.reduce((sum, column) => sum + column.width, 0);
  const pinnedRightWidth = pinnedRight.reduce((sum, column) => sum + column.width, 0);
  const usableWidth = Math.max(0, viewportWidth - pinnedLeftWidth - pinnedRightWidth);

  // The unpooled track scrolls under the pinned gutters.
  const effectiveScroll = Math.max(0, scrollLeft);
  let cursor = 0;
  let firstVisible = unpooled.length;
  let lastVisible = -1;
  for (let i = 0; i < unpooled.length; i += 1) {
    const next = cursor + unpooled[i].width;
    if (next > effectiveScroll && firstVisible === unpooled.length) firstVisible = i;
    if (cursor < effectiveScroll + usableWidth) lastVisible = i;
    cursor = next;
  }
  const start = Math.max(0, firstVisible - overscanColumns);
  const end = Math.min(unpooled.length - 1, lastVisible + overscanColumns);
  const visibleIds: string[] = [];
  let trackOffset = 0;
  for (let i = 0; i < unpooled.length; i += 1) {
    if (i >= start && i <= end) visibleIds.push(unpooled[i].id);
    if (i < start) trackOffset += unpooled[i].width;
  }

  return {
    visibleIds,
    pinnedLeftIds: pinnedLeft.map((column) => column.id),
    pinnedRightIds: pinnedRight.map((column) => column.id),
    trackWidth,
    trackOffset
  };
}

/**
 * Enforces the pin budget on a pin-state map: at most MAX_PINNED_COLUMNS
 * entries survive (insertion order = pin priority). Returns a new map.
 */
export function clampPinning(
  pinned: Readonly<Record<string, 'left' | 'right'>>
): Record<string, 'left' | 'right'> {
  const entries = Object.entries(pinned).slice(0, MAX_PINNED_COLUMNS);
  return Object.fromEntries(entries);
}
