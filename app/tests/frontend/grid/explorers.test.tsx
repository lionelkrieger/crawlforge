// @vitest-environment jsdom
/**
 * Explorer surface proofs: all eleven explorers render seeded server windows
 * through ExplorerHost; dynamic extraction columns expand; view tabs switch
 * domain filters; summary strips derive from locked response meta; the query
 * builder composes/saves locked rule trees; drill-downs fetch real detail;
 * offline and no-audit states stay honest.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { ShellProvider } from '../../../src/frontend/src/shell/ShellContext';
import { ExplorerHost } from '../../../src/frontend/src/surfaces/explorers/ExplorerHost';
import {
  EXPLORERS, EXPLORER_BY_ID, EXPLORER_NAV, eraseExplorerDefinition,
  urlExplorer, linkExplorer, extractionExplorer, logExplorer, analyticsExplorer, wafExplorer
} from '../../../src/frontend/src/surfaces/explorers';
import type { ExplorerDefinition } from '../../../src/frontend/src/surfaces/explorers/explorerTypes';
import type { UrlDetail } from '../../../src/frontend/src/contracts/api/audits';
import { fakeServices, offlineServices, ok, render, flush, byTestId, click, type, installGridStubs, primeGrid, type FetchCall } from './helpers';

const AUDIT = 'audit-1';

/** Seeds every explorer route (order matters: specific paths first). */
function explorerRoutes() {
  const savedQueries: { id: string; name: string; ruleTree: unknown; createdAt: string; updatedAt: string }[] = [
    { id: 'q-1', name: 'Saved errors', ruleTree: { kind: 'group', operator: 'and', children: [{ kind: 'condition', field: 'statusClass', operator: 'eq', value: '5xx' }] }, createdAt: '2026-11-14T10:00:00Z', updatedAt: '2026-11-14T10:00:00Z' }
  ];
  const urlDetail: UrlDetail = {
    id: 'url-0',
    canonicalUrl: 'https://example.com/page/0',
    statusCode: 200,
    statusClass: '2xx',
    contentType: 'text/html',
    depth: 0,
    indexable: true,
    title: 'Home page',
    responseHeaders: { 'content-type': 'text/html', 'x-frame-options': 'DENY' },
    elementInventory: { counts: { a: 42, img: 7 }, totalElements: 310 },
    findings: [{ id: 'f-0', ruleKey: 'title-length', severity: 'warning', category: 'metadata', urlId: 'url-0', message: 'Title exceeds 60 characters' }],
    inboundLinks: [],
    outboundLinks: [],
    redirectChain: [{ url: 'http://example.com', statusCode: 301, location: 'https://example.com' }],
    wafDetection: null,
    serpMetrics: { titlePixelWidth: 540, descriptionPixelWidth: 900, titleTruncated: false, descriptionTruncated: true }
  };
  return {
    '/urls/url-': () => ok(urlDetail),
    '/urls': () => ok([
      { id: 'url-0', canonicalUrl: 'https://example.com/page/0', statusCode: 200, statusClass: '2xx', contentType: 'text/html', depth: 0, indexable: true, title: 'Home page' },
      { id: 'url-1', canonicalUrl: 'https://example.com/page/1', statusCode: 404, statusClass: '4xx', contentType: 'text/html', depth: 1, indexable: false, title: null },
      { id: 'url-2', canonicalUrl: 'https://example.com/page/2', statusCode: 503, statusClass: '5xx', contentType: null, depth: 2, indexable: false, title: null, blockReason: 'waf-challenge', wafProvider: 'cloudflare' }
    ], { offset: 0, limit: 200, total: 3 }),
    '/findings': () => ok([
      { id: 'f-0', ruleKey: 'title-length', severity: 'warning', category: 'metadata', urlId: 'url-0', message: 'Title exceeds 60 characters', evidence: { snippet: '<title>…</title>', selector: 'head > title' } },
      { id: 'f-1', ruleKey: 'broken-link', severity: 'critical', category: 'links', urlId: 'url-1', message: 'Internal link returns 404' }
    ], { offset: 0, limit: 200, total: 2 }),
    '/links': () => ok([
      { sourceUrlId: 'url-0', targetUrlId: 'url-1', sourceUrl: 'https://example.com/page/0', targetUrl: 'https://example.com/page/1', internal: true, anchorText: 'Page one', domZone: 'content', equityWeight: 1, weightedEquity: 0.85 },
      { sourceUrlId: 'url-0', targetUrlId: 'url-9', sourceUrl: 'https://example.com/page/0', targetUrl: 'https://external.example/x', internal: false, anchorText: 'External', domZone: 'footer', equityWeight: 0.4, weightedEquity: 0.1 }
    ], { offset: 0, limit: 200, total: 2 }),
    '/extracted-content/summary': () => ok({ rules: [{ ruleKey: 'product-schema', pagesMatched: 12, valuesCaptured: 48, discards: 2, truncations: 0 }] }),
    '/extracted-content': () => ok({
      columns: ['price', 'stock'],
      rows: [
        { urlId: 'url-0', canonicalUrl: 'https://example.com/page/0', ruleKey: 'product-schema', values: { price: '9.99', stock: true } },
        { urlId: 'url-1', canonicalUrl: 'https://example.com/page/1', ruleKey: 'product-schema', values: { price: ['1.00', '2.00', '3.00'], stock: false } }
      ]
    }, { offset: 0, limit: 200, total: 2 }),
    '/ngrams': () => ok([
      { ngram: 'crawl forge', n: 2, zone: 'content', count: 42, density: 0.021 },
      { ngram: 'seo', n: 1, zone: 'title', count: 30, density: 0.015, urlId: 'url-0' }
    ], { offset: 0, limit: 200, total: 2 }),
    '/logs/correlation': () => ok({
      summary: { orphanCount: 7, budgetWasteCount: 3, uncrawledIndexableCount: 2 },
      rows: [
        { url: 'https://example.com/orphan', canonicalUrl: null, urlId: null, hits: 120, botHits: 95, lastSeen: '2026-11-13T10:00:00Z' },
        { url: 'https://example.com/page/1', canonicalUrl: 'https://example.com/page/1', urlId: 'url-1', hits: 40, botHits: 12, lastSeen: '2026-11-13T09:00:00Z' }
      ]
    }, { offset: 0, limit: 200, total: 2 }),
    '/semantics/link-opportunities': () => ok([
      { sourceUrlId: 'url-0', sourceCanonicalUrl: 'https://example.com/page/0', targetUrlId: 'url-1', targetCanonicalUrl: 'https://example.com/page/1', similarity: 0.87, anchorSuggestion: 'related guide' }
    ], { offset: 0, limit: 200, total: 1 }),
    '/geo/governance': () => ok([
      { botName: 'GPTBot', host: 'example.com', allowed: false, robotsTxtAllowed: false, llmsTxtAllowed: null, notes: 'Disallowed in robots.txt' },
      { botName: 'ClaudeBot', host: 'example.com', allowed: true, robotsTxtAllowed: true, llmsTxtAllowed: true, notes: null }
    ], { offset: 0, limit: 200, total: 2 }),
    '/geo/llms-txt': () => ok([
      { host: 'example.com', url: 'https://example.com/llms.txt', present: true, valid: false, issues: ['Missing H1'], checkedAt: '2026-11-13T08:00:00Z' }
    ], { offset: 0, limit: 200, total: 1 }),
    '/analytics/coverage': () => ok([
      { containerId: 'GTM-ABC', containerName: 'Main container', indexablePages: 100, pagesWithContainer: 95, coverage: 0.95 }
    ], { offset: 0, limit: 200, total: 1 }),
    '/analytics': () => ok({
      tags: [
        { name: 'Google Tag Manager', containerId: 'GTM-ABC', kind: 'container', urlId: 'url-0', occurrences: 3 },
        { name: 'Meta Pixel', containerId: null, kind: 'pixel', urlId: 'url-1', occurrences: 1 }
      ],
      consent: { consentModeV2Present: true, beaconPresent: false, pagesWithConsent: 80, pagesWithoutConsent: 20 },
      findings: [{ id: 'af-0', ruleKey: 'consent-missing', severity: 'warning', category: 'consent', urlId: 'url-1', message: 'Tag fires before consent' }]
    }, { offset: 0, limit: 200, total: 2 }),
    '/redirect-matrix/results': () => ok([
      { urlId: 'url-0', sourceUrl: 'https://old.example/a', expectedTargetUrl: 'https://example.com/page/0', verdict: 'pass', hopChain: [{ url: 'https://old.example/a', statusCode: 301, location: 'https://example.com/page/0' }], canonicalUrl: 'https://example.com/page/0', evidence: '301 → canonical match' },
      { urlId: 'url-1', sourceUrl: 'https://old.example/b', expectedTargetUrl: 'https://example.com/page/1', verdict: 'fail', hopChain: [], canonicalUrl: null, evidence: 'No redirect observed' }
    ], { offset: 0, limit: 200, total: 2 }),
    '/waf-blocks/summary': () => ok({ totalBlocked: 14, blockRate: 0.07, byProvider: { cloudflare: 10, akamai: 4 }, byChallengeClass: { 'js-challenge': 9, interstitial: 5 } }),
    '/waf-blocks': () => ok([
      { urlId: 'url-2', canonicalUrl: 'https://example.com/page/2', provider: 'cloudflare', challengeClass: 'js-challenge', confidence: 0.94, signatureKey: 'cf-js-challenge', detectedAt: '2026-11-13T11:00:00Z', evidence: { snippet: 'Checking your browser…', headers: { 'cf-ray': 'abc' } } }
    ], { offset: 0, limit: 200, total: 1 }),
    '/api/queries': (call: FetchCall) => {
      if (call.method === 'POST') {
        const body = JSON.parse(call.body ?? '{}') as { name: string; ruleTree: unknown };
        const created = { id: `q-${savedQueries.length + 1}`, name: body.name, ruleTree: body.ruleTree, createdAt: '2026-11-14T10:00:00Z', updatedAt: '2026-11-14T10:00:00Z' };
        savedQueries.push(created);
        return ok(created);
      }
      if (call.method === 'DELETE') return ok({ id: 'q-1', deleted: true });
      return ok(savedQueries, { offset: 0, limit: 50, total: savedQueries.length });
    }
  };
}

