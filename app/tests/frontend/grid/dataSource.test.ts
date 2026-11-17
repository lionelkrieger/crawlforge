/**
 * Data source proofs: typed-client window requests, the three locked
 * response shapes, envelope/transport errors as values, multi-sort wire
 * expression, and offline honesty.
 */

import { describe, expect, it } from 'vitest';
import {
  createExplorerDataSource,
  createOfflineDataSource,
  createMemoryDataSource,
  arrayAdapter,
  dynamicColumnsAdapter
} from '../../../src/frontend/src/grid/dataSource';
import type { UrlRecordSummary } from '../../../src/frontend/src/contracts/api/audits';
import type { ExtractedContentRow } from '../../../src/frontend/src/contracts/api/content';
import { fakeApiClient, ok, err, seedUrls } from './helpers';

describe('createExplorerDataSource', () => {
  it('requests offset/limit windows through the typed client', async () => {
    const rows = seedUrls(450);
    const { api, calls } = fakeApiClient({
      '/urls': (call) => {
        const url = new URL(call.url);
        const offset = Number(url.searchParams.get('offset') ?? 0);
        const limit = Number(url.searchParams.get('limit') ?? 200);
        return ok(rows.slice(offset, offset + limit), { offset, limit, total: rows.length });
      }
    });
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    const result = await source.fetchPage({ offset: 200, limit: 200 });
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.page.rows).toHaveLength(200);
      expect(result.page.total).toBe(450);
      expect(result.page.offset).toBe(200);
      expect(result.page.rows[0].id).toBe('url-200');
    }
    expect(calls[0].url).toContain('/api/audits/audit-1/urls');
    expect(calls[0].url).toContain('offset=200');
    expect(calls[0].url).toContain('limit=200');
  });

  it('merges base domain filters under every window request', async () => {
    const { api, calls } = fakeApiClient({ '/urls': () => ok([], { offset: 0, limit: 200, total: 0 }) });
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      baseQuery: { statusClass: '2xx' },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    await source.fetchPage({ offset: 0, search: 'hello' });
    expect(calls[0].url).toContain('statusClass=2xx');
    expect(calls[0].url).toContain('search=hello');
  });

  it('expresses multi-column sort as repeated sort/order fields', async () => {
    const { api, calls } = fakeApiClient({ '/urls': () => ok([], { offset: 0, limit: 200, total: 0 }) });
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    await source.fetchPage({ offset: 0, sort: ['depth', 'statusCode'], order: ['asc', 'desc'] });
    const url = new URL(calls[0].url);
    expect(url.searchParams.getAll('sort')).toEqual(['depth', 'statusCode']);
    expect(url.searchParams.getAll('order')).toEqual(['asc', 'desc']);
  });

  it('adapts dynamic-column payloads with the effective column order', async () => {
    const rows: ExtractedContentRow[] = [
      { urlId: 'u1', canonicalUrl: 'https://example.com/a', ruleKey: 'product', values: { price: '9.99', stock: true } }
    ];
    const { api } = fakeApiClient({
      '/extracted-content': () => ok({ columns: ['price', 'stock'], rows }, { offset: 0, limit: 200, total: 1 })
    });
    const source = createExplorerDataSource<ExtractedContentRow, 'audits.listExtractedContent'>({
      api,
      route: 'audits.listExtractedContent',
      params: { id: 'audit-1' },
      adapter: dynamicColumnsAdapter<ExtractedContentRow, 'audits.listExtractedContent'>()
    });
    const result = await source.fetchPage({ offset: 0 });
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.page.columns).toEqual(['price', 'stock']);
      expect(result.page.rows[0].values.price).toBe('9.99');
    }
  });

  it('converts envelope errors to error results (never throws)', async () => {
    const { api } = fakeApiClient({ '/urls': () => err('validation', 'Bad filter', { statusClass: ['unknown class'] }) });
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    const result = await source.fetchPage({ offset: 0 });
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errorCode).toBe('validation');
      expect(result.errorMessage).toBe('Bad filter');
    }
  });

  it('converts transport rejections to error results', async () => {
    const api = {
      request: async () => { throw new Error('ECONNREFUSED'); }
    } as never;
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    const result = await source.fetchPage({ offset: 0 });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errorCode).toBe('transport');
  });

  it('omits undefined/null filters from the wire', async () => {
    const { api, calls } = fakeApiClient({ '/urls': () => ok([], { offset: 0, limit: 200, total: 0 }) });
    const source = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: 'audit-1' },
      baseQuery: { statusClass: undefined, contentType: null },
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
    await source.fetchPage({ offset: 0 });
    expect(calls[0].url).not.toContain('statusClass');
    expect(calls[0].url).not.toContain('contentType');
  });

  it('partitions source identity by route + params', () => {
    const { api } = fakeApiClient({});
    const a = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api, route: 'audits.listUrls', params: { id: 'a1' }, adapter: arrayAdapter()
    });
    const b = createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api, route: 'audits.listUrls', params: { id: 'a2' }, adapter: arrayAdapter()
    });
    expect(a.sourceKey).not.toBe(b.sourceKey);
  });
});

describe('createOfflineDataSource', () => {
  it('returns honest offline errors, never fabricated rows', async () => {
    const source = createOfflineDataSource<UrlRecordSummary>('offline');
    const result = await source.fetchPage({ offset: 0 });
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errorCode).toBe('offline');
  });
});

describe('createMemoryDataSource', () => {
  it('serves bounded windows with the full total', async () => {
    const rows = seedUrls(500_000);
    const source = createMemoryDataSource('memory', rows, ['price']);
    const result = await source.fetchPage({ offset: 499_800, limit: 200 });
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.page.total).toBe(500_000);
      expect(result.page.rows).toHaveLength(200);
      expect(result.page.columns).toEqual(['price']);
      expect(result.page.rows[0].id).toBe('url-499800');
    }
  });
});
