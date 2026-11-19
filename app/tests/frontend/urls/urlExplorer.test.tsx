// @vitest-environment jsdom
/**
 * UrlExplorer surface proofs (WP-09-09, FS-CRAWL-002 §7.1 + §8.2, UI-13):
 * root testid `url-explorer`, ≤50 DOM rows at 10k total through the real
 * typed client, server-side advanced filters (status / CR-024 indexability
 * / depth / content type), whitelist sorting, pinning, custom columns,
 * drill-down callback + detail drawer, and empty/loading/error/offline
 * states. All payloads are the actual REST wire JSON (see helpers.tsx).
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { UrlExplorer, type UrlExplorerProps } from '../../../src/frontend/src/surfaces/explorers/urls/UrlExplorer';
import type { UrlOpenTarget } from '../../../src/frontend/src/surfaces/explorers/urls/types';
import type { UrlRecordSummary } from '../../../src/frontend/src/contracts/api/audits';
import { MAX_DOM_ROWS } from '../../../src/frontend/src/grid/types';
import {
  FIXTURE_BASE,
  byTestId,
  byTestIdAll,
  textOf,
  click,
  fire,
  flush,
  ok,
  err,
  urlServices,
  offlineServices,
  renderUrls,
  installGridStubs,
  primeGrid,
  setScrollGeometry,
  fixtureUrlRows,
  fixtureDetails,
  makeDetail,
  chainDetail,
  stubApi,
  deferred,
  type UrlRouteOptions
} from './helpers';

beforeEach(() => {
  document.body.replaceChildren();
  installGridStubs();
});

/** Mounts the explorer against the fake REST routes and primes the grid. */
async function mountExplorer(options: UrlRouteOptions = {}, props: Partial<UrlExplorerProps> = {}) {
  const { services, calls } = urlServices(options);
  const mounted = await renderUrls(<UrlExplorer auditId="1" {...props} />, services);
  await flush(20);
  await primeGrid(mounted.container);
  await flush(20);
  return { ...mounted, calls, services };
}

/** Last list-route fetch call (the /urls?… window requests). */
function lastListCall(calls: { url: string }[]): URL {
  const listCalls = calls.filter((call) => call.url.includes('/urls?') || call.url.endsWith('/urls'));
  expect(listCalls.length).toBeGreaterThan(0);
  return new URL(listCalls[listCalls.length - 1].url);
}

