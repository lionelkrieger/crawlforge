/**
 * ForceAtlas2 layout Web Worker entry (FS-CRAWL-001 §8.5). Thin message
 * wrapper around the pure layout core — all computation lives in
 * layoutCore.ts so it is unit-testable without a worker runtime. Vite
 * bundles this module via `new Worker(new URL(..., import.meta.url))`.
 *
 * @module graphs/layoutWorker
 */

import { computeLayout, type LayoutRequest, type LayoutResult } from './layoutCore';

/** Minimal dedicated-worker scope (lib.webworker types are not in tsconfig). */
interface LayoutWorkerScope {
  onmessage: ((event: MessageEvent<LayoutRequest>) => void) | null;
  postMessage(message: LayoutResult): void;
}

const scope = self as unknown as LayoutWorkerScope;

scope.onmessage = (event: MessageEvent<LayoutRequest>): void => {
  const result: LayoutResult = computeLayout(event.data);
  scope.postMessage(result);
};
