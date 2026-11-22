/**
 * Shared fixtures + harness for the rendered surfaces suites (WP-10-03).
 *
 * Every finding fixture is the ACTUAL evidence JSON the WP-10-02
 * `ResponseRenderedComparer` persists (camelCase `evidence.details`, locked
 * `render.*` rule keys, lowercase severity wire values). The late-meta and
 * js-content fixtures mirror the acceptance fixture site
 * (`app/tests/fixture-site/pages.mjs`): late-meta changes title + canonical and
 * injects a rendered-only noindex; js-content injects ~200 words via script.
 *
 * The fake routes implement the locked `audits.getUrl` / `audits.listFindings`
 * (ruleKey/urlId/category/severity filters + offset/limit pagination) /
 * `audits.listUrls` semantics so the component proofs run through the real
 * typed loopback client over real wire shapes.
 */

import type {
  Finding,
  UrlDetail,
  UrlRecordSummary
} from '../../../src/frontend/src/contracts/api/audits';
import type { ConsoleMessage, RenderTestResponse, RenderedNetworkEntry } from '../../../src/frontend/src/contracts/api/tools';
import type { LoopbackApiClient } from '../../../src/frontend/src/contracts/api/client';
import { createLoopbackApiClient } from '../../../src/frontend/src/store/apiClient';
import type { RenderedArtifactLoader, RenderedUrlArtifacts } from '../../../src/frontend/src/surfaces/explorers/rendered/renderedArtifacts';
import { ok, err, fakeFetch, type FetchCall } from '../shell/helpers';

export {
  ok, err, fakeFetch, render, flush, byTestId, fire, click, type,
  installDomStubs, type FetchCall, type Mounted
} from '../shell/helpers';
export { installGridStubs, primeGrid, setScrollGeometry, triggerResizeObservers } from '../grid/helpers';

export const AUDIT = 'audit-1';
export const FIXTURE_BASE = 'http://127.0.0.1:47311';

/** A 1×1 transparent PNG (valid base64, no data-URL prefix). */
export const PNG_1X1 =
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==';

/** All matches for a test id. */
export function byTestIdAll(container: HTMLElement, id: string): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));
}

/** Trimmed text content of a node (or ''). */
export function textOf(element: Element | null): string {
  return (element?.textContent ?? '').trim();
}

// ── Finding fixtures (WP-10-02 comparer evidence shapes) ───────────────────

/** Finding factory with wire defaults. */
export function renderFinding(partial: Partial<Finding> & { id: string; ruleKey: string; urlId: string }): Finding {
  return {
    severity: 'notice',
    category: 'indexability',
    message: 'rendered difference',
    ...partial
  };
}

/** late-meta.html — title changes after script (Error, dual evidence). */
export const lateMetaTitle = renderFinding({
  id: 'f-lm-title',
  ruleKey: 'render.title-delta',
  severity: 'error',
  category: 'indexability',
  urlId: 'url-late-meta',
  message: 'Title differs between the HTTP response and the rendered DOM',
  evidence: {
    details: {
      field: 'title',
      responseValue: 'Raw title before script',
      renderedValue: 'Rendered title after script'
    }
  }
});

/** late-meta.html — canonical changes after script (Error, dual evidence). */
export const lateMetaCanonical = renderFinding({
  id: 'f-lm-canonical',
  ruleKey: 'render.canonical-delta',
  severity: 'error',
  category: 'indexability',
  urlId: 'url-late-meta',
  message: 'Canonical differs between the HTTP response and the rendered DOM',
  evidence: {
    details: {
      field: 'canonical',
      responseValue: `${FIXTURE_BASE}/pages/late-meta.html`,
      renderedValue: `${FIXTURE_BASE}/pages/late-meta-other.html`
    }
  }
});

/** late-meta.html — rendered-only noindex (CRITICAL, dual evidence). */
export const lateMetaNoindex = renderFinding({
  id: 'f-lm-noindex',
  ruleKey: 'render.noindex-delta',
  severity: 'error',
  category: 'indexability',
  urlId: 'url-late-meta',
  message: 'CRITICAL: noindex is present only in the rendered DOM — search engines that render see a noindex the raw response does not carry',
  evidence: {
    details: {
      field: 'noindex',
      critical: true,
      direction: 'rendered-only',
      rawNoindex: false,
      renderedNoindex: true,
      rawMetaNoindex: false,
      rawHeaderNoindex: false,
      renderedMetaNoindex: true,
      rawDirectives: '',
      renderedDirectives: 'noindex',
      rawNoindexContents: [],
      renderedNoindexContents: ['noindex'],
      responseValue: 'index',
      renderedValue: 'noindex'
    }
  }
});

