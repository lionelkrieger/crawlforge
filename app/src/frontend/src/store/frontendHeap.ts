/**
 * Renderer JS heap measurement (FS-CRAWL-002 §7.2). The frontend heap is
 * composed client-side from the performance-memory API where available and
 * is NEVER sent to the backend. Null when the API is unavailable.
 *
 * @module store/frontendHeap
 */

/** Chromium performance.memory shape (non-standard; feature-detected). */
interface PerformanceMemory {
  readonly usedJSHeapSize: number;
  readonly totalJSHeapSize: number;
  readonly jsHeapSizeLimit: number;
}

/** Reads the renderer's used JS heap in bytes, or null when unavailable. */
export function measureFrontendJsHeap(): number | null {
  const perf = globalThis.performance as (Performance & { memory?: PerformanceMemory }) | undefined;
  const used = perf?.memory?.usedJSHeapSize;
  return typeof used === 'number' && Number.isFinite(used) && used >= 0 ? used : null;
}
