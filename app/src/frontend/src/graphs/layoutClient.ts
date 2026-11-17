/**
 * Layout worker client behind an injectable factory port. The component
 * layer never touches `Worker` directly: jsdom suites inject a fake worker,
 * and environments without Worker support fall back to the bounded
 * synchronous core. One-shot lifecycle per layout run — the worker is
 * terminated in `finally`, so no worker ever leaks (§7.4).
 *
 * @module graphs/layoutClient
 */

import type { CrawlGraph } from './graphModel';
import { computeLayout, type LayoutRequest, type LayoutResult } from './layoutCore';

/** Minimal worker surface consumed by the layout client. */
export interface LayoutWorkerPort {
  postMessage(message: LayoutRequest): void;
  terminate(): void;
  setOnMessage(handler: (result: LayoutResult) => void): void;
  setOnError(handler: (error: unknown) => void): void;
}

/** Factory port for layout workers (tests inject fakes). */
export type LayoutWorkerFactory = () => LayoutWorkerPort;

/** Layout run options. */
export interface LayoutRunOptions {
  readonly iterations?: number;
  readonly weighted?: boolean;
  /** Reject a hung worker after this many ms (default 30 s). */
  readonly timeoutMs?: number;
}

/** Default hung-worker timeout. */
export const DEFAULT_LAYOUT_TIMEOUT_MS = 30_000;

/** Production worker factory (Vite module worker bundling). */
export function createViteLayoutWorkerFactory(): LayoutWorkerFactory {
  return () => {
    const worker = new Worker(new URL('./layoutWorker.ts', import.meta.url), { type: 'module' });
    return {
      postMessage: (message) => worker.postMessage(message),
      terminate: () => worker.terminate(),
      setOnMessage: (handler) => {
        worker.onmessage = (event: MessageEvent<LayoutResult>) => handler(event.data);
      },
      setOnError: (handler) => {
        worker.onerror = (event) => handler(event);
      }
    };
  };
}

/** Serializes a graph model into a structured-clone-safe layout request. */
export function graphToLayoutRequest(graph: CrawlGraph, options: LayoutRunOptions = {}): LayoutRequest {
  const nodes = graph.mapNodes((nodeId, attributes) => ({
    id: nodeId,
    x: typeof attributes.x === 'number' ? attributes.x : undefined,
    y: typeof attributes.y === 'number' ? attributes.y : undefined
  }));
  const edges = graph.mapEdges((_edgeId, attributes, source, target) => ({
    source,
    target,
    weight: typeof attributes.weight === 'number' ? attributes.weight : 1
  }));
  return { nodes, edges, iterations: options.iterations, weighted: options.weighted };
}

/**
 * Runs one layout pass. With a factory: worker round-trip with timeout and
 * guaranteed termination (a factory that cannot construct a worker rejects,
 * and callers fall back to sync). Without: bounded synchronous fallback
 * (the pure core). Rejects on worker error/timeout.
 */
export function runLayout(
  graph: CrawlGraph,
  factory: LayoutWorkerFactory | null,
  options: LayoutRunOptions = {}
): Promise<LayoutResult> {
  const request = graphToLayoutRequest(graph, options);
  if (!factory) {
    return Promise.resolve(computeLayout(request));
  }

  return new Promise<LayoutResult>((resolve, reject) => {
    const worker = factory();
    const timeoutMs = options.timeoutMs ?? DEFAULT_LAYOUT_TIMEOUT_MS;
    const timer = setTimeout(() => {
      worker.terminate();
      reject(new Error('Layout worker timed out'));
    }, timeoutMs);

    worker.setOnMessage((result) => {
      clearTimeout(timer);
      worker.terminate();
      resolve(result);
    });
    worker.setOnError((error) => {
      clearTimeout(timer);
      worker.terminate();
      reject(error instanceof Error ? error : new Error('Layout worker failed'));
    });
    worker.postMessage(request);
  });
}

/** Synchronous fallback export (bounded core; used when workers fail). */
export function runLayoutSync(graph: CrawlGraph, options: LayoutRunOptions = {}): LayoutResult {
  return computeLayout(graphToLayoutRequest(graph, options));
}