/** js-content.html — script injects ~200 words (Notice, word-count evidence). */
export const jsContentText = renderFinding({
  id: 'f-jc-text',
  ruleKey: 'render.text-delta',
  severity: 'notice',
  category: 'content',
  urlId: 'url-js-content',
  message: 'Visible text differs between the HTTP response and the rendered DOM',
  evidence: {
    details: {
      field: 'text',
      responseValue: 'words=3; Script-injected content',
      renderedValue: 'words=203; Script-injected content injected…',
      rawWordCount: 3,
      renderedWordCount: 203,
      totalWordCount: 203,
      words: 203,
      wordCountDelta: 200,
      wordCountChangeFraction: 0.9852
    }
  }
});

/** js-link.html — a link exists only after rendering (Notice). */
export const jsLinkDelta = renderFinding({
  id: 'f-jl-links',
  ruleKey: 'render.links-delta',
  severity: 'notice',
  category: 'linking',
  urlId: 'url-js-link',
  message: 'Link set differs between the HTTP response and the rendered DOM',
  evidence: {
    details: {
      field: 'links',
      responseValue: `${FIXTURE_BASE}/pages/js-content.html`,
      renderedValue: `${FIXTURE_BASE}/pages/js-content.html\n${FIXTURE_BASE}/pages/js-only-target.html`,
      renderedOnlyLinks: [`${FIXTURE_BASE}/pages/js-only-target.html`],
      rawOnlyLinks: [],
      renderedOnlyCount: 1,
      rawOnlyCount: 0
    }
  }
});

/** A status delta page (Warning) for severity-ordering proofs. */
export const statusDelta = renderFinding({
  id: 'f-st-status',
  ruleKey: 'render.status-delta',
  severity: 'warning',
  category: 'indexability',
  urlId: 'url-status',
  message: 'Status differs between the HTTP response and the rendered DOM',
  evidence: { details: { field: 'status', responseValue: '200', renderedValue: '404' } }
});

/** structured-data.html — JSON-LD changes after render (Notice). */
export const structuredDataDelta = renderFinding({
  id: 'f-sd-delta',
  ruleKey: 'render.structured-data-delta',
  severity: 'notice',
  category: 'structureddata',
  urlId: 'url-structured-data',
  message: 'Structured data differs between the HTTP response and the rendered DOM',
  evidence: { details: { field: 'structured-data', responseValue: 'json-ld:Product', renderedValue: 'json-ld:Product\njson-ld:Offer' } }
});

/** images.html — resource set changes after render (Notice). */
export const resourcesDelta = renderFinding({
  id: 'f-im-resources',
  ruleKey: 'render.resources-delta',
  severity: 'notice',
  category: 'performance',
  urlId: 'url-images',
  message: 'Resource set differs between the HTTP response and the rendered DOM',
  evidence: { details: { field: 'resources', responseValue: 'img:/static/pixel.png', renderedValue: 'img:/static/pixel.png\nimg:/static/lazy.png' } }
});

/** service-worker registration (Notice, informational — NOT a delta). */
export const serviceWorkerRegistered = renderFinding({
  id: 'f-sw-reg',
  ruleKey: 'render.service-worker-registered',
  severity: 'notice',
  category: 'indexability',
  urlId: 'url-service-worker',
  message: 'A service worker is registered by this page; renders bypass service workers',
  evidence: { details: { registered: true, bypassed: true, scriptUrl: '/sw.js', observedIn: 'rendered' } }
});

/** Every render.* finding across the fixture audit. */
export function allRenderFindings(): Finding[] {
  return [
    lateMetaTitle,
    lateMetaCanonical,
    lateMetaNoindex,
    jsContentText,
    jsLinkDelta,
    statusDelta,
    structuredDataDelta,
    resourcesDelta,
    serviceWorkerRegistered
  ];
}

// ── URL records / detail ───────────────────────────────────────────────────

