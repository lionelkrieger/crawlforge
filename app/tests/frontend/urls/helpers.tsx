/**
 * Shared helpers for the URL explorer suites (WP-09-09).
 *
 * Fixtures are ACTUAL REST wire JSON: inventory rows mirror
 * UrlRecordSummaryWire (AuditExplorerWireContracts.cs), detail payloads
 * mirror UrlDetailWire + the WP-09-06 redirect-chain mapping
 * ({hops:[{ordinal,url,toUrl,statusCode}], finalUrl, finalStatusCode,
 * loopDetected, chainCapExceeded}) with the legacy flat RedirectHopWire[]
 * and redirectTargetUrl fallbacks. The fake routes implement the locked
 * ListUrlsAsync server semantics (statusClass/contentType/depth/indexable/
 * search filters, sort whitelist, offset/limit pagination) so filter and
 * virtualization proofs run through the real typed client.
 */

import type { ReactNode } from 'react';
import type {
  UrlRecordSummary,
  UrlDetail,
  LinkEdge,
  Finding
} from '../../../src/frontend/src/contracts/api/audits';
import type { LoopbackApiClient } from '../../../src/frontend/src/contracts/api/client';
import type { ApiEnvelope } from '../../../src/frontend/src/contracts/common/envelope';
import { ShellProvider, type ShellServices } from '../../../src/frontend/src/shell/ShellContext';
import type { UrlDetailWireView } from '../../../src/frontend/src/surfaces/explorers/urls/types';
import {
  render,
  byTestId,
  flush,
  click,
  fire,
  ok,
  err,
  fakeServices,
  offlineServices,
  installDomStubs,
  type FetchCall,
  type Mounted
} from '../shell/helpers';
import { installGridStubs, primeGrid, setScrollGeometry, triggerResizeObservers } from '../grid/helpers';

export {
  render,
  byTestId,
  flush,
  click,
  fire,
  ok,
  err,
  fakeServices,
  offlineServices,
  installDomStubs,
  installGridStubs,
  primeGrid,
  setScrollGeometry,
  triggerResizeObservers,
  type FetchCall,
  type Mounted
};

/** The acceptance fixture site origin (app/tests/fixture-site). */
export const FIXTURE_BASE = 'http://127.0.0.1:47311';

/** All matches for a test id. */
export function byTestIdAll(container: HTMLElement, id: string): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
}

/** Trimmed text content of a node (or ''). */
export function textOf(element: Element | null): string {
  return (element?.textContent ?? '').trim();
}

/** Renders a URL surface inside the shell context (no Mantine needed). */
export function renderUrls(ui: ReactNode, services: ShellServices): Promise<Mounted> {
  return render(<ShellProvider services={services}>{ui}</ShellProvider>);
}

// ── Inventory rows (UrlRecordSummaryWire JSON, fixture site) ─────────────

/** One inventory row factory with wire defaults. */
export function urlRow(partial: Partial<UrlRecordSummary> & { id: string; canonicalUrl: string }): UrlRecordSummary {
  return {
    statusCode: 200,
    statusClass: '2xx',
    contentType: 'text/html',
    depth: 1,
    indexable: true,
    title: null,
    ...partial
  };
}

/**
 * The fixture-site inventory as the REST list route returns it. CR-024:
 * noindex.html is fetchable (200) but NOT search-indexable; the canonical
 * conflict page is not self-canonical → not search-indexable either.
 */
