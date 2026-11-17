/**
 * Shared helpers for the charts/graphs/dashboards suites (WP-06-04):
 * locked-DTO fixtures, mock renderer/worker ports (jsdom never touches
 * WebGL or canvas), a fake-fetch API client, and the React 19 act+createRoot
 * render harness (same posture as tests/frontend/shell/helpers.ts).
 */

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import type { ReactNode } from 'react';
import type { EChartsOption } from 'echarts';
import type { ApiEnvelope } from '../../../src/frontend/src/contracts/common/envelope';
import type {
  AuditDashboardSummary,
  AuditScores,
  ChartPoint,
  GraphData
} from '../../../src/frontend/src/contracts/api/audits';
import type { HostState } from '../../../src/frontend/src/contracts/api/hosts';
import type { WafBlocksSummary } from '../../../src/frontend/src/contracts/api/waf';
import type { NgramRow, TopicalGap } from '../../../src/frontend/src/contracts/api/ngrams';
import type { LogCorrelationSummary } from '../../../src/frontend/src/contracts/api/logs';
import type { LlmsTxtValidation, BotGovernanceRow } from '../../../src/frontend/src/contracts/api/geo';
import type { AnalyticsConsentSummary, ContainerCoverage } from '../../../src/frontend/src/contracts/api/analytics';
import type { LinkOpportunity, SemanticCluster } from '../../../src/frontend/src/contracts/api/semantics';
import type { ChartClickParams, ChartRenderer, ChartRendererFactory } from '../../../src/frontend/src/charts/chartPorts';
import type { GraphRendererFactory, GraphRendererPort } from '../../../src/frontend/src/graphs/graphPorts';
import type { CrawlGraph } from '../../../src/frontend/src/graphs/graphModel';
import type { LayoutRequest, LayoutResult } from '../../../src/frontend/src/graphs/layoutCore';
import { computeLayout } from '../../../src/frontend/src/graphs/layoutCore';
import type { LayoutWorkerFactory, LayoutWorkerPort } from '../../../src/frontend/src/graphs/layoutClient';
import type { LoopbackApiClient } from '../../../src/frontend/src/contracts/api/client';
import { createLoopbackApiClient } from '../../../src/frontend/src/store/apiClient';

// ── Envelope + fetch fakes ─────────────────────────────────────────────────

/** Builds a successful envelope. */
export function ok<T>(data: T, pagination?: { offset: number; limit: number; total: number }): ApiEnvelope<T> {
  return pagination ? { data, pagination } : { data };
}

/** Recorded fetch call. */
export interface FetchCall {
  url: string;
  method: string;
  body: string | null;
}

/** Fake fetch routing by URL substring → responder. */
export function fakeFetch(routes: Record<string, (call: FetchCall) => unknown>) {
  const calls: FetchCall[] = [];
  const fetchLike = async (input: string, init?: RequestInit): Promise<Response> => {
    const call: FetchCall = { url: input, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : null };
    calls.push(call);
    for (const [fragment, responder] of Object.entries(routes)) {
      if (input.includes(fragment)) {
        return new Response(JSON.stringify(responder(call)), {
          status: 200,
          headers: { 'Content-Type': 'application/json' }
        });
      }
    }
    return new Response(JSON.stringify({ data: null, error: { code: 'not-found', message: `No fake route for ${input}` } }), {
      status: 404,
      headers: { 'Content-Type': 'application/json' }
    });
  };
  return { fetchLike, calls };
}

/** Typed API client over a fake fetch. */
export function fakeApiClient(routes: Record<string, (call: FetchCall) => unknown>): { api: LoopbackApiClient; calls: FetchCall[] } {
  const { fetchLike, calls } = fakeFetch(routes);
  const api = createLoopbackApiClient({
    baseUrl: 'http://127.0.0.1:5000',
    fetch: fetchLike,
    getSessionToken: () => 'test-session-token'
  });
  return { api, calls };
}

// ── Locked-DTO fixtures ────────────────────────────────────────────────────

/** Dashboard summary fixture (counts + health + additive WAF/throttle). */
export const summaryFixture: AuditDashboardSummary = {
  countsByStatus: { '2xx': 80, '3xx': 10, '4xx': 7, '5xx': 3 },
  countsBySeverity: { error: 4, warning: 9, notice: 12, pass: 75 },
  countsByCategory: { content: 10, crawl: 8, performance: 7 },
  crawlHealth: { totalUrls: 100, fetched: 90, errorCount: 3, blockedCount: 7, averageTtfbMs: 245.6 },
  waf: { totalBlocked: 5, blockRate: 0.055, byProvider: { cloudflare: 3, akamai: 2 }, byChallengeClass: { js: 4, captcha: 1 } },
  throttle: { throttledHostCount: 1, meanEffectiveRate: 4.2, adjustmentCount: 6 }
};

