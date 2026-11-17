/**
 * Bounded level-of-detail sampling for large graphs (FS-CRAWL-002 §8.5:
 * 500k+ URL scale never renders at once). Deterministic top-K selection by
 * link-equity percentile, edge capping by weight, the honesty banner
 * ("showing X of Y"), and drill-down neighborhood extraction. Pure data —
 * no DOM, no renderer.
 *
 * @module graphs/graphSampling
 */

import type { GraphData, GraphEdge, GraphNode } from '../contracts/api/audits';

/** LOD render budget (nodes/edges). Defaults keep sigma responsive. */
export interface LodBudget {
  readonly maxNodes: number;
  readonly maxEdges: number;
}

/** Default LOD budget for the visualization surface. */
export const DEFAULT_LOD_BUDGET: LodBudget = { maxNodes: 20_000, maxEdges: 60_000 };

/** A sampled, render-ready graph plus honesty metadata. */
export interface SampledGraphData {
  readonly nodes: readonly GraphNode[];
  readonly edges: readonly GraphEdge[];
  readonly totalNodeCount: number;
  readonly totalEdgeCount: number;
  readonly truncated: boolean;
  /** "Showing X of Y" banner; null when the full bounded graph fits. */
  readonly banner: string | null;
}

/** Node rank: equity percentile descending (null last), id tie-break. */
function nodeRank(node: GraphNode): number {
  return node.linkEquityPercentile ?? -1;
}

/**
 * Deterministically samples a bounded graph payload to the LOD budget.
 * Selection is stable across renders: same input → same output.
 */
export function sampleGraphData(data: GraphData, budget: LodBudget = DEFAULT_LOD_BUDGET): SampledGraphData {
  const rankedNodes = [...data.nodes].sort(
    (a, b) => nodeRank(b) - nodeRank(a) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  );
  const keptNodes = rankedNodes.slice(0, Math.max(0, budget.maxNodes));
  const keptIds = new Set(keptNodes.map((node) => node.id));

  const candidateEdges = data.edges.filter(
    (edge) => keptIds.has(edge.source) && keptIds.has(edge.target)
  );
  const rankedEdges = [...candidateEdges].sort(
    (a, b) =>
      (b.weight ?? 0) - (a.weight ?? 0) ||
      (a.source < b.source ? -1 : a.source > b.source ? 1 : 0) ||
      (a.target < b.target ? -1 : a.target > b.target ? 1 : 0)
  );
  const keptEdges = rankedEdges.slice(0, Math.max(0, budget.maxEdges));

  const truncated =
    data.truncated ||
    keptNodes.length < data.totalNodeCount ||
    keptEdges.length < data.totalEdgeCount;

  const banner = truncated
    ? `Showing ${keptNodes.length.toLocaleString('en-US')} of ${data.totalNodeCount.toLocaleString('en-US')} nodes · ` +
      `${keptEdges.length.toLocaleString('en-US')} of ${data.totalEdgeCount.toLocaleString('en-US')} edges — drill down or filter to explore more`
    : null;

  return {
    nodes: keptNodes,
    edges: keptEdges,
    totalNodeCount: data.totalNodeCount,
    totalEdgeCount: data.totalEdgeCount,
    truncated,
    banner
  };
}

/**
 * Drill-down: extracts the induced neighborhood of a focus node (undirected
 * adjacency, bounded radius) from a sampled graph. The result is a local
 * view — totals still reference the full graph for the honesty banner.
 */
export function neighborhoodSubgraph(
  data: SampledGraphData,
  focusId: string,
  radius = 1
): SampledGraphData {
  const adjacency = new Map<string, Set<string>>();
  for (const edge of data.edges) {
    (adjacency.get(edge.source) ?? adjacency.set(edge.source, new Set()).get(edge.source)!).add(edge.target);
    (adjacency.get(edge.target) ?? adjacency.set(edge.target, new Set()).get(edge.target)!).add(edge.source);
  }

  const visited = new Set<string>([focusId]);
  let frontier = new Set<string>([focusId]);
  for (let depth = 0; depth < Math.max(0, radius); depth++) {
    const next = new Set<string>();
    for (const id of frontier) {
      for (const neighbor of adjacency.get(id) ?? []) {
        if (!visited.has(neighbor)) {
          visited.add(neighbor);
          next.add(neighbor);
        }
      }
    }
    frontier = next;
  }

  const nodes = data.nodes.filter((node) => visited.has(node.id));
  const edges = data.edges.filter((edge) => visited.has(edge.source) && visited.has(edge.target));
  return {
    nodes,
    edges,
    totalNodeCount: data.totalNodeCount,
    totalEdgeCount: data.totalEdgeCount,
    truncated: nodes.length < data.totalNodeCount,
    banner: `Neighborhood of ${focusId} · ${nodes.length} nodes · ${edges.length} edges (of ${data.totalNodeCount.toLocaleString('en-US')} total)`
  };
}
