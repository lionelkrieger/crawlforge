/**
 * WP-07-03 real-browser benchmark entry.
 *
 * Mounts the REAL product surfaces — ExplorerHost + the server-windowed grid
 * engine + the full shell service graph (typed loopback API client, SignalR
 * hub, normalized store) — against the REAL product Host process over a
 * same-origin proxy. No mocks: every row on screen came from the 500k seeded
 * SQLite database through the production API.
 *
 * The page exposes a small driver surface (window.__bench) consumed by the
 * puppeteer-core harness: explorer switching, the rAF scroll sweep, and the
 * graph-layout Web Worker run.
 *
 * @module tests/frontend/grid/bench/entry/bench-main
 */

import { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { DirectedGraph } from 'graphology';
import '@mantine/core/styles.css';
import {
  ShellProvider,
  createShellServices,
  useShellLifecycle,
  type ShellServices
} from '../../../../../src/frontend/src/shell/ShellContext';
import { ExplorerHost } from '../../../../../src/frontend/src/surfaces/explorers/ExplorerHost';
import {
  urlExplorer,
  issueExplorer,
  linkExplorer,
  extractionExplorer,
  ngramExplorer
} from '../../../../../src/frontend/src/surfaces/explorers/coreExplorers';
import type { ExplorerDefinition } from '../../../../../src/frontend/src/surfaces/explorers/explorerTypes';
import type { RowData } from '@tanstack/react-table';
import { runLayout, createViteLayoutWorkerFactory } from '../../../../../src/frontend/src/graphs/layoutClient';
import type { CrawlGraph } from '../../../../../src/frontend/src/graphs/graphModel';

/** Harness-injected configuration (same-origin proxy origin + session). */
interface BenchConfig {
  readonly backendBaseUrl: string;
  readonly sessionToken: string;
  readonly auditId: string;
}

declare global {
  interface Window {
    __BENCH_CONFIG__?: BenchConfig;
    __bench?: BenchDriver;
  }
}

/** Driver surface consumed by the puppeteer harness. */
interface BenchDriver {
  ready(): boolean;
  setExplorer(id: ExplorerId): void;
  gridInfo(): GridInfo;
  gridDiag(): GridDiag;
  runSweep(options: SweepOptions): Promise<SweepResult>;
  runWorkerLayout(options: WorkerLayoutOptions): Promise<WorkerLayoutResult>;
}

type ExplorerId = 'urls' | 'issues' | 'links' | 'extraction' | 'ngrams';

interface GridInfo {
  totalRowsText: string;
  domRows: number;
  scrollHeight: number;
  clientHeight: number;
}

/** Failure-diagnostics snapshot of the mounted grid. */
interface GridDiag {
  totalRowsText: string;
  domRows: number;
  errorText: string | null;
  explorerTestId: string | null;
  bodyTextSample: string;
}

interface SweepOptions {
  segments: number;
  stepsPerSegment: number;
  stepPx: number;
}

interface SweepResult {
  frameDeltasMs: number[];
  idleFrameDeltasMs: number[];
  domRowSamples: number[];
  maxDomRows: number;
  scrollHeight: number;
  steps: number;
}

interface WorkerLayoutOptions {
  nodeCount: number;
  edgeCount: number;
  iterations: number;
}

interface WorkerLayoutResult {
  elapsedMs: number;
  positionCount: number;
  iterations: number;
}

// Single documented variance erasure: the runtime rows ARE the definition's
// row type; the host consumes the open RowData surface (WP-06-03 pattern).
const EXPLORERS: Record<ExplorerId, ExplorerDefinition<RowData>> = {
  urls: urlExplorer as unknown as ExplorerDefinition<RowData>,
  issues: issueExplorer as unknown as ExplorerDefinition<RowData>,
  links: linkExplorer as unknown as ExplorerDefinition<RowData>,
  extraction: extractionExplorer as unknown as ExplorerDefinition<RowData>,
  ngrams: ngramExplorer as unknown as ExplorerDefinition<RowData>
};

const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

const countDomRows = () => document.querySelectorAll('[data-row-index]').length;

/**
 * rAF scroll sweep: jumps across the full virtual scroll range in segments,
 * then smooth-scrolls each segment one viewport-step per animation frame.
 * Every frame delta is recorded; DOM row counts are sampled throughout.
 */
async function runSweep(options: SweepOptions): Promise<SweepResult> {
  const element = document.querySelector<HTMLElement>('[data-testid="cf-grid-body"]');
  if (!element) throw new Error('Grid body not mounted.');

  // Idle baseline: ~90 vsync-aligned rAF intervals with NO scrolling and a
  // static grid. Isolates the headless frame-clock's own jitter from any
  // scroll/render load — if idle p95 ≈ scroll p95, the spread is the clock,
  // not per-frame rendering work.
  const idleFrameDeltasMs: number[] = [];
  let idleLast = -1;
  for (let i = 0; i < 90; i++) {
    const ts = await new Promise<number>((resolve) => requestAnimationFrame(resolve));
    if (idleLast >= 0) idleFrameDeltasMs.push(ts - idleLast);
    idleLast = ts;
  }

  const frameDeltasMs: number[] = [];
  const domRowSamples: number[] = [];
  let running = true;
  let last = performance.now();
  const loop = (now: number) => {
    if (!running) return;
    frameDeltasMs.push(now - last);
    last = now;
    requestAnimationFrame(loop);
  };
  requestAnimationFrame((start) => {
    last = start;
    requestAnimationFrame(loop);
  });

  const maxScroll = Math.max(0, element.scrollHeight - element.clientHeight);
  let steps = 0;
  for (let segment = 0; segment < options.segments; segment++) {
    element.scrollTop = Math.floor((maxScroll * segment) / Math.max(1, options.segments - 1));
    await nextFrame();
    await nextFrame();
    domRowSamples.push(countDomRows());
    for (let i = 0; i < options.stepsPerSegment; i++) {
      element.scrollTop = Math.min(maxScroll, element.scrollTop + options.stepPx);
      await nextFrame();
      steps++;
      if (i % 10 === 0) domRowSamples.push(countDomRows());
    }
  }

  running = false;
  return {
    frameDeltasMs: frameDeltasMs.slice(1),
    idleFrameDeltasMs,
    domRowSamples,
    maxDomRows: domRowSamples.length > 0 ? Math.max(...domRowSamples) : 0,
    scrollHeight: element.scrollHeight,
    steps
  };
}

/**
 * Production graph-layout path: a deterministic large graph laid out by the
 * real ForceAtlas2 Web Worker (Vite module worker). The harness samples the
 * worker's own JS heap via CDP while this runs and verifies termination.
 */
async function runWorkerLayout(options: WorkerLayoutOptions): Promise<WorkerLayoutResult> {
  let state = 0x2f6e2b1;
  const random = () => {
    state = (state * 1103515245 + 12345) & 0x7fffffff;
    return state / 0x7fffffff;
  };

  // Untyped graphology graph: the layout path only reads x/y/weight; the
  // full render attribute set is irrelevant to the worker layout contract.
  const graph = new DirectedGraph();
  for (let i = 0; i < options.nodeCount; i++) {
    graph.addNode(`n${i}`, { x: random() * 100, y: random() * 100 });
  }
  for (let i = 0; i < options.edgeCount; i++) {
    const source = `n${Math.floor(random() * options.nodeCount)}`;
    const target = `n${Math.floor(random() * options.nodeCount)}`;
    if (source !== target && !graph.hasEdge(source, target)) {
      graph.addEdge(source, target, { weight: 1 + random() * 4 });
    }
  }

  const factory = createViteLayoutWorkerFactory();
  const start = performance.now();
  const result = await runLayout(graph as unknown as CrawlGraph, factory, {
    iterations: options.iterations,
    weighted: true,
    timeoutMs: 120_000
  });
  return {
    elapsedMs: performance.now() - start,
    positionCount: Object.keys(result.positions).length,
    iterations: result.iterations
  };
}

/** Inner frame: real shell lifecycle (hub + bootstrap loads) + explorer. */
function BenchFrame({ services }: { services: ShellServices }) {
  useShellLifecycle(services);
  const [explorerId, setExplorerId] = useState<ExplorerId>('urls');
  // Every setExplorer forces a FRESH mount (even for the same id) so each
  // benchmark test starts from a clean engine + fetch state.
  const [mountKey, setMountKey] = useState(0);

  useEffect(() => {
    const driver: BenchDriver = {
      ready: () => true,
      setExplorer: (id) => {
        setExplorerId(id);
        setMountKey((key) => key + 1);
      },
      gridInfo: () => {
        const element = document.querySelector<HTMLElement>('[data-testid="cf-grid-body"]');
        const total = document.querySelector('[data-testid="cf-grid-total"]');
        return {
          totalRowsText: total?.textContent ?? '',
          domRows: countDomRows(),
          scrollHeight: element?.scrollHeight ?? 0,
          clientHeight: element?.clientHeight ?? 0
        };
      },
      gridDiag: () => {
        const total = document.querySelector('[data-testid="cf-grid-total"]');
        const error = document.querySelector('[data-testid="cf-grid-error"]');
        const explorer = document.querySelector('[data-testid^="cf-explorer-"]');
        const body = document.querySelector('[data-testid="cf-grid-body"]');
        return {
          totalRowsText: total?.textContent ?? '',
          domRows: countDomRows(),
          errorText: error?.textContent ?? null,
          explorerTestId: explorer?.getAttribute('data-testid') ?? null,
          bodyTextSample: (body?.textContent ?? '').slice(0, 400)
        };
      },
      runSweep,
      runWorkerLayout
    };
    window.__bench = driver;
    return () => {
      if (window.__bench === driver) delete window.__bench;
    };
  }, []);

  const config = window.__BENCH_CONFIG__;
  return (
    <div style={{ padding: 16 }}>
      <ExplorerHost
        key={`${explorerId}:${mountKey}`}
        definition={EXPLORERS[explorerId]}
        auditId={config?.auditId ?? null}
      />
    </div>
  );
}

/** Root: real service graph bound to the proxied loopback backend. */
function BenchApp() {
  const config = window.__BENCH_CONFIG__;
  const services = useMemo(
    () =>
      createShellServices({
        baseUrl: config?.backendBaseUrl ?? null,
        getSessionToken: () => config?.sessionToken ?? ''
      }),
    [config]
  );
  return (
    <ShellProvider services={services}>
      <BenchFrame services={services} />
    </ShellProvider>
  );
}

createRoot(document.getElementById('root')!).render(<BenchApp />);
