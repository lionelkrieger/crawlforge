/**
 * Window math proofs (criterion #40 mechanism): the row-cap invariant at
 * 500k+ rows, overscan bounds, stable page mapping, bounded measurement
 * cache, and horizontal column windows with pin budgets.
 */

import { describe, expect, it } from 'vitest';
import {
  clampOverscan,
  computeRowWindow,
  pageForIndex,
  offsetForPage,
  pagesForRange,
  stableIndexForOffset,
  MeasurementCache,
  computeColumnWindow,
  clampPinning,
  type ColumnGeometry
} from '../../../src/frontend/src/grid/windowMath';
import { MAX_DOM_ROWS, MAX_MEASURED_ROWS, MAX_PINNED_COLUMNS, GRID_PAGE_SIZE } from '../../../src/frontend/src/grid/types';

const HALF_MILLION = 500_000;

describe('clampOverscan', () => {
  it('keeps the requested overscan when the viewport leaves headroom', () => {
    // 600px / 36px ≈ 17 visible rows → headroom 33 → overscan 6 fits.
    expect(clampOverscan(600, 36, 6)).toBe(6);
  });

  it('shrinks overscan so viewport + 2·overscan never exceeds the cap', () => {
    // 1600px / 36px ≈ 45 visible → headroom 5 → overscan ≤ 2.
    const overscan = clampOverscan(1600, 36, 6);
    const visible = Math.ceil(1600 / 36);
    expect(visible + 2 * overscan).toBeLessThanOrEqual(MAX_DOM_ROWS);
  });

  it('returns zero overscan for viewports at or beyond the cap', () => {
    expect(clampOverscan(36 * MAX_DOM_ROWS, 36, 6)).toBe(0);
    expect(clampOverscan(36 * (MAX_DOM_ROWS + 20), 36, 6)).toBe(0);
  });

  it('tolerates pathological inputs', () => {
    expect(clampOverscan(0, 36, 6)).toBeGreaterThanOrEqual(0);
    expect(clampOverscan(600, 0, 6)).toBeGreaterThanOrEqual(0);
    expect(clampOverscan(-100, -5, -3)).toBe(0);
  });
});

describe('computeRowWindow — row-cap invariant @500k rows', () => {
  it('never mounts more than MAX_DOM_ROWS across the whole scroll range', () => {
    const rowHeight = 36;
    const viewport = 600;
    // Sweep the entire 500k-row scroll space in 997 steps (prime stride).
    for (let scrollTop = 0; scrollTop < HALF_MILLION * rowHeight; scrollTop += 997 * rowHeight) {
      const window = computeRowWindow(scrollTop, viewport, rowHeight, HALF_MILLION, 6);
      expect(window.endIndex - window.startIndex).toBeLessThanOrEqual(MAX_DOM_ROWS);
      expect(window.startIndex).toBeGreaterThanOrEqual(0);
      expect(window.endIndex).toBeLessThanOrEqual(HALF_MILLION);
    }
  });

  it('holds the cap for enormous viewports and tiny rows', () => {
    const window = computeRowWindow(10_000, 100_000, 1, HALF_MILLION, 50);
    expect(window.endIndex - window.startIndex).toBeLessThanOrEqual(MAX_DOM_ROWS);
  });

  it('holds the cap with fractional row heights', () => {
    for (const height of [27.5, 33.33, 44.9]) {
      const window = computeRowWindow(123_456.7, 700.5, height, HALF_MILLION, 8);
      expect(window.endIndex - window.startIndex).toBeLessThanOrEqual(MAX_DOM_ROWS);
    }
  });

  it('returns an empty window for zero rows or degenerate geometry', () => {
    expect(computeRowWindow(0, 600, 36, 0)).toMatchObject({ startIndex: 0, endIndex: 0 });
    expect(computeRowWindow(100, 0, 36, 1000)).toMatchObject({ startIndex: 0, endIndex: 0 });
    expect(computeRowWindow(100, 600, 0, 1000)).toMatchObject({ startIndex: 0, endIndex: 0 });
  });

  it('applies symmetric overscan in the middle of the dataset', () => {
    const window = computeRowWindow(10_000 * 36, 600, 36, HALF_MILLION, 6);
    expect(window.overscanAbove).toBe(6);
    expect(window.overscanBelow).toBe(6);
    expect(window.startIndex).toBe(10_000 - 6);
  });

  it('clamps overscan at the dataset edges', () => {
    const top = computeRowWindow(0, 600, 36, HALF_MILLION, 6);
    expect(top.startIndex).toBe(0);
    expect(top.overscanAbove).toBe(0);
    const bottom = computeRowWindow((HALF_MILLION - 1) * 36, 600, 36, HALF_MILLION, 6);
    expect(bottom.endIndex).toBe(HALF_MILLION);
  });

  it('trims from the top so the viewport bottom stays mounted', () => {
    // Viewport taller than the cap: the window keeps the LAST cap rows.
    const window = computeRowWindow(5_000 * 36, 36 * 80, 36, HALF_MILLION, 0);
    expect(window.endIndex - window.startIndex).toBe(MAX_DOM_ROWS);
    const firstVisible = 5_000;
    const visibleCount = 80;
    expect(window.endIndex).toBe(Math.min(HALF_MILLION, firstVisible + visibleCount));
  });
});