export function fixtureUrlRows(): UrlRecordSummary[] {
  return [
    urlRow({ id: 'url-home', canonicalUrl: `${FIXTURE_BASE}/`, depth: 0, title: 'Fixture Site' }),
    urlRow({ id: 'url-noindex', canonicalUrl: `${FIXTURE_BASE}/pages/noindex.html`, indexable: false, title: 'Noindex page' }),
    urlRow({ id: 'url-canonical-conflict', canonicalUrl: `${FIXTURE_BASE}/pages/canonical-conflict.html`, indexable: false, title: 'Canonical conflict' }),
    urlRow({ id: 'url-chain-1', canonicalUrl: `${FIXTURE_BASE}/redirect/chain-1`, statusCode: 301, statusClass: '3xx', indexable: false, depth: 1 }),
    urlRow({ id: 'url-chain-2', canonicalUrl: `${FIXTURE_BASE}/redirect/chain-2`, statusCode: 302, statusClass: '3xx', indexable: false, depth: 2 }),
    urlRow({ id: 'url-chain-3', canonicalUrl: `${FIXTURE_BASE}/redirect/chain-3`, statusCode: 301, statusClass: '3xx', indexable: false, depth: 3 }),
    urlRow({ id: 'url-redirect-final', canonicalUrl: `${FIXTURE_BASE}/pages/redirect-final.html`, title: 'Redirect final', depth: 4 }),
    urlRow({ id: 'url-broken-target', canonicalUrl: `${FIXTURE_BASE}/pages/broken-link-target.html`, statusCode: 404, statusClass: '4xx', indexable: false, title: null }),
    urlRow({ id: 'url-orphan', canonicalUrl: `${FIXTURE_BASE}/pages/orphan.html`, title: 'Orphan page', depth: 3 }),
    urlRow({ id: 'url-thin', canonicalUrl: `${FIXTURE_BASE}/pages/thin.html`, title: 'Thin page' }),
    urlRow({ id: 'url-dup-a', canonicalUrl: `${FIXTURE_BASE}/pages/dup-title-a.html`, title: 'Duplicate title' }),
    urlRow({ id: 'url-dup-b', canonicalUrl: `${FIXTURE_BASE}/pages/dup-title-b.html`, title: 'Duplicate title' }),
    urlRow({ id: 'url-soft-404', canonicalUrl: `${FIXTURE_BASE}/pages/soft-404.html`, title: 'Page not found' }),
    urlRow({ id: 'url-postback', canonicalUrl: `${FIXTURE_BASE}/postback.aspx`, title: 'PostBack form' }),
    urlRow({ id: 'url-images', canonicalUrl: `${FIXTURE_BASE}/pages/images.html`, title: 'Images page' }),
    urlRow({ id: 'url-llms', canonicalUrl: `${FIXTURE_BASE}/llms.txt`, contentType: 'text/plain', title: null }),
    urlRow({ id: 'url-sitemap', canonicalUrl: `${FIXTURE_BASE}/sitemap.xml`, contentType: 'application/xml', title: null }),
    urlRow({ id: 'url-css', canonicalUrl: `${FIXTURE_BASE}/assets/style.css`, contentType: 'text/css', title: null }),
    urlRow({ id: 'url-js', canonicalUrl: `${FIXTURE_BASE}/assets/app.js`, contentType: 'application/javascript', title: null }),
    urlRow({ id: 'url-png', canonicalUrl: `${FIXTURE_BASE}/assets/logo.png`, contentType: 'image/png', title: null })
  ];
}

// ── Detail payloads (UrlDetailWire JSON) ─────────────────────────────────

/** Detail factory: locked UrlDetail defaults + additive wire view fields. */
export function makeDetail(partial: Partial<UrlDetailWireView> & { id: string; canonicalUrl: string }): UrlDetailWireView {
  const base: UrlDetail = {
    statusCode: 200,
    statusClass: '2xx',
    contentType: 'text/html',
    depth: 1,
    indexable: true,
    title: null,
    responseHeaders: {},
    elementInventory: { counts: {}, totalElements: 0 },
    findings: [],
    inboundLinks: [],
    outboundLinks: [],
    redirectChain: [],
    ...partial
  } as UrlDetail;
  return base as UrlDetailWireView;
}

/** Link edge factory (LinkEdgeWire JSON). */
export function linkEdge(partial: Partial<LinkEdge> & { sourceUrlId: string; targetUrlId: string; sourceUrl: string; targetUrl: string }): LinkEdge {
  return { internal: true, anchorText: null, ...partial };
}