/** Drives a controlled <input> through React's onChange. */
function inputValue(input: HTMLInputElement, value: string): void {
  act(() => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (setter) setter.call(input, value);
    else input.value = value;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

/** Drives a controlled <select> through React's onChange. */
function selectValue(select: HTMLSelectElement, value: string): void {
  act(() => {
    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
    if (setter) setter.call(select, value);
    else select.value = value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

describe('UI-13 — surface identity', () => {
  it('renders the url-explorer root testid (also satisfies UI-11)', async () => {
    const mounted = await mountExplorer();
    const root = byTestId(mounted.container, 'url-explorer');
    expect(root).not.toBeNull();
    expect(root!.querySelector('[data-testid="cf-grid"]')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'cf-grid-total'))).toContain('20 rows');
    mounted.unmount();
  });

  it('asks for an audit selection when none is active', async () => {
    const { services } = urlServices();
    const mounted = await renderUrls(<UrlExplorer auditId={null} />, services);
    expect(byTestId(mounted.container, 'url-explorer')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-explorer-state'))).toContain('Select an audit');
    mounted.unmount();
  });
});

describe('§7.1 — virtualization at 10k rows', () => {
  it('mounts ≤50 DOM rows for a 10,000-row inventory without materializing it', async () => {
    const mounted = await mountExplorer({ syntheticTotal: 10_000 });
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    const dataRows = body.querySelectorAll('[data-row-index]');
    expect(dataRows.length).toBeGreaterThan(0);
    expect(dataRows.length).toBeLessThanOrEqual(MAX_DOM_ROWS);
    expect(textOf(byTestId(mounted.container, 'cf-grid-total'))).toContain('10,000');

    // Only bounded windows were fetched — never the dataset.
    const listCalls = mounted.calls.filter((call) => call.url.includes('/urls?'));
    expect(listCalls.length).toBeLessThanOrEqual(2);
    for (const call of listCalls) {
      const limit = Number.parseInt(new URL(call.url).searchParams.get('limit') ?? '200', 10);
      expect(limit).toBeLessThanOrEqual(200);
    }
    mounted.unmount();
  });

  it('keeps the ≤50 cap while sliding the window to row 5,000', async () => {
    const mounted = await mountExplorer({ syntheticTotal: 10_000 });
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    setScrollGeometry(body, { scrollTop: 36 * 5000 });
    await flush(30);

    const dataRows = body.querySelectorAll('[data-row-index]');
    expect(dataRows.length).toBeGreaterThan(0);
    expect(dataRows.length).toBeLessThanOrEqual(MAX_DOM_ROWS);
    const indices = Array.from(dataRows).map((row) => Number.parseInt(row.getAttribute('data-row-index') ?? '0', 10));
    expect(Math.min(...indices)).toBeGreaterThanOrEqual(4900);
    expect(body.textContent).toContain('/page/5000');
    mounted.unmount();
  });
});

describe('§8.2 — advanced filters ride the locked server query', () => {
  it('status=4xx lists the broken-link target (phase-gate proof)', async () => {
    const mounted = await mountExplorer();
    selectValue(byTestId(mounted.container, 'url-explorer-filter-status') as HTMLSelectElement, '4xx');
    await flush(30);

    const query = lastListCall(mounted.calls).searchParams;
    expect(query.get('statusClass')).toBe('4xx');
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    expect(body.textContent).toContain('/pages/broken-link-target.html');
    expect(body.textContent).toContain('404');
    expect(body.textContent).not.toContain('/pages/noindex.html');
    mounted.unmount();
  });

  it('indexable=false lists noindex.html — CR-024 search-indexable semantics', async () => {
    const mounted = await mountExplorer();
    selectValue(byTestId(mounted.container, 'url-explorer-filter-indexable') as HTMLSelectElement, 'false');
    await flush(30);

    const query = lastListCall(mounted.calls).searchParams;
    expect(query.get('indexable')).toBe('false');
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    // noindex.html is fetchable (200) but NOT search-indexable.
    expect(body.textContent).toContain('/pages/noindex.html');
    expect(body.textContent).toContain('200');
    const noindexRow = Array.from(body.querySelectorAll('[data-row-index]'))
      .find((row) => row.textContent?.includes('/pages/noindex.html'));
    expect(noindexRow).toBeDefined();
    expect(noindexRow!.textContent).toContain('No');
    mounted.unmount();
  });

  it('depth filter travels as the server depth param', async () => {
    const mounted = await mountExplorer();
    inputValue(byTestId(mounted.container, 'url-explorer-filter-depth') as HTMLInputElement, '0');
    await flush(30);

    expect(lastListCall(mounted.calls).searchParams.get('depth')).toBe('0');
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    expect(body.textContent).toContain(`${FIXTURE_BASE}/`);
    expect(body.textContent).not.toContain('/pages/');
    mounted.unmount();
  });

  it('content-type filter lists only matching records', async () => {
    const mounted = await mountExplorer();
    selectValue(byTestId(mounted.container, 'url-explorer-filter-content-type') as HTMLSelectElement, 'text/plain');
    await flush(30);

    expect(lastListCall(mounted.calls).searchParams.get('contentType')).toBe('text/plain');
    const body = byTestId(mounted.container, 'cf-grid-body')!;
    expect(body.textContent).toContain('/llms.txt');
    expect(body.textContent).not.toContain('/pages/noindex.html');
    mounted.unmount();
  });

  it('clear filters restores the full inventory', async () => {
    const mounted = await mountExplorer();
    selectValue(byTestId(mounted.container, 'url-explorer-filter-status') as HTMLSelectElement, '4xx');
    await flush(30);
    click(byTestId(mounted.container, 'url-explorer-filters-clear')!);
    await flush(30);

    const query = lastListCall(mounted.calls).searchParams;
    expect(query.get('statusClass')).toBeNull();
    expect(textOf(byTestId(mounted.container, 'cf-grid-total'))).toContain('20 rows');
    mounted.unmount();
  });
});

describe('§7.1 — sorting, pinning, custom columns', () => {
  it('sorts server-side within the route whitelist', async () => {
    const mounted = await mountExplorer();
    click(byTestId(mounted.container, 'cf-grid-sort-statusCode')!);
    await flush(30);

    const query = lastListCall(mounted.calls).searchParams;
    expect(query.getAll('sort')).toEqual(['statusCode']);
    expect(textOf(byTestId(mounted.container, 'cf-grid-sort-indicator-statusCode'))).toContain('▲');

    // Shift-click adds a second sort column (multi-column server sort).
    fire(byTestId(mounted.container, 'cf-grid-sort-depth')!, 'click', { shiftKey: true });
    await flush(30);
    expect(lastListCall(mounted.calls).searchParams.getAll('sort')).toEqual(['statusCode', 'depth']);
    mounted.unmount();
  });

  it('pins the URL column left through the shared grid affordance', async () => {
    const mounted = await mountExplorer();
    click(byTestId(mounted.container, 'cf-grid-pin-canonicalUrl')!);
    await flush(10);
    const header = byTestId(mounted.container, 'cf-grid-header-canonicalUrl')!;
    expect(header.className).toContain('cf-grid-pinned-left');
    mounted.unmount();
  });

  it('custom columns: opt-in blockReason, opt-out title', async () => {
    const mounted = await mountExplorer();
    expect(byTestId(mounted.container, 'cf-grid-header-blockReason')).toBeNull();

    click(byTestId(mounted.container, 'url-explorer-columns')!);
    const panel = byTestId(mounted.container, 'url-explorer-columns-panel')!;
    expect(panel).not.toBeNull();

    click(byTestId(mounted.container, 'url-explorer-column-toggle-blockReason')!);
    await flush(10);
    expect(byTestId(mounted.container, 'cf-grid-header-blockReason')).not.toBeNull();

    click(byTestId(mounted.container, 'url-explorer-column-toggle-title')!);
    await flush(10);
    expect(byTestId(mounted.container, 'cf-grid-header-title')).toBeNull();
    mounted.unmount();
  });
});

describe('§8.2 — drill-down handoff', () => {
  it('fires onOpenUrl and renders the detail drawer from audits.getUrl', async () => {
    const opened: UrlOpenTarget[] = [];
    const mounted = await mountExplorer({}, { onOpenUrl: (target: UrlOpenTarget) => opened.push(target) });

    const firstCell = mounted.container.querySelector('[data-cf-row="0"][data-cf-col="0"]')!;
    click(firstCell);
    await flush(30);

    expect(opened).toHaveLength(1);
    expect(opened[0]).toEqual({ auditId: '1', urlId: 'url-home', canonicalUrl: `${FIXTURE_BASE}/` });
    expect(byTestId(mounted.container, 'url-explorer-detail')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-url'))).toBe(`${FIXTURE_BASE}/`);
    expect(mounted.calls.some((call) => call.url.includes('/urls/url-home'))).toBe(true);
    mounted.unmount();
  });

  it('drills further through internal outlinks (anchor + domZone rows)', async () => {
    const opened: UrlOpenTarget[] = [];
    const details = {
      ...fixtureDetails(),
      'url-broken-target': makeDetail({
        id: 'url-broken-target',
        canonicalUrl: `${FIXTURE_BASE}/pages/broken-link-target.html`,
        statusCode: 404,
        statusClass: '4xx',
        indexable: false
      })
    };
    const mounted = await mountExplorer({ details }, { onOpenUrl: (target: UrlOpenTarget) => opened.push(target) });

    click(mounted.container.querySelector('[data-cf-row="0"][data-cf-col="0"]')!);
    await flush(30);

    const outlinks = byTestId(mounted.container, 'url-detail-outlinks')!;
    expect(outlinks.textContent).toContain('Broken target');
    expect(outlinks.querySelector('[data-dom-zone="mainContent"]')).not.toBeNull();

    const brokenRow = Array.from(outlinks.querySelectorAll('[data-testid="url-detail-link"]'))
      .find((row) => row.textContent?.includes('broken-link-target.html'))!;
    click(brokenRow.querySelector('[data-testid="url-detail-link-target"]')!);
    await flush(30);

    expect(opened[opened.length - 1]).toEqual({
      auditId: '1',
      urlId: 'url-broken-target',
      canonicalUrl: `${FIXTURE_BASE}/pages/broken-link-target.html`
    });
    expect(textOf(byTestId(mounted.container, 'url-detail-url'))).toContain('broken-link-target.html');
    expect(textOf(byTestId(mounted.container, 'url-detail-status'))).toContain('404');
    mounted.unmount();
  });

  it('external link targets are not drillable', async () => {
    const mounted = await mountExplorer();
    click(mounted.container.querySelector('[data-cf-row="0"][data-cf-col="0"]')!);
    await flush(30);
    const externalRow = Array.from(byTestId(mounted.container, 'url-detail-outlinks')!.querySelectorAll('[data-testid="url-detail-link"]'))
      .find((row) => row.textContent?.includes('external.example.com'))!;
    const button = externalRow.querySelector('[data-testid="url-detail-link-target"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    mounted.unmount();
  });
});

describe('honest states — empty / loading / error / offline', () => {
  it('shows the empty inventory hint when the audit has no URLs', async () => {
    const mounted = await mountExplorer({ rows: [] });
    expect(textOf(byTestId(mounted.container, 'url-explorer-empty'))).toContain('no URL records yet');
    mounted.unmount();
  });

  it('shows the filter-specific empty hint when filters exclude everything', async () => {
    const mounted = await mountExplorer({}, { initialFilters: { statusClass: '5xx' } });
    await flush(30);
    expect(textOf(byTestId(mounted.container, 'url-explorer-empty'))).toContain('No URLs match the current filters');
    mounted.unmount();
  });

  it('surfaces list errors through the grid error state', async () => {
    const mounted = await mountExplorer({ failList: 'internal' });
    expect(textOf(byTestId(mounted.container, 'cf-grid-error'))).toContain('Injected list failure');
    mounted.unmount();
  });

  it('shows the loading state while the first window is in flight', async () => {
    const gate = deferred<ReturnType<typeof ok<UrlRecordSummary[]>>>();
    const rows = fixtureUrlRows();
    const api = stubApi(async (route) => {
      if (route === 'audits.listUrls') return gate.promise;
      return err('not-found', 'unexpected route');
    });
    const { services } = urlServices();
    const mounted = await renderUrls(<UrlExplorer auditId="1" api={api} />, services);
    await flush(10);
    expect(textOf(byTestId(mounted.container, 'cf-grid-total'))).toContain('Loading');

    act(() => gate.resolve(ok(rows.slice(0, 200), { offset: 0, limit: 200, total: rows.length })));
    await flush(20);
    await primeGrid(mounted.container);
    await flush(20);
    expect(textOf(byTestId(mounted.container, 'cf-grid-total'))).toContain('20 rows');
    mounted.unmount();
  });

  it('shows the offline banner without a backend connection', async () => {
    const mounted = await renderUrls(<UrlExplorer auditId="1" />, offlineServices());
    expect(textOf(byTestId(mounted.container, 'url-explorer-offline'))).toContain('Backend disconnected');
    mounted.unmount();
  });

  it('shows detail loading, then the detail error with a working retry', async () => {
    const rows = fixtureUrlRows();
    let detailAttempts = 0;
    const gate = deferred<ReturnType<typeof ok<unknown>>>();
    const api = stubApi(async (route) => {
      if (route === 'audits.listUrls') {
        return ok(rows.slice(0, 200), { offset: 0, limit: 200, total: rows.length });
      }
      detailAttempts += 1;
      if (detailAttempts === 1) return gate.promise;
      return ok(chainDetail());
    });
    const { services } = urlServices();
    const mounted = await renderUrls(<UrlExplorer auditId="1" api={api} />, services);
    await flush(20);
    await primeGrid(mounted.container);
    await flush(20);

    click(mounted.container.querySelector('[data-cf-row="0"][data-cf-col="0"]')!);
    await flush(10);
    expect(byTestId(mounted.container, 'url-detail-loading')).not.toBeNull();

    act(() => gate.resolve(err('internal', 'Injected detail failure')));
    await flush(20);
    expect(textOf(byTestId(mounted.container, 'url-detail-error'))).toContain('Injected detail failure');

    click(byTestId(mounted.container, 'url-detail-retry')!);
    await flush(20);
    expect(byTestId(mounted.container, 'url-detail')).not.toBeNull();
    expect(byTestIdAll(mounted.container, 'url-detail-hop')).toHaveLength(3);
    expect(detailAttempts).toBe(2);
    mounted.unmount();
  });
});