/** Category scores fixture. */
export const scoresFixture: AuditScores = {
  overall: 78.4,
  categories: [
    { category: 'content', score: 82.5, findingCount: 10 },
    { category: 'crawl', score: 71.0, findingCount: 8 },
    { category: 'performance', score: 90.2, findingCount: 2 }
  ]
};

/** Pre-bucketed chart points fixture. */
export const pointsFixture: readonly ChartPoint[] = [
  { label: 'audit-1', value: 61 },
  { label: 'audit-2', value: 70 },
  { label: 'audit-3', value: 78 }
];

/** Host throttle fixture with adjustment history. */
export const hostsFixture: readonly HostState[] = [
  {
    host: 'example.com',
    effectiveRate: 4,
    concurrency: 4,
    ttfbEwmaMs: 320,
    rollingBaselineRate: 8,
    throttled: true,
    override: null,
    history: [
      { timestamp: '2026-11-14T10:00:00Z', trigger: 'ttfb-spike', rateBefore: 8, rateAfter: 4, concurrencyBefore: 8, concurrencyAfter: 4, ttfbSampleMs: 900 },
      { timestamp: '2026-11-14T10:05:00Z', trigger: 'recovery', rateBefore: 4, rateAfter: 6, concurrencyBefore: 4, concurrencyAfter: 6, ttfbSampleMs: 280 }
    ]
  },
  {
    host: 'cdn.example.com',
    effectiveRate: 12,
    concurrency: 8,
    ttfbEwmaMs: 90,
    rollingBaselineRate: 12,
    throttled: false,
    override: { pinnedRate: null, pinnedConcurrency: null }
  }
];

/** WAF blocks summary fixture. */
export const wafFixture: WafBlocksSummary = {
  totalBlocked: 5,
  blockRate: 0.055,
  byProvider: { cloudflare: 3, akamai: 2 },
  byChallengeClass: { js: 4, captcha: 1 }
};

/** N-gram rows fixture (two zones). */
export const ngramRowsFixture: readonly NgramRow[] = [
  { ngram: 'running shoes', n: 2, zone: 'title', count: 40 },
  { ngram: 'trail running', n: 2, zone: 'title', count: 25 },
  { ngram: 'running shoes', n: 2, zone: 'body', count: 120 },
  { ngram: 'hiking boots', n: 2, zone: 'body', count: 60 }
];

/** Topical gaps fixture. */
export const gapsFixture: readonly TopicalGap[] = [
  { ngram: 'waterproof jackets', observedCount: 2, expectedCount: 20, gapScore: 0.9 },
  { ngram: 'trail running', observedCount: 15, expectedCount: 20, gapScore: 0.25 }
];

/** Log correlation summary fixture. */
export const logCorrelationFixture: LogCorrelationSummary = {
  orphanCount: 4,
  budgetWasteCount: 11,
  uncrawledIndexableCount: 6
};

/** llms.txt validation fixture (valid / invalid / missing). */
export const llmsFixture: readonly LlmsTxtValidation[] = [
  { host: 'example.com', url: 'https://example.com/llms.txt', present: true, valid: true, issues: [], checkedAt: '2026-11-14T10:00:00Z' },
  { host: 'shop.example.com', url: 'https://shop.example.com/llms.txt', present: true, valid: false, issues: ['missing-title'], checkedAt: '2026-11-14T10:00:00Z' },
  { host: 'blog.example.com', url: 'https://blog.example.com/llms.txt', present: false, valid: false, issues: ['not-found'], checkedAt: '2026-11-14T10:00:00Z' }
];

/** Bot governance fixture. */
export const governanceFixture: readonly BotGovernanceRow[] = [
  { botName: 'gptbot', host: 'example.com', allowed: true, robotsTxtAllowed: true, llmsTxtAllowed: true, notes: null },
  { botName: 'gptbot', host: 'shop.example.com', allowed: false, robotsTxtAllowed: false, llmsTxtAllowed: null, notes: 'disallowed in robots.txt' },
  { botName: 'claudebot', host: 'example.com', allowed: true, robotsTxtAllowed: true, llmsTxtAllowed: null, notes: null }
];

/** Container coverage fixture. */
export const coverageFixture: readonly ContainerCoverage[] = [
  { containerId: 'GTM-ABC123', containerName: 'Main container', indexablePages: 100, pagesWithContainer: 92, coverage: 0.92 },
  { containerId: 'AW-999', containerName: null, indexablePages: 100, pagesWithContainer: 40, coverage: 0.4 }
];

