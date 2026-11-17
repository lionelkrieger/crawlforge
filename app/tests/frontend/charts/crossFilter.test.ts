/**
 * Cross-filter suite (node env): pure selection → locked ListQuery mappings,
 * dispatcher behavior against spy actions, and end-to-end proof that chart
 * selections drive the ACCEPTED store actions and reach the wire with the
 * correct server-side filters (WP-06-04, FS-CRAWL-001 §8.3).
 */

import { describe, expect, it } from 'vitest';
import {
  applyChartCrossFilter,
  selectionTargets,
  selectionToFindingQuery,
  selectionToLinkQuery,
  selectionToNgramQuery,
  selectionToUrlQuery,
  selectionToWafQuery,
  type ChartSelection
} from '../../../src/frontend/src/charts/crossFilter';
import type { CrawlForgeStoreActions } from '../../../src/frontend/src/store/crawlForgeStore';
import { createCrawlForgeStore, createStoreActions } from '../../../src/frontend/src/store/crawlForgeStore';
import { fakeApiClient, ok } from './helpers';

describe('selection → query mappings', () => {
  it('maps status selections to URL-explorer statusClass filters', () => {
    expect(selectionToUrlQuery({ kind: 'status', statusClass: '4xx' })).toEqual({ statusClass: '4xx' });
    expect(selectionToFindingQuery({ kind: 'status', statusClass: '4xx' })).toBeNull();
    expect(selectionToLinkQuery({ kind: 'status', statusClass: '4xx' })).toBeNull();
  });

  it('maps severity and category selections to issue-explorer filters', () => {
    expect(selectionToFindingQuery({ kind: 'severity', severity: 'error' })).toEqual({ severity: 'error' });
    expect(selectionToFindingQuery({ kind: 'category', category: 'content' })).toEqual({ category: 'content' });
    expect(selectionToUrlQuery({ kind: 'severity', severity: 'error' })).toBeNull();
  });

  it('maps WAF provider selections to URL + WAF block filters', () => {
    const selection: ChartSelection = { kind: 'waf-provider', provider: 'cloudflare' };
    expect(selectionToUrlQuery(selection)).toEqual({ wafProvider: 'cloudflare' });
    expect(selectionToWafQuery(selection)).toEqual({ provider: 'cloudflare' });
    expect(selectionToFindingQuery(selection)).toBeNull();
  });

  it('maps zone selections to link domZone + n-gram zone filters', () => {
    const selection: ChartSelection = { kind: 'ngram-zone', zone: 'title' };
    expect(selectionToLinkQuery(selection)).toEqual({ domZone: 'title' });
    expect(selectionToNgramQuery(selection)).toEqual({ zone: 'title' });
    expect(selectionToUrlQuery(selection)).toBeNull();
  });

  it('host selections reach no explorer query (host-scoped views only)', () => {
    const selection: ChartSelection = { kind: 'host', host: 'example.com' };
    expect(selectionToUrlQuery(selection)).toBeNull();
    expect(selectionToFindingQuery(selection)).toBeNull();
    expect(selectionToLinkQuery(selection)).toBeNull();
    expect(selectionToNgramQuery(selection)).toBeNull();
    expect(selectionToWafQuery(selection)).toBeNull();
    expect(selectionTargets(selection)).toEqual([]);
  });

  it('reports the explorer targets each selection reaches', () => {
    expect(selectionTargets({ kind: 'status', statusClass: '5xx' })).toEqual(['urls']);
    expect(selectionTargets({ kind: 'severity', severity: 'warning' })).toEqual(['findings']);
    expect(selectionTargets({ kind: 'category', category: 'crawl' })).toEqual(['findings']);
    expect(selectionTargets({ kind: 'waf-provider', provider: 'akamai' })).toEqual(['urls', 'waf-blocks']);
    expect(selectionTargets({ kind: 'ngram-zone', zone: 'body' })).toEqual(['links', 'ngrams']);
  });
});

