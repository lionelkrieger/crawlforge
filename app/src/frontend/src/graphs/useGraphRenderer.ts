/**
 * Shared graph rendering lifecycle hook: worker layout → position apply →
 * renderer mount → strict teardown. One effect owns the whole lifecycle;
 * cancellation guards prevent post-unmount mounts, and cleanup kills the
 * renderer, disconnects the resize observer, and removes every listener
 * (§7.4: no leaked canvases, WebGL contexts, workers, or listeners).
 *
 * @module graphs/useGraphRenderer
 */

import { useEffect, useRef, useState, type RefObject } from 'react';
import type { CrawlGraph } from './graphModel';
import { applyPositions } from './graphModel';
import type { GraphRendererFactory, GraphRendererPort } from './graphPorts';
import {
  runLayout,
  runLayoutSync,
  createViteLayoutWorkerFactory,
  type LayoutWorkerFactory
} from './layoutClient';

/** Layout lifecycle states surfaced to the UI. */
export type GraphLayoutState = 'running' | 'done' | 'fallback';

/** Hook options. */
export interface UseGraphRendererOptions {
  readonly rendererFactory?: GraphRendererFactory;
  /** null forces the synchronous fallback (tests/offline). */
  readonly workerFactory?: LayoutWorkerFactory | null;
  readonly weighted?: boolean;
  /** ForceAtlas2 iteration budget (live control; re-runs layout on change). */
  readonly iterations?: number;
  /** Renderer settings applied live (e.g. labelRenderedSizeThreshold). */
  readonly settings?: Readonly<Record<string, unknown>>;
  readonly onNodeClick?: (nodeId: string) => void;
  readonly onNodeHover?: (nodeId: string | null) => void;
}

/** Hook result: container ref + layout state. */
export interface UseGraphRendererResult {
  readonly containerRef: RefObject<HTMLDivElement | null>;
  readonly layoutState: GraphLayoutState;
}

let defaultRendererFactoryPromise: Promise<GraphRendererFactory> | null = null;
let defaultWorkerFactory: LayoutWorkerFactory | null = null;

/**
 * Lazily loads the real sigma adapter. The dynamic import keeps sigma's
 * WebGL-touching module init out of every static import chain — jsdom
 * suites inject mock factories and never evaluate it.
 */
function getDefaultRendererFactory(): Promise<GraphRendererFactory> {
  defaultRendererFactoryPromise ??= import('./sigmaRenderer').then((module) => module.createSigmaRendererFactory());
  return defaultRendererFactoryPromise;
}

/** Shared graph lifecycle (layout in worker, mount behind port, dispose). */
export function useGraphRenderer(
  graph: CrawlGraph,
  options: UseGraphRendererOptions
): UseGraphRendererResult {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<GraphRendererPort | null>(null);
  const [layoutState, setLayoutState] = useState<GraphLayoutState>('running');
  const clickRef = useRef(options.onNodeClick);
  clickRef.current = options.onNodeClick;
  const hoverRef = useRef(options.onNodeHover);
  hoverRef.current = options.onNodeHover;

  const { rendererFactory, workerFactory, weighted, iterations, settings } = options;
  const settingsRef = useRef(settings);
  settingsRef.current = settings;

  // Live renderer settings (applied after mount and on every change —
  // never remounts the graph).
  useEffect(() => {
    const renderer = rendererRef.current;
    if (!renderer || !settings) return;
    for (const [key, value] of Object.entries(settings)) renderer.setSetting(key, value);
  }, [settings]);

  useEffect(() => {
    let cancelled = false;
    let renderer: GraphRendererPort | null = null;
    let observer: ResizeObserver | null = null;
    const disposers: (() => void)[] = [];
    const container = containerRef.current;

    async function start(): Promise<void> {
      setLayoutState('running');
      const activeWorkerFactory =
        workerFactory === null ? null : workerFactory ?? (defaultWorkerFactory ??= createViteLayoutWorkerFactory());

      let usedFallback = false;
      let positions;
      try {
        const result = await runLayout(graph, activeWorkerFactory, { weighted, iterations });
        positions = result.positions;
      } catch {
        // Hung/failed worker → bounded synchronous fallback (graceful degrade).
        positions = runLayoutSync(graph, { weighted, iterations: iterations ?? 80 }).positions;
        usedFallback = true;
      }
      if (cancelled) return;

      applyPositions(graph, positions);
      setLayoutState(usedFallback ? 'fallback' : 'done');
      if (!container) return;

      let activeRendererFactory: GraphRendererFactory;
      if (rendererFactory) {
        activeRendererFactory = rendererFactory;
      } else {
        try {
          activeRendererFactory = await getDefaultRendererFactory();
        } catch {
          // WebGL adapter unavailable → keep the laid-out graph data; the
          // surface degrades gracefully instead of breaking (§7.4).
          return;
        }
      }
      if (cancelled) return;

      let mounted: GraphRendererPort;
      try {
        mounted = activeRendererFactory(container, graph);
      } catch {
        // WebGL context creation failed → graceful no-renderer degrade.
        return;
      }
      renderer = mounted;
      rendererRef.current = mounted;
      const initialSettings = settingsRef.current;
      if (initialSettings) {
        for (const [key, value] of Object.entries(initialSettings)) mounted.setSetting(key, value);
      }
      disposers.push(
        mounted.onNodeClick((nodeId) => clickRef.current?.(nodeId)),
        mounted.onNodeHover((nodeId) => hoverRef.current?.(nodeId))
      );
      if (typeof ResizeObserver !== 'undefined') {
        // Guard against callbacks queued before disconnect: a killed
        // renderer must never receive a resize (§7.4).
        observer = new ResizeObserver(() => {
          if (renderer === mounted) mounted.resize();
        });
        observer.observe(container);
      }
    }

    void start();

    return () => {
      cancelled = true;
      observer?.disconnect();
      for (const dispose of disposers) dispose();
      renderer?.kill();
      renderer = null;
      rendererRef.current = null;
    };
  }, [graph, rendererFactory, workerFactory, weighted, iterations]);

  return { containerRef, layoutState };
}
