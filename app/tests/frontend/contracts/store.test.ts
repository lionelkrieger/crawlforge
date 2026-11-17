/**
 * Normalized store contract tests (FS-CRAWL-002 §8.8, §7.4): entity tables,
 * windowed pages, bounded ring buffers, slice shape locks, and a test-local
 * store implementation proving the StoreApi contract is implementable.
 */

import { describe, expect, it } from 'vitest';
import {
  DEFAULT_RECENT_ACTIVITY_CAPACITY,
  isEntityTable,
  isRecentActivityBuffer,
  isString,
  isWindowedPage
} from '../../../src/frontend/src/contracts';
import type {
  CrawlForgeState,
  CrawlForgeStore,
  EntityTable,
  HubActivityEntry,
  RecentActivityBuffer,
  SystemHealthSnapshot,
  UrlRecordSummary,
  WindowedPage,
  WindowedSlice
} from '../../../src/frontend/src/contracts';
import { Equal, Expect, emptyCrawlForgeState, healthSnapshot } from './helpers';

/** Minimal test-local store proving the structural StoreApi contract. */
function createTestStore(initial: CrawlForgeState): CrawlForgeStore {
  let state = initial;
  const listeners = new Set<(next: CrawlForgeState, prev: CrawlForgeState) => void>();
  return {
    getState: () => state,
    getInitialState: () => initial,
    setState(partial, replace) {
      const prev = state;
      const next = typeof partial === 'function' ? partial(state) : partial;
      // replace=true requires a full state object (Zustand-class semantics).
      state = replace === true ? (next as CrawlForgeState) : { ...state, ...next };
      for (const listener of listeners) listener(state, prev);
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    }
  };
}

const urlRow: UrlRecordSummary = {
  id: 'url-1',
  canonicalUrl: 'https://target.test/',
  statusCode: 200,
  statusClass: '2xx',
  contentType: 'text/html',
  depth: 0,
  indexable: true,
  title: 'Target'
};

const urlPage: WindowedPage<UrlRecordSummary> = {
  key: 'offset=0&limit=50',
  offset: 0,
  limit: 50,
  total: 500_000,
  items: [urlRow],
  fetchedAt: '2026-11-14T06:28:43Z'
};