describe('applyChartCrossFilter with spy actions', () => {
  function spyActions() {
    const calls: { loader: string; auditId: string; query: unknown }[] = [];
    const record = (loader: string) => async (auditId: string, query?: unknown) => {
      calls.push({ loader, auditId, query });
      return { ok: true };
    };
    const actions = {
      loadUrlPage: record('urls'),
      loadFindingPage: record('findings'),
      loadLinkPage: record('links'),
      loadNgramPage: record('ngrams')
    } as unknown as CrawlForgeStoreActions;
    return { actions, calls };
  }

  it('dispatches only the matching loaders with mapped queries', async () => {
    const { actions, calls } = spyActions();
    await applyChartCrossFilter(actions, 'a1', { kind: 'ngram-zone', zone: 'body' });
    expect(calls).toEqual([
      { loader: 'links', auditId: 'a1', query: { domZone: 'body' } },
      { loader: 'ngrams', auditId: 'a1', query: { zone: 'body' } }
    ]);
  });

  it('dispatches nothing for host selections and never throws', async () => {
    const { actions, calls } = spyActions();
    await applyChartCrossFilter(actions, 'a1', { kind: 'host', host: 'example.com' });
    expect(calls).toEqual([]);
  });

  it('propagates unexpected loader errors (real store loaders return outcomes instead)', async () => {
    const actions = {
      loadUrlPage: async () => {
        throw new Error('offline');
      }
    } as unknown as CrawlForgeStoreActions;
    await expect(
      applyChartCrossFilter(actions, 'a1', { kind: 'status', statusClass: '4xx' })
    ).rejects.toThrow('offline');
  });
});

describe('cross-filter → accepted store → wire (integration)', () => {
  it('status selection loads the URL window with statusClass on the wire', async () => {
    const { api, calls } = fakeApiClient({
      '/urls': () => ok([], { offset: 0, limit: 50, total: 7 })
    });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    await applyChartCrossFilter(actions, 'a1', { kind: 'status', statusClass: '4xx' });

    expect(calls.length).toBe(1);
    expect(calls[0].url).toContain('/api/audits/a1/urls');
    expect(calls[0].url).toContain('statusClass=4xx');
    const slice = store.getState().urlPages;
    expect(slice.activeKey).not.toBeNull();
    const page = slice.pages[slice.activeKey ?? ''];
    expect(page.total).toBe(7);
  });

  it('severity selection loads the finding window with severity on the wire', async () => {
    const { api, calls } = fakeApiClient({
      '/findings': () => ok([], { offset: 0, limit: 50, total: 4 })
    });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    await applyChartCrossFilter(actions, 'a1', { kind: 'severity', severity: 'error' });

    expect(calls[0].url).toContain('/api/audits/a1/findings');
    expect(calls[0].url).toContain('severity=error');
    expect(store.getState().findingPages.activeKey).not.toBeNull();
  });

  it('zone selection loads link + n-gram windows with zone filters', async () => {
    const { api, calls } = fakeApiClient({
      '/links': () => ok([], { offset: 0, limit: 50, total: 12 }),
      '/ngrams': () => ok([], { offset: 0, limit: 50, total: 30 })
    });
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, api);
    await applyChartCrossFilter(actions, 'a1', { kind: 'ngram-zone', zone: 'title' });

    expect(calls.length).toBe(2);
    const linkCall = calls.find((call) => call.url.includes('/links'));
    const ngramCall = calls.find((call) => call.url.includes('/ngrams'));
    expect(linkCall?.url).toContain('domZone=title');
    expect(ngramCall?.url).toContain('zone=title');
    expect(store.getState().linkPages.activeKey).not.toBeNull();
    expect(store.getState().ngramPages.activeKey).not.toBeNull();
  });

  it('degrades to error outcomes offline without throwing', async () => {
    const store = createCrawlForgeStore();
    const actions = createStoreActions(store, null);
    await expect(
      applyChartCrossFilter(actions, 'a1', { kind: 'status', statusClass: '5xx' })
    ).resolves.toBeUndefined();
    expect(store.getState().urlPages.activeKey).toBeNull();
  });
});