/** Finding factory (FindingWire JSON). */
export function finding(partial: Partial<Finding> & { id: string; ruleKey: string }): Finding {
  return {
    severity: 'warning',
    category: 'indexability',
    urlId: null,
    message: `${partial.ruleKey} observed`,
    ...partial
  };
}

/**
 * /redirect/chain-1 detail — the FULL three-hop chain in the WP-09-06
 * storage→wire payload shape (hops with ordinal/url/toUrl/statusCode).
 */
export function chainDetail(): UrlDetailWireView {
  return makeDetail({
    id: 'url-chain-1',
    canonicalUrl: `${FIXTURE_BASE}/redirect/chain-1`,
    statusCode: 301,
    statusClass: '3xx',
    indexable: false,
    depth: 1,
    responseHeaders: { location: `${FIXTURE_BASE}/redirect/chain-2` },
    elementInventory: { counts: { document_head: 1, header_directives: 1 }, totalElements: 2 },
    inboundLinks: [
      linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: 'url-chain-1',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: `${FIXTURE_BASE}/redirect/chain-1`,
        anchorText: 'Redirect chain',
        domZone: 'mainContent',
        equityWeight: 1,
        weightedEquity: 1
      })
    ],
    redirectChain: {
      hops: [
        { ordinal: 1, url: `${FIXTURE_BASE}/redirect/chain-1`, toUrl: `${FIXTURE_BASE}/redirect/chain-2`, statusCode: 301 },
        { ordinal: 2, url: `${FIXTURE_BASE}/redirect/chain-2`, toUrl: `${FIXTURE_BASE}/redirect/chain-3`, statusCode: 302 },
        { ordinal: 3, url: `${FIXTURE_BASE}/redirect/chain-3`, toUrl: `${FIXTURE_BASE}/pages/redirect-final.html`, statusCode: 301 }
      ],
      finalUrl: `${FIXTURE_BASE}/pages/redirect-final.html`,
      finalStatusCode: 200,
      loopDetected: false,
      chainCapExceeded: false
    }
  });
}

/** The same chain in the CURRENT flat RedirectHopWire[] wire form. */
export function legacyFlatChainDetail(): UrlDetailWireView {
  const detail = chainDetail();
  return {
    ...detail,
    redirectChain: [
      { url: `${FIXTURE_BASE}/redirect/chain-1`, statusCode: 301, location: `${FIXTURE_BASE}/redirect/chain-2` },
      { url: `${FIXTURE_BASE}/redirect/chain-2`, statusCode: 302, location: `${FIXTURE_BASE}/redirect/chain-3` },
      { url: `${FIXTURE_BASE}/redirect/chain-3`, statusCode: 301, location: `${FIXTURE_BASE}/pages/redirect-final.html` }
    ]
  };
}

/** Legacy single-hop fallback: no chain row, only redirectTargetUrl. */
export function legacyTargetDetail(): UrlDetailWireView {
  const detail = chainDetail();
  return { ...detail, redirectChain: [], redirectTargetUrl: `${FIXTURE_BASE}/redirect/chain-2` };
}

/** A redirect loop: A → B → A with the engine's loop verdict. */
export function loopChainDetail(): UrlDetailWireView {
  const detail = chainDetail();
  return {
    ...detail,
    redirectChain: {
      hops: [
        { ordinal: 1, url: `${FIXTURE_BASE}/redirect/loop-a`, toUrl: `${FIXTURE_BASE}/redirect/loop-b`, statusCode: 301 },
        { ordinal: 2, url: `${FIXTURE_BASE}/redirect/loop-b`, toUrl: `${FIXTURE_BASE}/redirect/loop-a`, statusCode: 302 },
        { ordinal: 3, url: `${FIXTURE_BASE}/redirect/loop-a`, toUrl: `${FIXTURE_BASE}/redirect/loop-b`, statusCode: 301 }
      ],
      finalUrl: `${FIXTURE_BASE}/redirect/loop-a`,
      finalStatusCode: 301,
      loopDetected: true,
      chainCapExceeded: false
    }
  };
}