describe('page mapping', () => {
  it('maps global indices to pages and back', () => {
    expect(pageForIndex(0)).toBe(0);
    expect(pageForIndex(GRID_PAGE_SIZE - 1)).toBe(0);
    expect(pageForIndex(GRID_PAGE_SIZE)).toBe(1);
    expect(pageForIndex(HALF_MILLION - 1)).toBe(Math.floor((HALF_MILLION - 1) / GRID_PAGE_SIZE));
    expect(offsetForPage(3)).toBe(3 * GRID_PAGE_SIZE);
  });

  it('covers a window with the distinct pages it touches', () => {
    expect(pagesForRange(190, 210)).toEqual([0, 1]);
    expect(pagesForRange(0, 200)).toEqual([0]);
    expect(pagesForRange(200, 200)).toEqual([]);
    expect(pagesForRange(400, 650)).toEqual([2, 3]);
  });

  it('keeps the scroll→index mapping estimate-stable under measurement', () => {
    // Measurement updates never enter this mapping: same offset, same index.
    const estimate = 36;
    expect(stableIndexForOffset(36_000, estimate)).toBe(1_000);
    expect(stableIndexForOffset(36_000 + 35, estimate)).toBe(1_000);
    expect(stableIndexForOffset(-50, estimate)).toBe(0);
  });
});

describe('MeasurementCache', () => {
  it('stores and refreshes measured heights', () => {
    const cache = new MeasurementCache();
    cache.measure(5, 40);
    expect(cache.get(5)).toBe(40);
    cache.measure(5, 44);
    expect(cache.get(5)).toBe(44);
    expect(cache.size).toBe(1);
  });

  it('ignores invalid measurements', () => {
    const cache = new MeasurementCache();
    cache.measure(1, 0);
    cache.measure(2, -4);
    cache.measure(3, Number.NaN);
    expect(cache.size).toBe(0);
  });

  it('evicts oldest entries past the bound at any scale', () => {
    const cache = new MeasurementCache();
    for (let i = 0; i < MAX_MEASURED_ROWS + 500; i += 1) cache.measure(i, 30 + (i % 5));
    expect(cache.size).toBe(MAX_MEASURED_ROWS);
    expect(cache.get(0)).toBeUndefined();
    expect(cache.get(MAX_MEASURED_ROWS + 499)).toBeDefined();
  });

  it('refreshes recency on re-measure (LRU, not FIFO)', () => {
    const cache = new MeasurementCache();
    for (let i = 0; i < MAX_MEASURED_ROWS; i += 1) cache.measure(i, 30);
    cache.measure(0, 31); // touch oldest → becomes newest
    cache.measure(MAX_MEASURED_ROWS, 30); // evicts index 1, not 0
    expect(cache.get(0)).toBe(31);
    expect(cache.get(1)).toBeUndefined();
  });
});

describe('computeColumnWindow — horizontal virtualization', () => {
  const columns: ColumnGeometry[] = Array.from({ length: 40 }, (_, i) => ({
    id: `col-${i}`,
    width: 200,
    pinned: null
  }));

  it('mounts only visible columns plus overscan on a wide dataset', () => {
    const window = computeColumnWindow(columns, 2_000, 800, 2);
    // Viewport shows cols 10–13; overscan 2 → cols 8–15.
    expect(window.visibleIds.length).toBeLessThan(columns.length);
    expect(window.visibleIds).toContain('col-10');
    expect(window.visibleIds).toContain('col-13');
    expect(window.visibleIds).not.toContain('col-0');
    expect(window.visibleIds).not.toContain('col-39');
    expect(window.trackWidth).toBe(40 * 200);
    expect(window.trackOffset).toBe(8 * 200);
  });

  it('always mounts pinned columns and subtracts their gutters', () => {
    const withPins: ColumnGeometry[] = [
      { id: 'pin-l', width: 150, pinned: 'left' },
      ...columns,
      { id: 'pin-r', width: 120, pinned: 'right' }
    ];
    const window = computeColumnWindow(withPins, 0, 800, 1);
    expect(window.pinnedLeftIds).toEqual(['pin-l']);
    expect(window.pinnedRightIds).toEqual(['pin-r']);
    expect(window.trackWidth).toBe(40 * 200);
    expect(window.visibleIds.length).toBeLessThanOrEqual(6);
  });

  it('demotes pins beyond the budget to the unpooled track', () => {
    const manyPins: ColumnGeometry[] = Array.from({ length: MAX_PINNED_COLUMNS + 3 }, (_, i) => ({
      id: `pin-${i}`,
      width: 100,
      pinned: i % 2 === 0 ? 'left' : 'right'
    }));
    const window = computeColumnWindow(manyPins, 0, 1_000, 1);
    expect(window.pinnedLeftIds.length + window.pinnedRightIds.length).toBe(MAX_PINNED_COLUMNS);
  });

  it('mounts everything when the track fits the viewport', () => {
    const few = columns.slice(0, 3);
    const window = computeColumnWindow(few, 0, 1_000, 2);
    expect(window.visibleIds).toEqual(['col-0', 'col-1', 'col-2']);
    expect(window.trackOffset).toBe(0);
  });
});

describe('clampPinning', () => {
  it('keeps at most MAX_PINNED_COLUMNS pins in insertion order', () => {
    const pinned = { a: 'left', b: 'left', c: 'right', d: 'right', e: 'left', f: 'right' } as const;
    const clamped = clampPinning(pinned);
    expect(Object.keys(clamped)).toEqual(['a', 'b', 'c', 'd']);
  });

  it('passes small pin maps through unchanged', () => {
    expect(clampPinning({ a: 'left' })).toEqual({ a: 'left' });
    expect(clampPinning({})).toEqual({});
  });
});
