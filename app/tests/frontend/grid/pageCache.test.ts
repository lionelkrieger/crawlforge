/**
 * Page cache proofs: LRU bound ≤20 pages, in-flight dedup, stale-epoch
 * discard, signature partitioning, and bounded resident memory at 500k rows.
 */

import { describe, expect, it } from 'vitest';
import { GridPageCache, querySignature } from '../../../src/frontend/src/grid/pageCache';
import { GRID_MAX_PAGES, GRID_PAGE_SIZE } from '../../../src/frontend/src/grid/types';
import { recordingSource, seedUrls } from './helpers';

describe('querySignature', () => {
  it('ignores pagination but captures sort/filter state', () => {
    const a = querySignature('src', { offset: 0, limit: 200, sort: 'depth', order: 'asc' });
    const b = querySignature('src', { offset: 400, limit: 200, sort: 'depth', order: 'asc' });
    const c = querySignature('src', { offset: 0, limit: 200, sort: 'depth', order: 'desc' });
    expect(a).toBe(b);
    expect(a).not.toBe(c);
  });

  it('is stable under filter key order', () => {
    const a = querySignature('src', { statusClass: '2xx', indexable: true });
    const b = querySignature('src', { indexable: true, statusClass: '2xx' });
    expect(a).toBe(b);
  });

  it('partitions by source key', () => {
    expect(querySignature('a', {})).not.toBe(querySignature('b', {}));
  });
});

describe('GridPageCache', () => {
  it('fetches a window once and serves repeats from cache', async () => {
    const source = recordingSource(seedUrls(1_000));
    const cache = new GridPageCache(source);
    const first = await cache.ensurePage(0, {});
    const second = await cache.ensurePage(0, {});
    expect(first.ok).toBe(true);
    expect(second.ok).toBe(true);
    expect(source.fetchCount()).toBe(1);
    expect(cache.size).toBe(1);
  });

  it('reads rows by global index across page boundaries', async () => {
    const rows = seedUrls(1_000);
    const cache = new GridPageCache(recordingSource(rows));
    await cache.ensurePage(0, {});
    await cache.ensurePage(1, {});
    await cache.ensurePage(4, {});
    expect(cache.getRow(0)?.id).toBe('url-0');
    expect(cache.getRow(GRID_PAGE_SIZE - 1)?.id).toBe(`url-${GRID_PAGE_SIZE - 1}`);
    expect(cache.getRow(GRID_PAGE_SIZE)?.id).toBe(`url-${GRID_PAGE_SIZE}`);
    expect(cache.getRow(999)?.id).toBe('url-999');
    expect(cache.getRow(5_000)).toBeUndefined();
  });

  it('evicts least-recently-used pages past the 20-page bound', async () => {
    const source = recordingSource(seedUrls(HALF_MILLION_ROWS));
    const cache = new GridPageCache(source);
    for (let page = 0; page < GRID_MAX_PAGES + 10; page += 1) {
      await cache.ensurePage(page, {});
    }
    expect(cache.size).toBe(GRID_MAX_PAGES);
    expect(cache.hasPage(0)).toBe(false);
    expect(cache.hasPage(GRID_MAX_PAGES + 9)).toBe(true);
    // Resident rows ≈ 20 × 200 = 4,000 — never the 500k dataset.
    expect(cache.size * GRID_PAGE_SIZE).toBeLessThanOrEqual(4_000);
  });

  it('LRU touch protects recently read pages from eviction', async () => {
    const source = recordingSource(seedUrls(HALF_MILLION_ROWS));
    const cache = new GridPageCache(source);
    await cache.ensurePage(0, {});
    for (let page = 1; page < GRID_MAX_PAGES; page += 1) await cache.ensurePage(page, {});
    cache.getRow(0); // touch page 0 → newest
    await cache.ensurePage(GRID_MAX_PAGES, {}); // evicts page 1
    expect(cache.hasPage(0)).toBe(true);
    expect(cache.hasPage(1)).toBe(false);
  });

  it('deduplicates concurrent fetches of the same page', async () => {
    const source = recordingSource(seedUrls(1_000), { delayMs: 15 });
    const cache = new GridPageCache(source);
    const [a, b, c] = await Promise.all([
      cache.ensurePage(2, {}),
      cache.ensurePage(2, {}),
      cache.ensurePage(2, {})
    ]);
    expect(source.fetchCount()).toBe(1);
    expect(a.ok && b.ok && c.ok).toBe(true);
  });

  it('discards stale arrivals after a signature change', async () => {
    const source = recordingSource(seedUrls(1_000), { delayMs: 20 });
    const cache = new GridPageCache(source);
    const stale = cache.ensurePage(0, { sort: 'depth', order: 'asc' });
    // User re-sorts mid-flight: epoch bumps, the old arrival must not land.
    cache.setSignature({ sort: 'depth', order: 'desc' });
    const result = await stale;
    expect(result.ok).toBe(true); // caller still gets its data
    expect(cache.hasPage(0)).toBe(false); // but nothing resident under the new signature
    const fresh = await cache.ensurePage(0, { sort: 'depth', order: 'desc' });
    expect(fresh.ok).toBe(true);
    expect(cache.hasPage(0)).toBe(true);
    expect(source.fetchCount()).toBe(2);
  });

  it('reports totals from the active signature only', async () => {
    const source = recordingSource(seedUrls(1_234));
    const cache = new GridPageCache(source);
    await cache.ensurePage(0, {});
    expect(cache.totalRows()).toBe(1_234);
    cache.setSignature({ search: 'nothing-matches' });
    expect(cache.totalRows()).toBe(0);
  });

  it('resolves envelope/transport failures as values, never throws', async () => {
    const source = recordingSource(seedUrls(100), { failNext: 1, failCode: 'internal' });
    const cache = new GridPageCache(source);
    const failed = await cache.ensurePage(0, {});
    expect(failed.ok).toBe(false);
    if (!failed.ok) expect(failed.errorCode).toBe('internal');
    expect(cache.size).toBe(0);
    const recovered = await cache.ensurePage(0, {});
    expect(recovered.ok).toBe(true);
  });

  it('catches source rejections as transport errors', async () => {
    const cache = new GridPageCache({
      sourceKey: 'throwing',
      fetchPage: async () => { throw new Error('socket hang up'); }
    });
    const result = await cache.ensurePage(0, {});
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errorCode).toBe('transport');
      expect(result.errorMessage).toContain('socket hang up');
    }
  });

  it('clear drops all resident pages and bumps the epoch', async () => {
    const source = recordingSource(seedUrls(1_000), { delayMs: 10 });
    const cache = new GridPageCache(source);
    await cache.ensurePage(0, {});
    const pending = cache.ensurePage(1, {});
    cache.clear();
    await pending;
    expect(cache.size).toBe(0);
    expect(cache.totalRows()).toBe(0);
  });
});

const HALF_MILLION_ROWS = 500_000;