/** Consent coverage fixture. */
export const consentFixture: AnalyticsConsentSummary = {
  consentModeV2Present: true,
  beaconPresent: true,
  pagesWithConsent: 85,
  pagesWithoutConsent: 15
};

/** Bounded link-graph fixture (5 nodes, 5 edges; totals larger → truncated). */
export const graphDataFixture: GraphData = {
  nodes: [
    { id: 'u1', url: 'https://example.com/', label: 'Home', statusClass: '2xx', depth: 0, linkEquityPercentile: 99 },
    { id: 'u2', url: 'https://example.com/shoes', label: 'Shoes', statusClass: '2xx', depth: 1, linkEquityPercentile: 80 },
    { id: 'u3', url: 'https://example.com/boots', label: 'Boots', statusClass: '4xx', depth: 1, linkEquityPercentile: 40 },
    { id: 'u4', url: 'https://example.com/about', label: 'About', statusClass: '2xx', depth: 1, linkEquityPercentile: null },
    { id: 'u5', url: 'https://example.com/old', label: 'Old', statusClass: '3xx', depth: 2, linkEquityPercentile: 10 }
  ],
  edges: [
    { source: 'u1', target: 'u2', weight: 3 },
    { source: 'u1', target: 'u3', weight: 1 },
    { source: 'u2', target: 'u4', weight: null },
    { source: 'u3', target: 'u5', weight: 0.5 },
    { source: 'u5', target: 'u1', weight: 2 }
  ],
  totalNodeCount: 500_000,
  totalEdgeCount: 1_200_000,
  truncated: true
};

/** Semantic clusters fixture. */
export const clustersFixture: readonly SemanticCluster[] = [
  {
    clusterId: 'c1',
    label: 'Footwear',
    centroidUrlId: 'u2',
    members: [
      { urlId: 'u2', canonicalUrl: 'https://example.com/shoes', similarity: 1 },
      { urlId: 'u3', canonicalUrl: 'https://example.com/boots', similarity: 0.82 },
      { urlId: 'u5', canonicalUrl: 'https://example.com/old', similarity: 0.61 }
    ]
  },
  {
    clusterId: 'c2',
    label: 'Company',
    centroidUrlId: null,
    members: [{ urlId: 'u4', canonicalUrl: 'https://example.com/about', similarity: 0.9 }]
  }
];

/** Link opportunities fixture. */
export const opportunitiesFixture: readonly LinkOpportunity[] = [
  {
    sourceUrlId: 'u4',
    sourceCanonicalUrl: 'https://example.com/about',
    targetUrlId: 'u2',
    targetCanonicalUrl: 'https://example.com/shoes',
    similarity: 0.77,
    anchorSuggestion: 'our shoe range'
  }
];

// ── Mock chart renderer port ───────────────────────────────────────────────

/** One mock chart renderer instance with recorded lifecycle. */
export interface MockChartInstance extends ChartRenderer {
  readonly options: EChartsOption[];
  resizeCount: number;
  disposeCount: number;
  readonly clickHandlers: Set<(params: ChartClickParams) => void>;
  emitClick(params: ChartClickParams): void;
}

/** Mock chart factory tracking every created instance (leak assertions). */
export function createMockChartFactory() {
  const instances: MockChartInstance[] = [];
  const factory: ChartRendererFactory = (container) => {
    const instance: MockChartInstance = {
      options: [],
      resizeCount: 0,
      disposeCount: 0,
      clickHandlers: new Set(),
      setOption(option) {
        instance.options.push(option);
      },
      resize() {
        instance.resizeCount += 1;
      },
      getDataURL() {
        return 'data:image/png;base64,MOCKCHART';
      },
      on(_event, handler) {
        instance.clickHandlers.add(handler);
      },
      off(_event, handler) {
        instance.clickHandlers.delete(handler);
      },
      dispose() {
        instance.disposeCount += 1;
      },
      emitClick(params) {
        for (const handler of instance.clickHandlers) handler(params);
      }
    };
    void container;
    instances.push(instance);
    return instance;
  };
  return { factory, instances };
}

// ── Mock graph renderer port ───────────────────────────────────────────────

/** One mock graph renderer instance with recorded lifecycle. */
export interface MockGraphInstance extends GraphRendererPort {
  readonly graph: CrawlGraph;
  readonly container: HTMLElement;
  refreshCount: number;
  resizeCount: number;
  killCount: number;
  readonly settings: Record<string, unknown>;
  clickHandlers: Set<(nodeId: string) => void>;
  hoverHandlers: Set<(nodeId: string | null) => void>;
  emitClick(nodeId: string): void;
  emitHover(nodeId: string | null): void;
}