/** A capped chain: the engine stopped at the hop cap. */
export function cappedChainDetail(): UrlDetailWireView {
  const detail = chainDetail();
  return {
    ...detail,
    redirectChain: {
      ...(detail.redirectChain as object),
      chainCapExceeded: true
    }
  } as UrlDetailWireView;
}

/** /pages/noindex.html detail — CR-024: fetchable 200, NOT search-indexable. */
export function noindexDetail(): UrlDetailWireView {
  return makeDetail({
    id: 'url-noindex',
    canonicalUrl: `${FIXTURE_BASE}/pages/noindex.html`,
    indexable: false,
    title: 'Noindex page',
    elementInventory: { counts: { document_head: 1, links: 2 }, totalElements: 3 },
    elements: [
      {
        elementGroup: 'document_head',
        payloadJson: JSON.stringify({
          title: 'Noindex page',
          metaDescription: 'A page excluded from search indexing.',
          metaRobots: 'noindex,follow',
          viewport: 'width=device-width, initial-scale=1',
          charset: 'utf-8',
          canonicalUrl: null,
          alternates: [],
          openGraph: {},
          twitterCard: {},
          favicons: []
        })
      }
    ],
    findings: [
      finding({
        id: 'f-noindex',
        ruleKey: 'indexability.noindex',
        severity: 'warning',
        urlId: 'url-noindex',
        message: 'Page carries a noindex directive (meta robots) and is excluded from search indexing.',
        evidence: { details: { source: 'meta-robots', directive: 'noindex', value: 'noindex,follow' } }
      })
    ],
    inboundLinks: [
      linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: 'url-noindex',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: `${FIXTURE_BASE}/pages/noindex.html`,
        anchorText: 'Noindex page',
        domZone: 'navigation'
      })
    ]
  });
}

/**
 * /pages/canonical-conflict.html detail — BOTH canonical targets:
 * HTML <link rel=canonical> → canonical-target-html.html,
 * HTTP Link header → canonical-target-header.html.
 */
export function canonicalConflictDetail(): UrlDetailWireView {
  const htmlTarget = `${FIXTURE_BASE}/pages/canonical-target-html.html`;
  const headerTarget = `${FIXTURE_BASE}/pages/canonical-target-header.html`;
  return makeDetail({
    id: 'url-canonical-conflict',
    canonicalUrl: `${FIXTURE_BASE}/pages/canonical-conflict.html`,
    indexable: false,
    title: 'Canonical conflict',
    responseHeaders: { link: `<${headerTarget}>; rel="canonical"` },
    elementInventory: { counts: { document_head: 1, header_directives: 1 }, totalElements: 2 },
    elements: [
      {
        elementGroup: 'document_head',
        payloadJson: JSON.stringify({
          title: 'Canonical conflict',
          metaDescription: 'HTML and header canonicals disagree.',
          metaRobots: null,
          canonicalUrl: htmlTarget,
          alternates: [{ hreflang: 'en', href: `${FIXTURE_BASE}/pages/canonical-conflict.html` }]
        })
      },
      {
        elementGroup: 'header_directives',
        payloadJson: JSON.stringify({ xRobotsTag: null, link: `<${headerTarget}>; rel="canonical"` })
      }
    ],
    headerConflicts: [
      { headerName: 'link', headerValue: `<${headerTarget}>; rel="canonical"`, htmlValue: htmlTarget, conflict: true }
    ],
    findings: [
      finding({
        id: 'f-canonical-conflict',
        ruleKey: 'canonical.header-html-conflict',
        severity: 'error',
        category: 'indexability',
        urlId: 'url-canonical-conflict',
        message: 'Canonical target differs between HTTP Link header and HTML.',
        evidence: { details: { headerValue: `<${headerTarget}>; rel="canonical"`, htmlValue: htmlTarget } }
      })
    ]
  });
}