describe('normalized slice shape (§8.8)', () => {
  it('locks the exact slice set', () => {
    type Checks = [
      Expect<
        Equal<
          keyof CrawlForgeState,
          | 'settings'
          | 'projects'
          | 'audits'
          | 'urlPages'
          | 'findingPages'
          | 'linkPages'
          | 'ngramPages'
          | 'hostThrottle'
          | 'systemHealth'
          | 'plugins'
          | 'hub'
        >
      >,
      Expect<Equal<CrawlForgeState['urlPages'], WindowedSlice<UrlRecordSummary>>>,
      Expect<Equal<CrawlForgeState['systemHealth']['latest'], SystemHealthSnapshot | null>>,
      Expect<Equal<CrawlForgeState['hub']['recentActivity'], RecentActivityBuffer>>
    ];
    const checks: Checks = [true, true, true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('starts empty: no slice holds a dataset', () => {
    const state = emptyCrawlForgeState;
    expect(state.projects.entities.allIds).toHaveLength(0);
    expect(state.audits.entities.allIds).toHaveLength(0);
    expect(Object.keys(state.urlPages.pages)).toHaveLength(0);
    expect(state.hub.recentActivity.entries).toHaveLength(0);
    expect(state.systemHealth.frontendJsHeapBytes).toBeNull();
  });

  it('forbids mutating state in place (immutability contract)', () => {
    // Compile-time negatives only: the function is never invoked, so the
    // shared fixture is never actually mutated.
    const negativeMutations = (): void => {
      const state = emptyCrawlForgeState;
      // @ts-expect-error — slices are deeply readonly; updates must replace objects.
      state.audits.activeAuditId = 'audit-1';
      // @ts-expect-error — windowed pages are readonly too.
      state.urlPages.activeKey = 'k';
    };
    expect(typeof negativeMutations).toBe('function');
    expect(emptyCrawlForgeState.audits.activeAuditId).toBeNull();
  });
});

describe('entity table guard', () => {
  const table: EntityTable<string> = { byId: { a: 'alpha', b: 'beta' }, allIds: ['a', 'b'] };

  it('accepts a consistent table', () => {
    expect(isEntityTable(table, isString)).toBe(true);
    expect(isEntityTable({ byId: {}, allIds: [] }, isString)).toBe(true);
  });

  it('rejects byId/allIds inconsistency', () => {
    expect(isEntityTable({ byId: { a: 'alpha' }, allIds: ['a', 'b'] }, isString)).toBe(false);
    expect(isEntityTable({ byId: { a: 'alpha', b: 'beta' }, allIds: ['a'] }, isString)).toBe(false);
  });

  it('rejects invalid items and shapes', () => {
    expect(isEntityTable({ byId: { a: 42 }, allIds: ['a'] }, isString)).toBe(false);
    expect(isEntityTable({ byId: {}, allIds: 'a' }, isString)).toBe(false);
    expect(isEntityTable(null, isString)).toBe(false);
  });
});

describe('windowed page guard (no full datasets, §7.4)', () => {
  it('accepts a bounded server window over a large total', () => {
    expect(isWindowedPage(urlPage, (item): item is UrlRecordSummary => {
      return typeof item === 'object' && item !== null && 'id' in item;
    })).toBe(true);
    expect(urlPage.total).toBeGreaterThan(urlPage.items.length);
  });

  it('rejects windows holding more items than the limit', () => {
    const overfull = { ...urlPage, items: [urlRow, urlRow], limit: 1 };
    expect(isWindowedPage(overfull, (item): item is UrlRecordSummary => {
      return typeof item === 'object' && item !== null && 'id' in item;
    })).toBe(false);
  });

  it('rejects invalid offsets/limits', () => {
    const isUrl = (item: unknown): item is UrlRecordSummary =>
      typeof item === 'object' && item !== null && 'id' in item;
    expect(isWindowedPage({ ...urlPage, offset: -1 }, isUrl)).toBe(false);
    expect(isWindowedPage({ ...urlPage, limit: 0 }, isUrl)).toBe(false);
    expect(isWindowedPage({ ...urlPage, total: -1 }, isUrl)).toBe(false);
    expect(isWindowedPage({ ...urlPage, fetchedAt: 123 }, isUrl)).toBe(false);
  });
});

describe('bounded ring buffer guard (recent activity <= 1,000, §7.4)', () => {
  const entry: HubActivityEntry = {
    event: 'crawl-progress',
    receivedAt: '2026-11-14T06:28:43Z',
    auditId: 'audit-1'
  };

  it('defaults to a 1,000-entry capacity', () => {
    expect(DEFAULT_RECENT_ACTIVITY_CAPACITY).toBe(1000);
  });

  it('accepts a buffer within capacity', () => {
    const buffer: RecentActivityBuffer = { entries: [entry], capacity: 2, droppedCount: 0 };
    expect(isRecentActivityBuffer(buffer)).toBe(true);
  });

  it('rejects overflow beyond capacity', () => {
    const buffer = { entries: [entry, entry, entry], capacity: 2, droppedCount: 1 };
    expect(isRecentActivityBuffer(buffer)).toBe(false);
  });

  it('rejects unknown event names and invalid capacities', () => {
    expect(
      isRecentActivityBuffer({
        entries: [{ ...entry, event: 'not-an-event' }],
        capacity: 2,
        droppedCount: 0
      })
    ).toBe(false);
    expect(isRecentActivityBuffer({ entries: [], capacity: 0, droppedCount: 0 })).toBe(false);
    expect(isRecentActivityBuffer({ entries: [], capacity: 2, droppedCount: -1 })).toBe(false);
  });
});

describe('StoreApi contract (Zustand-class, structural)', () => {
  it('updates slices immutably and notifies subscribers', () => {
    const store = createTestStore(emptyCrawlForgeState);
    const seen: CrawlForgeState[] = [];
    const unsubscribe = store.subscribe((next) => seen.push(next));

    store.setState({
      systemHealth: {
        latest: healthSnapshot,
        frontendJsHeapBytes: 64 * 1024 * 1024,
        lastHeartbeatAt: healthSnapshot.timestamp
      }
    });

    const next = store.getState();
    expect(seen).toHaveLength(1);
    expect(next).not.toBe(emptyCrawlForgeState);
    // Untouched slices keep their reference (structural sharing).
    expect(next.projects).toBe(emptyCrawlForgeState.projects);
    expect(next.systemHealth.latest).toEqual(healthSnapshot);
    expect(store.getInitialState()).toBe(emptyCrawlForgeState);

    unsubscribe();
    store.setState({ settings: { settings: null, loaded: true } });
    expect(seen).toHaveLength(1);
  });

  it('supports selector projections', () => {
    const store = createTestStore(emptyCrawlForgeState);
    const selectActiveAuditId = (state: CrawlForgeState): string | null => state.audits.activeAuditId;
    expect(selectActiveAuditId(store.getState())).toBeNull();
    store.setState({ audits: { ...emptyCrawlForgeState.audits, activeAuditId: 'audit-7' } });
    expect(selectActiveAuditId(store.getState())).toBe('audit-7');
  });
});