/** Mock graph factory tracking every created instance (leak assertions). */
export function createMockGraphFactory() {
  const instances: MockGraphInstance[] = [];
  const factory: GraphRendererFactory = (container, graph) => {
    const instance: MockGraphInstance = {
      graph,
      container,
      refreshCount: 0,
      resizeCount: 0,
      killCount: 0,
      settings: {},
      clickHandlers: new Set(),
      hoverHandlers: new Set(),
      refresh() {
        instance.refreshCount += 1;
      },
      resize() {
        instance.resizeCount += 1;
      },
      setSetting(key, value) {
        instance.settings[key] = value;
      },
      onNodeClick(handler) {
        instance.clickHandlers.add(handler);
        return () => instance.clickHandlers.delete(handler);
      },
      onNodeHover(handler) {
        instance.hoverHandlers.add(handler);
        return () => instance.hoverHandlers.delete(handler);
      },
      kill() {
        instance.killCount += 1;
      },
      emitClick(nodeId) {
        for (const handler of instance.clickHandlers) handler(nodeId);
      },
      emitHover(nodeId) {
        for (const handler of instance.hoverHandlers) handler(nodeId);
      }
    };
    instances.push(instance);
    return instance;
  };
  return { factory, instances };
}

// ── Fake layout worker port ────────────────────────────────────────────────

/** Fake worker behavior modes. */
export type FakeWorkerMode = 'compute' | 'hang' | 'error';

/** One fake layout worker with recorded lifecycle. */
export interface FakeLayoutWorker extends LayoutWorkerPort {
  readonly requests: LayoutRequest[];
  terminateCount: number;
  mode: FakeWorkerMode;
  onMessage?: (result: LayoutResult) => void;
  onError?: (error: unknown) => void;
}

/** Fake worker factory: computes via the pure core, hangs, or errors. */
export function createFakeWorkerFactory(mode: FakeWorkerMode = 'compute') {
  const workers: FakeLayoutWorker[] = [];
  const factory: LayoutWorkerFactory = () => {
    const worker: FakeLayoutWorker = {
      requests: [],
      terminateCount: 0,
      mode,
      postMessage(message) {
        worker.requests.push(message);
        if (worker.mode === 'hang') return;
        queueMicrotask(() => {
          if (worker.terminateCount > 0) return;
          if (worker.mode === 'error') {
            worker.onError?.(new Error('fake worker failure'));
            return;
          }
          worker.onMessage?.(computeLayout(message));
        });
      },
      terminate() {
        worker.terminateCount += 1;
      },
      setOnMessage(handler) {
        worker.onMessage = handler;
      },
      setOnError(handler) {
        worker.onError = handler;
      },
      onMessage: undefined,
      onError: undefined
    };
    workers.push(worker);
    return worker;
  };
  return { factory, workers };
}

// ── jsdom render harness (React 19 act + createRoot) ──────────────────────

/** Installs the browser APIs the surfaces need under jsdom. */
export function installDomStubs(): void {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  if (!globalThis.ResizeObserver) {
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  }
}

/** Mounted root handle. */
export interface Mounted {
  container: HTMLElement;
  unmount: () => void;
  rerender: (node: ReactNode) => void;
}

/** Renders a node into a fresh container under act. */
export async function render(node: ReactNode): Promise<Mounted> {
  installDomStubs();
  const container = document.createElement('div');
  document.body.appendChild(container);
  let root: Root | null = null;
  await act(async () => {
    root = createRoot(container);
    root.render(node);
  });
  return {
    container,
    unmount: () => {
      act(() => root?.unmount());
      container.remove();
    },
    rerender: (next: ReactNode) => {
      act(() => root?.render(next));
    }
  };
}

/** Flushes pending promises/timers under act. */
export async function flush(ms = 0): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

/** Query helper by data-testid. */
export function byTestId(container: HTMLElement, id: string): HTMLElement | null {
  return container.querySelector(`[data-testid="${id}"]`);
}

/** Click helper under act. */
export function click(element: Element): void {
  act(() => {
    element.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });
}

/** Change helper for controlled selects/checkboxes under act. */
export function changeValue(element: HTMLInputElement | HTMLSelectElement, value: string | boolean): void {
  act(() => {
    if (element instanceof HTMLInputElement && element.type === 'checkbox') {
      const checkedSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'checked')?.set;
      if (checkedSetter) checkedSetter.call(element, Boolean(value));
      else element.checked = Boolean(value);
      element.dispatchEvent(new Event('click', { bubbles: true }));
      return;
    }
    const proto = element instanceof HTMLInputElement ? HTMLInputElement.prototype : HTMLSelectElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(element, value);
    else element.value = String(value);
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  });
}