/** URL record factory. */
export function urlRecord(partial: Partial<UrlRecordSummary> & { id: string; canonicalUrl: string }): UrlRecordSummary {
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

/** The fixture audit's URL inventory (capture sources per WP-10-02). */
export function fixtureUrlRecords(): UrlRecordSummary[] {
  return [
    urlRecord({ id: 'url-late-meta', canonicalUrl: `${FIXTURE_BASE}/pages/late-meta.html`, title: 'Rendered title after script', captureSource: 'both', discoverySource: 'link' }),
    urlRecord({ id: 'url-js-content', canonicalUrl: `${FIXTURE_BASE}/pages/js-content.html`, title: 'Script-injected content', captureSource: 'rendered', discoverySource: 'link' }),
    urlRecord({ id: 'url-js-link', canonicalUrl: `${FIXTURE_BASE}/pages/js-link.html`, title: 'JavaScript-only link', captureSource: 'rendered', discoverySource: 'link' }),
    urlRecord({ id: 'url-status', canonicalUrl: `${FIXTURE_BASE}/pages/status.html`, title: 'Status change', captureSource: 'both' }),
    urlRecord({ id: 'url-structured-data', canonicalUrl: `${FIXTURE_BASE}/pages/structured-data.html`, title: 'Structured data errors', captureSource: 'both' }),
    urlRecord({ id: 'url-images', canonicalUrl: `${FIXTURE_BASE}/pages/images.html`, title: 'Images without alt text', captureSource: 'both' }),
    urlRecord({ id: 'url-service-worker', canonicalUrl: `${FIXTURE_BASE}/pages/sw.html`, title: 'Service worker page', captureSource: 'rendered' }),
    urlRecord({ id: 'url-clean', canonicalUrl: `${FIXTURE_BASE}/pages/clean.html`, title: 'Clean page', captureSource: 'raw' })
  ];
}

/** late-meta URL detail wire (findings carry the dual evidence). */
export function lateMetaDetail(): UrlDetail {
  const record = fixtureUrlRecords()[0];
  return {
    ...record,
    responseHeaders: { 'content-type': 'text/html' },
    redirectChain: [],
    inboundLinks: [],
    outboundLinks: [],
    findings: [lateMetaTitle, lateMetaCanonical, lateMetaNoindex],
    elementInventory: { counts: { document_head: 1, headings: 1, resources: 1, text_statistics: 1, link_relations: 1 }, totalElements: 5 },
    scopeDiagnostics: { included: true, matchedIncludeSelectors: [], matchedExcludeSelectors: [], scopedWordCount: 156, totalWordCount: 156 }
  };
}

/** js-content URL detail wire. */
export function jsContentDetail(): UrlDetail {
  const record = fixtureUrlRecords()[1];
  return {
    ...record,
    responseHeaders: { 'content-type': 'text/html' },
    redirectChain: [],
    inboundLinks: [],
    outboundLinks: [],
    findings: [jsContentText],
    elementInventory: { counts: { document_head: 1, headings: 1, resources: 1, text_statistics: 1, link_relations: 1 }, totalElements: 5 },
    scopeDiagnostics: { included: true, matchedIncludeSelectors: [], matchedExcludeSelectors: [], scopedWordCount: 203, totalWordCount: 203 }
  };
}

/** A clean URL detail (no render deltas). */
export function cleanDetail(): UrlDetail {
  const record = fixtureUrlRecords()[7];
  return {
    ...record,
    responseHeaders: {},
    redirectChain: [],
    inboundLinks: [],
    outboundLinks: [],
    findings: [],
    elementInventory: { counts: { document_head: 1, headings: 1, resources: 1, text_statistics: 1, link_relations: 1 }, totalElements: 5 }
  };
}

// ── Rendered artifacts + render-test response ──────────────────────────────

/** A realistic captured network waterfall (locked RenderedNetworkEntry). */
export function fixtureWaterfall(): RenderedNetworkEntry[] {
  return [
    { url: `${FIXTURE_BASE}/pages/late-meta.html`, resourceType: 'document', method: 'GET', statusCode: 200, mimeType: 'text/html', transferredBytes: 1480, encodedDataLength: 1320, fromCache: false, fromServiceWorker: false },
    { url: `${FIXTURE_BASE}/static/app.js`, resourceType: 'script', method: 'GET', statusCode: 200, mimeType: 'application/javascript', transferredBytes: 5200, fromCache: false, fromServiceWorker: false },
    { url: `${FIXTURE_BASE}/static/pixel.png`, resourceType: 'image', method: 'GET', statusCode: 200, mimeType: 'image/png', transferredBytes: 95, fromCache: true, fromServiceWorker: false }
  ];
}

/** Console messages captured during rendering. */
export function fixtureConsole(): ConsoleMessage[] {
  return [
    { level: 'log', text: 'late-meta script ran', timestamp: '2026-11-20T08:00:00Z' },
    { level: 'warn', text: 'canonical rewritten by script', timestamp: null },
    { level: 'error', text: 'Uncaught reference (fixture)', timestamp: null }
  ];
}

/** late-meta rendered artifacts (RenderedDom / Screenshot / Waterfall / console). */
export function lateMetaArtifacts(): RenderedUrlArtifacts {
  return {
    renderedDom: '<html><head><title>Rendered title after script</title><link rel="canonical" href="/pages/late-meta-other.html"><meta name="robots" content="noindex"></head><body><h1>Late meta change</h1></body></html>',
    screenshotBase64: PNG_1X1,
    consoleMessages: fixtureConsole(),
    networkWaterfall: fixtureWaterfall()
  };
}

/** An artifact loader returning the supplied artifacts (coordinator stand-in). */
export function makeArtifactLoader(artifacts: RenderedUrlArtifacts | null): RenderedArtifactLoader {
  return async () => artifacts;
}

/** A locked render-test response (real CDP pool output shape). */
export function renderTestResponse(overrides: Partial<RenderTestResponse> = {}): RenderTestResponse {
  return {
    screenshotBase64: PNG_1X1,
    renderedHtml: '<html><head><title>Rendered title after script</title></head><body><h1>Late meta change</h1></body></html>',
    consoleMessages: fixtureConsole(),
    networkWaterfall: fixtureWaterfall(),
    wafVerdict: null,
    pickerActive: true,
    ...overrides
  };
}

// ── Fake wire routes + typed client ────────────────────────────────────────

/** Options for the rendered fake routes. */
export interface RenderedRoutesOptions {
  readonly details?: Readonly<Record<string, UrlDetail>>;
  readonly findings?: readonly Finding[];
  readonly urls?: readonly UrlRecordSummary[];
  /** Force an envelope error on every route. */
  readonly failWith?: { code: string; message: string };
}

/** Parses the query string of a recorded call URL. */
export function queryParams(call: FetchCall): URLSearchParams {
  return new URL(call.url).searchParams;
}

/** Fake routes for the rendered surfaces (locked query semantics). */
export function renderedRoutes(options: RenderedRoutesOptions = {}): Record<string, (call: FetchCall) => unknown> {
  const details = options.details ?? {
    'url-late-meta': lateMetaDetail(),
    'url-js-content': jsContentDetail(),
    'url-clean': cleanDetail()
  };
  const findings = options.findings ?? allRenderFindings();
  const urls = options.urls ?? fixtureUrlRecords();
  const fail = () => err(options.failWith!.code, options.failWith!.message);

  return {
    // URL detail: /api/audits/{id}/urls/{urlId}
    '/urls/': (call) => {
      if (options.failWith) return fail();
      const urlId = call.url.split('/urls/')[1]?.split('?')[0] ?? '';
      const detail = details[urlId];
      return detail ? ok(detail) : err('not-found', `No URL detail for ${urlId}`);
    },
    // URL inventory: /api/audits/{id}/urls?offset=&limit=
    '/urls?': (call) => {
      if (options.failWith) return fail();
      const params = queryParams(call);
      const offset = Number(params.get('offset') ?? 0);
      const limit = Number(params.get('limit') ?? 50);
      const rows = urls.slice(offset, offset + limit);
      return ok(rows, { offset, limit, total: urls.length });
    },
    // Findings: /api/audits/{id}/findings?ruleKey=&urlId=&category=&severity=
    '/findings': (call) => {
      if (options.failWith) return fail();
      const params = queryParams(call);
      const ruleKey = params.get('ruleKey');
      const urlId = params.get('urlId');
      const category = params.get('category');
      const severity = params.get('severity');
      let rows = findings.slice();
      if (ruleKey) rows = rows.filter((f) => f.ruleKey === ruleKey);
      if (urlId) rows = rows.filter((f) => f.urlId === urlId);
      if (category) rows = rows.filter((f) => f.category === category);
      if (severity) rows = rows.filter((f) => f.severity === severity);
      const offset = Number(params.get('offset') ?? 0);
      const limit = Number(params.get('limit') ?? 50);
      return ok(rows.slice(offset, offset + limit), { offset, limit, total: rows.length });
    }
  };
}

/** Typed fake client over the rendered routes. */
export function renderedClient(options: RenderedRoutesOptions = {}): { api: LoopbackApiClient; calls: FetchCall[] } {
  const { fetchLike, calls } = fakeFetch(renderedRoutes(options));
  const api = createLoopbackApiClient({ baseUrl: 'http://127.0.0.1:5000', fetch: fetchLike });
  return { api, calls };
}

/** Finds recorded calls whose URL contains a fragment. */
export function callsTo(calls: readonly FetchCall[], fragment: string): FetchCall[] {
  return calls.filter((call) => call.url.includes(fragment));
}