/** Renders one explorer inside the shell provider with seeded routes. */
async function mountExplorer(definition: ExplorerDefinition, routes = explorerRoutes()) {
  const { services, calls } = fakeServices(routes);
  const mounted = await render(
    <ShellProvider services={services}>
      <ExplorerHost definition={definition} auditId={AUDIT} />
    </ShellProvider>
  );
  await flush(40);
  await primeGrid(mounted.container);
  return { mounted, calls, services };
}

/** Selects an option on a native select under React's value tracker. */
function selectOption(select: HTMLSelectElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
  setter?.call(select, value);
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

beforeEach(() => {
  document.body.replaceChildren();
  installGridStubs();
});

describe('explorer registry', () => {
  it('registers all eleven explorers with unique ids and nav metadata', () => {
    expect(EXPLORERS).toHaveLength(11);
    const ids = EXPLORERS.map((definition) => definition.id);
    expect(new Set(ids).size).toBe(11);
    expect(ids).toEqual(['urls', 'issues', 'links', 'extraction', 'ngrams', 'logs', 'semantics', 'geo', 'analytics', 'redirect-matrix', 'waf']);
    expect(EXPLORER_BY_ID.get('urls')?.title).toBe('URL Explorer');
    expect(EXPLORER_NAV).toHaveLength(11);
  });
});

describe('all eleven explorers render seeded windows', () => {
  for (const definition of EXPLORERS) {
    it(`${definition.id} renders rows from its frozen route`, async () => {
      const { mounted } = await mountExplorer(definition);
      const host = byTestId(mounted.container, `cf-explorer-${definition.id}`);
      expect(host).not.toBeNull();
      expect(host!.textContent).toContain(definition.title);
      const body = byTestId(mounted.container, 'cf-grid-body')!;
      const rows = body.querySelectorAll('[data-row-index]');
      expect(rows.length).toBeGreaterThan(0);
      expect(rows.length).toBeLessThanOrEqual(50);
      mounted.unmount();
    });
  }
});

describe('Link explorer server sort mapping (WP-06-03 reopen — defect B)', () => {
  // Backend whitelist: AuditExplorerController.ListLinksAsync TryBindSort.
  // The WP-07-03 real-browser bench caught every UI sort click 400'ing because
  // the sourceUrl/targetUrl columns sent their column ids instead of the
  // whitelisted id fields. This pins the wire mapping at the unit layer.
  const LINK_SORT_WHITELIST = ['id', 'sourceUrlId', 'targetUrlId'];

  it('Source and Target sort clicks send whitelisted server fields', async () => {
    const { mounted, calls } = await mountExplorer(eraseExplorerDefinition(linkExplorer));

    click(byTestId(mounted.container, 'cf-grid-sort-sourceUrl')!);
    await flush(40);
    let last = calls.filter((call) => call.url.includes('/links')).at(-1)!;
    let params = new URL(last.url, 'http://localhost').searchParams;
    expect(LINK_SORT_WHITELIST).toContain(params.get('sort'));
    expect(params.get('sort')).toBe('sourceUrlId');

    click(byTestId(mounted.container, 'cf-grid-sort-targetUrl')!);
    await flush(40);
    last = calls.filter((call) => call.url.includes('/links')).at(-1)!;
    params = new URL(last.url, 'http://localhost').searchParams;
    expect(LINK_SORT_WHITELIST).toContain(params.get('sort'));
    expect(params.get('sort')).toBe('targetUrlId');

    mounted.unmount();
  });
});

describe('sort affordance audit (WP-06-03 reopen — cross-layer whitelist guard)', () => {
  // Every offered sort button must map (meta.serverField ?? column id) to a
  // field the backend route's TryBindSort whitelist accepts; routes that bind
  // no sort parameter offer no sort buttons at all. Whitelists:
  //   urls [id, canonicalUrl, depth, statusCode] · findings [id, ruleKey,
  //   severity, category] · links [id, sourceUrlId, targetUrlId] · ngrams
  //   [df, tf, count] · waf [detectedAt, provider, confidence]; extraction,
  //   logs, semantics, geo, analytics, redirect-matrix bind no sort.
  const EXPECTED_SORTABLE: Record<string, string[]> = {
    urls: ['canonicalUrl', 'depth', 'statusCode'],
    issues: ['category', 'ruleKey', 'severity'],
    links: ['sourceUrl', 'targetUrl'],
    extraction: [],
    ngrams: ['count'],
    logs: [],
    semantics: [],
    geo: [],
    analytics: [],
    'redirect-matrix': [],
    waf: ['confidence', 'detectedAt', 'provider']
  };

  for (const definition of EXPLORERS) {
    it(`${definition.id} offers only server-accepted sort affordances`, async () => {
      const { mounted } = await mountExplorer(definition);
      const buttons = [...mounted.container.querySelectorAll('button[data-testid^="cf-grid-sort-"]')]
        .map((button) => button.getAttribute('data-testid')!.replace('cf-grid-sort-', ''))
        .sort();
      expect(buttons).toEqual([...EXPECTED_SORTABLE[definition.id]].sort());
      mounted.unmount();
    });
  }
});

describe('URL explorer', () => {
  it('renders status badges and opens a drill-down with fetched detail', async () => {
    const { mounted, calls } = await mountExplorer(eraseExplorerDefinition(urlExplorer));
    expect(mounted.container.textContent).toContain('https://example.com/page/0');
    expect(mounted.container.querySelector('.cf-badge-success')).not.toBeNull();

    const cell = mounted.container.querySelector('[data-cf-row="0"][data-cf-col="1"]')!;
    click(cell);
    await flush(40);
    const drawer = byTestId(mounted.container, 'cf-drilldown')!;
    expect(drawer.textContent).toContain('URL record url-0');
    expect(drawer.textContent).toContain('Findings (1)');
    expect(drawer.textContent).toContain('title-length');
    expect(drawer.textContent).toContain('Redirect chain (1 hops)');
    expect(drawer.textContent).toContain('SERP preview');
    expect(calls.some((call) => call.url.includes('/urls/url-0'))).toBe(true);

    click(byTestId(mounted.container, 'cf-drilldown-close')!);
    await flush(10);
    expect(byTestId(mounted.container, 'cf-drilldown')).toBeNull();
    mounted.unmount();
  });
});

describe('Custom Extraction explorer', () => {
  it('expands dynamic columns from the locked response order', async () => {
    const { mounted } = await mountExplorer(eraseExplorerDefinition(extractionExplorer));
    expect(byTestId(mounted.container, 'cf-grid-header-dyn:price')).not.toBeNull();
    expect(byTestId(mounted.container, 'cf-grid-header-dyn:stock')).not.toBeNull();
    // Multi-valued cell expands to a count badge (busiest-page semantics).
    expect(mounted.container.textContent).toContain('3×');
    // Summary strip from the extraction summary route.
    const summary = byTestId(mounted.container, 'cf-explorer-summary')!;
    expect(summary.textContent).toContain('product-schema');
    expect(summary.textContent).toContain('12 pages');
    mounted.unmount();
  });
});

describe('Log File Analyzer', () => {
  it('switches correlation views and shows summary chips from response meta', async () => {
    const { mounted, calls } = await mountExplorer(eraseExplorerDefinition(logExplorer));
    const summary = byTestId(mounted.container, 'cf-explorer-summary')!;
    expect(summary.textContent).toContain('Orphans');
    expect(summary.textContent).toContain('7');
    expect(summary.textContent).toContain('Budget waste');

    click(byTestId(mounted.container, 'cf-explorer-view-budget-waste')!);
    await flush(40);
    const correlationCalls = calls.filter((call) => call.url.includes('/logs/correlation'));
    const last = correlationCalls[correlationCalls.length - 1];
    expect(last.url).toContain('view=budget-waste');
    mounted.unmount();
  });
});

describe('Analytics & Tagging', () => {
  it('shows tag inventory, consent rollup, and findings view', async () => {
    const { mounted, calls } = await mountExplorer(eraseExplorerDefinition(analyticsExplorer));
    expect(mounted.container.textContent).toContain('Google Tag Manager');
    const summary = byTestId(mounted.container, 'cf-explorer-summary')!;
    expect(summary.textContent).toContain('Consent Mode v2');
    expect(summary.textContent).toContain('Main container');

    click(byTestId(mounted.container, 'cf-explorer-view-findings')!);
    await flush(40);
    const analyticsCalls = calls.filter((call) => call.url.includes('/analytics') && !call.url.includes('/coverage'));
    expect(analyticsCalls[analyticsCalls.length - 1].url).toContain('view=findings');
    expect(mounted.container.textContent).toContain('consent-missing');
    mounted.unmount();
  });
});

describe('WAF Blocks', () => {
  it('renders provider badges and the block-rate summary strip', async () => {
    const { mounted } = await mountExplorer(eraseExplorerDefinition(wafExplorer));
    const summary = byTestId(mounted.container, 'cf-explorer-summary')!;
    expect(summary.textContent).toContain('Total blocked');
    expect(summary.textContent).toContain('14');
    expect(summary.textContent).toContain('7.0%');
    expect(summary.textContent).toContain('cloudflare');
    expect(mounted.container.textContent).toContain('js-challenge');
    mounted.unmount();
  });
});

describe('query builder end-to-end', () => {
  it('composes a locked rule tree, applies it live, and saves it', async () => {
    const { mounted, calls } = await mountExplorer(eraseExplorerDefinition(urlExplorer));
    click(byTestId(mounted.container, 'cf-grid-query-toggle')!);
    await flush(20);
    const panel = byTestId(mounted.container, 'cf-qb-panel')!;
    expect(panel).not.toBeNull();
    expect(byTestId(mounted.container, 'cf-qb-depth')!.textContent).toContain('depth 1/16');

    // Add a condition, retarget it to the whitelisted statusClass filter.
    click(byTestId(mounted.container, 'cf-qb-add-condition')!);
    await flush(10);
    selectOption(byTestId(mounted.container, 'cf-qb-field') as HTMLSelectElement, 'statusClass');
    await flush(10);
    selectOption(byTestId(mounted.container, 'cf-qb-value') as HTMLSelectElement, '4xx');
    await flush(10);
    expect(byTestId(mounted.container, 'cf-qb-summary')!.textContent).toContain('Status class eq 4xx');

    click(byTestId(mounted.container, 'cf-qb-apply')!);
    await flush(40);
    const urlCalls = calls.filter((call) => call.url.includes('/urls') && !call.url.includes('/urls/'));
    expect(urlCalls[urlCalls.length - 1].url).toContain('statusClass=4xx');

    // Save as a named query through the frozen /api/queries route.
    click(byTestId(mounted.container, 'cf-grid-query-toggle')!);
    await flush(20);
    type(byTestId(mounted.container, 'cf-qb-save-name') as HTMLInputElement, '4xx pages');
    click(byTestId(mounted.container, 'cf-qb-save')!);
    await flush(40);
    const createCall = calls.find((call) => call.url.includes('/api/queries') && call.method === 'POST');
    expect(createCall).toBeDefined();
    const body = JSON.parse(createCall!.body ?? '{}') as { name: string; ruleTree: { kind: string; operator: string; children: { field: string; operator: string; value: string }[] } };
    expect(body.name).toBe('4xx pages');
    expect(body.ruleTree.kind).toBe('group');
    expect(body.ruleTree.operator).toBe('and');
    expect(body.ruleTree.children[0]).toMatchObject({ field: 'statusClass', operator: 'eq', value: '4xx' });
    // Saved list refresh shows the new query.
    expect(byTestId(mounted.container, 'cf-qb-saved')!.textContent).toContain('4xx pages');
    mounted.unmount();
  });

  it('loads a saved query into the editor', async () => {
    const { mounted } = await mountExplorer(eraseExplorerDefinition(urlExplorer));
    click(byTestId(mounted.container, 'cf-grid-query-toggle')!);
    await flush(30);
    const load = byTestId(mounted.container, 'cf-qb-saved-load-q-1');
    expect(load).not.toBeNull();
    click(load!);
    await flush(10);
    expect(byTestId(mounted.container, 'cf-qb-summary')!.textContent).toContain('Status class eq 5xx');
    mounted.unmount();
  });
});

describe('export handoff', () => {
  it('opens the handoff inspector with the locked payload', async () => {
    const { mounted } = await mountExplorer(eraseExplorerDefinition(urlExplorer));
    click(byTestId(mounted.container, 'cf-grid-select-0') as HTMLInputElement);
    await flush(10);
    click(byTestId(mounted.container, 'cf-grid-export')!);
    await flush(10);
    const inspector = byTestId(mounted.container, 'cf-handoff')!;
    const json = JSON.parse(byTestId(mounted.container, 'cf-handoff-json')!.textContent ?? '{}') as { selectedRowKeys: string[]; sourceKey: string; ruleTree: unknown };
    expect(json.selectedRowKeys).toEqual(['url-0']);
    expect(json.sourceKey).toContain('audits.listUrls');
    expect(inspector.textContent).toContain('Export handoff');
    mounted.unmount();
  });
});

describe('honest preconditions', () => {
  it('shows the disconnected state when the backend is offline', async () => {
    const services = offlineServices();
    const mounted = await render(
      <ShellProvider services={services}>
        <ExplorerHost definition={eraseExplorerDefinition(urlExplorer)} auditId={AUDIT} />
      </ShellProvider>
    );
    await flush(30);
    expect(byTestId(mounted.container, 'cf-explorer-offline')).not.toBeNull();
    mounted.unmount();
  });

  it('prompts for audit selection when none is active', async () => {
    const { services } = fakeServices(explorerRoutes());
    const mounted = await render(
      <ShellProvider services={services}>
        <ExplorerHost definition={eraseExplorerDefinition(urlExplorer)} auditId={null} />
      </ShellProvider>
    );
    await flush(20);
    expect(byTestId(mounted.container, 'cf-explorer-no-audit')).not.toBeNull();
    mounted.unmount();
  });
});
