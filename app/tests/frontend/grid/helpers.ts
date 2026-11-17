/**
 * Shared helpers for the grid suites: seeded row factories, a recording
 * data source with failure/delay injection, and re-exports of the shell
 * jsdom harness.
 */

import type { UrlRecordSummary, HttpStatusClass } from '../../../src/frontend/src/contracts/api/audits';
import type { GridDataSource, GridFetchResult, GridPage, ServerListQuery } from '../../../src/frontend/src/grid/types';
import { GRID_PAGE_SIZE } from '../../../src/frontend/src/grid/types';
import { makeColumn } from '../../../src/frontend/src/surfaces/explorers/columnUtils';
import { installDomStubs, flush } from '../shell/helpers';

export {
  ok, err, fakeFetch, fakeServices, offlineServices, fakeApiClient,
  installDomStubs, render, flush, byTestId, fire, click, type, keyDown,
  type FetchCall, type Mounted
} from '../shell/helpers';

/** Seeds N believable URL records (deterministic). */
export function seedUrls(count: number): UrlRecordSummary[] {
  const classes: HttpStatusClass[] = ['2xx', '2xx', '2xx', '3xx', '4xx', '5xx'];
  const rows: UrlRecordSummary[] = [];
  for (let i = 0; i < count; i += 1) {
    const statusClass = classes[i % classes.length];
    const statusCode = statusClass === '2xx' ? 200 : statusClass === '3xx' ? 301 : statusClass === '4xx' ? 404 : statusClass === '5xx' ? 503 : 100;
    rows.push({
      id: `url-${i}`,
      canonicalUrl: `https://example.com/page/${i}`,
      statusCode,
      statusClass,
      contentType: i % 3 === 0 ? 'text/html' : 'application/json',
      depth: i % 7,
      indexable: statusClass === '2xx' && i % 4 !== 0,
      title: `Page ${i} title`,
      ...(statusClass === '5xx' ? { blockReason: 'waf-challenge', wafProvider: 'cloudflare' } : {})
    });
  }
  return rows;
}

/** Controls for the recording source. */
export interface RecordingSourceOptions {
  readonly pageSize?: number;
  readonly columns?: readonly string[];
  /** Artificial latency per fetch (ms). */
  readonly delayMs?: number;
  /** Fail the next N fetches with this error code. */
  failNext?: number;
  failCode?: string;
}

/** A memory-backed data source that records every window query. */
export interface RecordingSource extends GridDataSource<UrlRecordSummary> {
  readonly queries: ServerListQuery[];
  fetchCount(): number;
  setRows(rows: readonly UrlRecordSummary[]): void;
}

/** Creates a recording source over seeded rows. */
export function recordingSource(rows: readonly UrlRecordSummary[], options: RecordingSourceOptions = {}): RecordingSource {
  const pageSize = options.pageSize ?? GRID_PAGE_SIZE;
  let current = rows;
  const queries: ServerListQuery[] = [];
  let fetches = 0;
  let failuresLeft = options.failNext ?? 0;
  return {
    sourceKey: 'test-source',
    queries,
    fetchCount: () => fetches,
    setRows: (next) => { current = next; },
    async fetchPage(query: ServerListQuery): Promise<GridFetchResult<UrlRecordSummary>> {
      fetches += 1;
      queries.push({ ...query });
      if (options.delayMs) await new Promise((resolve) => setTimeout(resolve, options.delayMs));
      if (failuresLeft > 0) {
        failuresLeft -= 1;
        return { ok: false, errorCode: options.failCode ?? 'internal', errorMessage: 'Injected failure' };
      }
      const offset = query.offset ?? 0;
      const limit = query.limit ?? pageSize;
      const page: GridPage<UrlRecordSummary> = {
        offset,
        limit,
        total: current.length,
        rows: current.slice(offset, offset + limit),
        ...(options.columns ? { columns: options.columns } : {}),
        fetchedAt: Date.now()
      };
      return { ok: true, page };
    }
  };
}

