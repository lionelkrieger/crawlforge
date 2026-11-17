/**
 * Pure ForceAtlas2 layout core (FS-CRAWL-001 §8.5: layout runs off the
 * render thread). This module is worker-safe AND node-testable: no DOM, no
 * worker globals — the worker entry (layoutWorker.ts) is a thin message
 * wrapper around computeLayout. Deterministic circular seeding makes
 * layouts reproducible for identical inputs.
 *
 * @module graphs/layoutCore
 */

import { DirectedGraph } from 'graphology';
import forceAtlas2, { inferSettings } from 'graphology-layout-forceatlas2';

/** One node in a layout request (optional seed position). */
export interface LayoutNodeInput {
  readonly id: string;
  readonly x?: number;
  readonly y?: number;
}

/** One edge in a layout request. */
export interface LayoutEdgeInput {
  readonly source: string;
  readonly target: string;
  readonly weight?: number;
}

/** Serialized layout request (structured-clone safe). */
export interface LayoutRequest {
  readonly nodes: readonly LayoutNodeInput[];
  readonly edges: readonly LayoutEdgeInput[];
  readonly iterations?: number;
  /** When true, edge weights influence attraction (weighted equity mode). */
  readonly weighted?: boolean;
}

/** Layout result: final node positions. */
export interface LayoutResult {
  readonly positions: Readonly<Record<string, { x: number; y: number }>>;
  readonly iterations: number;
}

/** Default iteration count (bounded; the worker keeps the UI responsive). */
export const DEFAULT_LAYOUT_ITERATIONS = 200;

/** Barnes-Hut kicks in above this node count (large-graph performance). */
export const BARNES_HUT_THRESHOLD = 1_000;

/** Computes a ForceAtlas2 layout synchronously (worker or fallback path). */
export function computeLayout(request: LayoutRequest): LayoutResult {
  const iterations = Math.max(1, request.iterations ?? DEFAULT_LAYOUT_ITERATIONS);
  const graph = new DirectedGraph();

  request.nodes.forEach((node, index) => {
    if (graph.hasNode(node.id)) return;
    const angle = (2 * Math.PI * index) / Math.max(1, request.nodes.length);
    const radius = 10 + Math.sqrt(Math.max(1, request.nodes.length));
    graph.addNode(node.id, {
      x: node.x ?? Math.cos(angle) * radius,
      y: node.y ?? Math.sin(angle) * radius
    });
  });

  for (const edge of request.edges) {
    if (!graph.hasNode(edge.source) || !graph.hasNode(edge.target)) continue;
    if (graph.hasEdge(edge.source, edge.target)) continue;
    graph.addEdge(edge.source, edge.target, { weight: edge.weight ?? 1 });
  }

  if (graph.order === 0) {
    return { positions: {}, iterations };
  }

  const settings = inferSettings(graph);
  settings.adjustSizes = true;
  settings.barnesHutOptimize = graph.order > BARNES_HUT_THRESHOLD;
  settings.edgeWeightInfluence = request.weighted ? 1 : 0;

  forceAtlas2.assign(graph, {
    iterations,
    settings,
    getEdgeWeight: request.weighted ? 'weight' : null
  });

  const positions: Record<string, { x: number; y: number }> = {};
  graph.forEachNode((nodeId, attributes) => {
    positions[nodeId] = {
      x: typeof attributes.x === 'number' ? attributes.x : 0,
      y: typeof attributes.y === 'number' ? attributes.y : 0
    };
  });
  return { positions, iterations };
}