/** Home detail with a long title/description (SERP truncation proof). */
export function serpLongDetail(): UrlDetailWireView {
  return makeDetail({
    id: 'url-home',
    canonicalUrl: `${FIXTURE_BASE}/`,
    depth: 0,
    title: 'Fixture Site Home — The Complete Catalogue of Redirect Chains, Canonical Conflicts, Noindex Pages, and Broken Link Targets for Acceptance Testing',
    metaDescription: 'The fixture site exercises every acceptance trap: three-hop redirect chains, redirect loops, capped chains, noindex directives in meta robots and X-Robots-Tag headers, canonical conflicts between HTML and HTTP headers, hreflang alternates, broken links, orphan pages, thin content, duplicate titles, soft 404s, postback forms, and asset inventories for images, stylesheets, scripts, and documents.',
    elementInventory: { counts: { document_head: 1, links: 8, images: 2 }, totalElements: 11 },
    serpMetrics: { titlePixelWidth: 1420.5, descriptionPixelWidth: 1890.2, titleTruncated: true, descriptionTruncated: true },
    outboundLinks: [
      linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: 'url-broken-target',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: `${FIXTURE_BASE}/pages/broken-link-target.html`,
        anchorText: 'Broken target',
        domZone: 'mainContent'
      }),
      linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: 'url-noindex',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: `${FIXTURE_BASE}/pages/noindex.html`,
        anchorText: 'Noindex page',
        domZone: 'navigation'
      }),
      linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: '',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: 'https://external.example.com/reference',
        internal: false,
        anchorText: 'External reference',
        domZone: 'footer'
      })
    ]
  });
}

/** Home detail with short title/description (SERP fits proof). */
export function serpShortDetail(): UrlDetailWireView {
  return makeDetail({
    id: 'url-home',
    canonicalUrl: `${FIXTURE_BASE}/`,
    depth: 0,
    title: 'Fixture Site',
    metaDescription: 'Welcome to the fixture site.',
    elementInventory: { counts: { document_head: 1 }, totalElements: 1 }
  });
}

/** /pages/images.html detail with asset lists (record + element rows). */
export function assetsDetail(): UrlDetailWireView {
  return makeDetail({
    id: 'url-images',
    canonicalUrl: `${FIXTURE_BASE}/pages/images.html`,
    title: 'Images page',
    elementInventory: { counts: { document_head: 1, images: 2 }, totalElements: 3 },
    assets: {
      image: [`${FIXTURE_BASE}/assets/logo.png`, { url: `${FIXTURE_BASE}/assets/photo.jpg`, alt: 'Fixture photo' }],
      css: [`${FIXTURE_BASE}/assets/style.css`],
      js: [`${FIXTURE_BASE}/assets/app.js`]
    },
    elements: [
      { elementGroup: 'images', payloadJson: JSON.stringify({ src: `${FIXTURE_BASE}/assets/diagram.svg`, alt: 'Architecture diagram' }) },
      { elementGroup: 'images', payloadJson: JSON.stringify({ src: `${FIXTURE_BASE}/assets/logo.png`, alt: 'Logo' }) }
    ]
  });
}

/** Detail map keyed by urlId (the REST detail route's lookup). */
export function fixtureDetails(): Record<string, UrlDetailWireView> {
  return {
    'url-home': serpLongDetail(),
    'url-noindex': noindexDetail(),
    'url-canonical-conflict': canonicalConflictDetail(),
    'url-chain-1': chainDetail(),
    'url-images': assetsDetail()
  };
}

// ── Fake REST routes (locked ListUrlsAsync + GetUrlDetailAsync semantics) ─

/** Options for the fake URL routes. */
export interface UrlRouteOptions {
  readonly rows?: readonly UrlRecordSummary[];
  readonly details?: Record<string, UrlDetailWireView>;
  /** Generate N synthetic rows per window (virtualization proof). */
  readonly syntheticTotal?: number;
  /** Fail the list route with this error code. */
  readonly failList?: string;
  /** Fail the detail route with this error code. */
  readonly failDetail?: string;
}

const SORT_WHITELIST = ['id', 'canonicalUrl', 'depth', 'statusCode'] as const;