/** Simple URL columns for engine/component tests. */
export function testUrlColumns() {
  return [
    makeColumn<UrlRecordSummary, string>('canonicalUrl', 'URL', (row) => row.canonicalUrl, { size: 300 }),
    makeColumn<UrlRecordSummary, number>('statusCode', 'Status', (row) => row.statusCode, { size: 90, meta: { numeric: true } }),
    makeColumn<UrlRecordSummary, number>('depth', 'Depth', (row) => row.depth, { size: 80, meta: { numeric: true } }),
    makeColumn<UrlRecordSummary, boolean>('indexable', 'Indexable', (row) => row.indexable, { size: 100 })
  ];
}

/** Sets a jsdom scroll element's geometry, notifies observers, and scrolls. */
export function setScrollGeometry(element: HTMLElement, opts: { clientHeight?: number; clientWidth?: number; scrollTop?: number; scrollLeft?: number }): void {
  if (opts.clientHeight !== undefined) Object.defineProperty(element, 'clientHeight', { value: opts.clientHeight, configurable: true });
  if (opts.clientWidth !== undefined) Object.defineProperty(element, 'clientWidth', { value: opts.clientWidth, configurable: true });
  if (opts.scrollTop !== undefined) element.scrollTop = opts.scrollTop;
  if (opts.scrollLeft !== undefined) element.scrollLeft = opts.scrollLeft;
  triggerResizeObservers();
  element.dispatchEvent(new Event('scroll', { bubbles: true }));
}

/**
 * Grid-specific jsdom stubs on top of the shell harness:
 *  - a triggerable ResizeObserver (the virtualizer reads clientWidth/Height
 *    through its rect observer; jsdom performs no layout, so tests drive
 *    geometry explicitly via setScrollGeometry);
 *  - Element.scrollTo, which the virtualizer's scrollToIndex requires.
 */
const resizeObservers: { notify: () => void; elements: Set<Element> }[] = [];

export function installGridStubs(): void {
  installDomStubs();
  resizeObservers.length = 0;
  globalThis.ResizeObserver = class {
    private readonly entry: { notify: () => void; elements: Set<Element> };
    constructor(callback: ResizeObserverCallback) {
      this.entry = {
        notify: () => callback([], this as unknown as ResizeObserver),
        elements: new Set<Element>()
      };
      resizeObservers.push(this.entry);
    }
    observe(element: Element): void { this.entry.elements.add(element); }
    unobserve(element: Element): void { this.entry.elements.delete(element); }
    disconnect(): void { this.entry.elements.clear(); }
  } as unknown as typeof ResizeObserver;
  if (!Element.prototype.scrollTo) {
    Element.prototype.scrollTo = function scrollTo(this: Element, options?: ScrollToOptions | number, y?: number): void {
      const top = typeof options === 'number' ? y ?? 0 : options?.top ?? 0;
      const left = typeof options === 'number' ? options : options?.left ?? 0;
      (this as HTMLElement).scrollTop = top;
      (this as HTMLElement).scrollLeft = left;
      this.dispatchEvent(new Event('scroll', { bubbles: true }));
    } as typeof Element.prototype.scrollTo;
  }
}

/** Fires every live ResizeObserver callback (geometry re-measurement). */
export function triggerResizeObservers(): void {
  for (const observer of [...resizeObservers]) {
    if (observer.elements.size > 0) observer.notify();
  }
}

/** Gives the mounted grid body a real viewport and flushes the first window. */
export async function primeGrid(container: HTMLElement): Promise<void> {
  const body = container.querySelector('[data-testid="cf-grid-body"]') as HTMLElement | null;
  if (!body) return;
  setScrollGeometry(body, { clientHeight: 600, clientWidth: 1200, scrollTop: 0 });
  await flush(20);
}