/** Server-side list semantics: filters + sort whitelist + offset/limit. */
function listResponder(options: UrlRouteOptions) {
  const rows = options.rows ?? fixtureUrlRows();
  return (call: FetchCall): unknown => {
    if (options.failList) return err(options.failList, 'Injected list failure');
    const url = new URL(call.url);
    const params = url.searchParams;
    const offset = Number.parseInt(params.get('offset') ?? '0', 10) || 0;
    const limit = Number.parseInt(params.get('limit') ?? '200', 10) || 200;

    if (options.syntheticTotal !== undefined) {
      const total = options.syntheticTotal;
      const page: UrlRecordSummary[] = [];
      for (let i = offset; i < Math.min(offset + limit, total); i += 1) {
        page.push(urlRow({
          id: `url-${i}`,
          canonicalUrl: `${FIXTURE_BASE}/page/${i}`,
          depth: i % 9,
          indexable: i % 2 === 0,
          title: `Page ${i}`
        }));
      }
      return ok(page, { offset, limit, total });
    }

    let filtered = [...rows];
    const statusClass = params.get('statusClass');
    if (statusClass) filtered = filtered.filter((row) => row.statusClass === statusClass);
    const contentType = params.get('contentType');
    if (contentType) filtered = filtered.filter((row) => row.contentType === contentType);
    const depth = params.get('depth');
    if (depth !== null) filtered = filtered.filter((row) => row.depth === Number.parseInt(depth, 10));
    const indexable = params.get('indexable');
    if (indexable !== null) filtered = filtered.filter((row) => row.indexable === (indexable === 'true'));
    const search = params.get('search');
    if (search) filtered = filtered.filter((row) => row.canonicalUrl.toLowerCase().includes(search.toLowerCase()));

    const sorts = params.getAll('sort').filter((field) => (SORT_WHITELIST as readonly string[]).includes(field));
    const orders = params.getAll('order');
    if (sorts.length > 0) {
      filtered.sort((a, b) => {
        for (let i = 0; i < sorts.length; i += 1) {
          const field = sorts[i] as keyof UrlRecordSummary;
          const dir = orders[i] === 'desc' ? -1 : 1;
          const av = a[field];
          const bv = b[field];
          if (av === bv) continue;
          if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * dir;
          return String(av).localeCompare(String(bv)) * dir;
        }
        return 0;
      });
    }

    return ok(filtered.slice(offset, offset + limit), { offset, limit, total: filtered.length });
  };
}

/** Detail route: /urls/{urlId} lookup in the fixture map. */
function detailResponder(options: UrlRouteOptions) {
  const details = options.details ?? fixtureDetails();
  return (call: FetchCall): unknown => {
    if (options.failDetail) return err(options.failDetail, 'Injected detail failure');
    const url = new URL(call.url);
    const urlId = url.pathname.split('/').pop() ?? '';
    const detail = details[urlId];
    if (!detail) return err('not-found', `URL ${urlId} does not exist in audit 1.`);
    return ok(detail);
  };
}

/**
 * Fake routes for the URL surface. The detail fragment ('/urls/') is
 * registered BEFORE the list fragment ('/urls') so detail URLs match the
 * detail responder (fakeFetch matches by substring in insertion order).
 */
export function urlRouteFakes(options: UrlRouteOptions = {}): Record<string, (call: FetchCall) => unknown> {
  return {
    '/urls/': detailResponder(options),
    '/urls': listResponder(options)
  };
}

/** Services wired to the fake URL routes. */
export function urlServices(options: UrlRouteOptions = {}) {
  return fakeServices(urlRouteFakes(options));
}

// ── Controlled API stub (loading/error injection) ────────────────────────

/** Handler signature for the stub client. */
export type StubHandler = (route: string, init: unknown) => Promise<ApiEnvelope<unknown>>;

/** A LoopbackApiClient stub with full control over latency and errors. */
export function stubApi(handler: StubHandler): LoopbackApiClient {
  return {
    baseUrl: 'http://127.0.0.1:5000',
    request: handler as LoopbackApiClient['request']
  };
}

/** A promise the test resolves manually (loading-state proof). */
export function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (error: Error) => void } {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
