/**
 * Pure graph-model transforms (FS-CRAWL-001 §8.5, FS-CRAWL-002 §8.5/§12.9):
 * bounded graph payloads → graphology models for sigma. Node size encodes
 * internal link-equity percentile; color encodes status class or crawl
 * depth; weighted mode honors dom_zone equity weights (the server's
 * equityWeight carries the zone weight; unweighted mode counts every link
 * as 1). Semantic cluster + opportunity overlays are additive edge kinds.
 * No DOM, no renderer imports.
 *
 * @module graphs/graphModel
 */

import { DirectedGraph } from 'graphology';
import type { GraphEdge, GraphNode, LinkEdge } from '../contracts/api/audits';
import type { LinkOpportunity, SemanticCluster } from '../contracts/api/semantics';
import { categoryColor, depthColor, statusColors } from '../charts/chartTheme';

/** Node attributes carried into sigma (x/y/size/color/label are sigma's). */
export interface GraphNodeAttributes {
  x: number;
  y: number;
  size: number;
  color: string;
  label: string;
  url: string;
  depth: number;
  statusClass: string | null;
  equityPercentile: number | null;
  clusterId: string | null;
  kind: 'page';
  [key: string]: unknown;
}

/** Edge attributes carried into sigma. */
export interface GraphEdgeAttributes {
  weight: number;
  size: number;
  color: string;
  kind: 'link' | 'similarity' | 'opportunity';
  type?: string;
  [key: string]: unknown;
}

/** The graph model type used across the graphs area. */
export type CrawlGraph = DirectedGraph<GraphNodeAttributes, GraphEdgeAttributes>;

/** Link-graph rendering options. */
export interface LinkGraphOptions {
  /** Weighted equity (dom_zone weights) vs unweighted link counts. */
  readonly weighted: boolean;
  readonly colorMode: 'status' | 'depth';
}

/** Equity weight for an explorer link edge (zone-weighted vs unweighted). */
export function linkEdgeWeight(edge: LinkEdge, weighted: boolean): number {
  if (!weighted) return 1;
  return edge.equityWeight ?? 1;
}

/** Node size from equity percentile (3..12 px radius). */
export function nodeSizeFromPercentile(percentile: number | null): number {
  const clamped = Math.max(0, Math.min(100, percentile ?? 0));
  return 3 + (9 * clamped) / 100;
}

/** Short display label from a URL (path tail, host for roots). */
export function urlLabel(url: string): string {
  try {
    const parsed = new URL(url);
    const path = parsed.pathname.replace(/\/+$/, '');
    if (path === '') return parsed.host;
    const segments = path.split('/').filter((segment) => segment.length > 0);
    return segments[segments.length - 1] ?? parsed.host;
  } catch {
    return url;
  }
}

/** Deterministic circular seed positions (layout worker refines them). */
function seedPosition(index: number, total: number): { x: number; y: number } {
  const angle = (2 * Math.PI * index) / Math.max(1, total);
  const radius = 10 + Math.sqrt(Math.max(1, total));
  return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
}

function nodeColor(node: GraphNode, colorMode: 'status' | 'depth'): string {
  if (colorMode === 'depth') return depthColor(node.depth);
  return (node.statusClass ? statusColors[node.statusClass] : undefined) ?? '#8b9ab3';
}

/** Builds the internal link-network graph from a sampled payload. */
export function buildLinkGraph(
  data: { readonly nodes: readonly GraphNode[]; readonly edges: readonly GraphEdge[] },
  options: LinkGraphOptions
): CrawlGraph {
  const graph: CrawlGraph = new DirectedGraph();
  data.nodes.forEach((node, index) => {
    const seed = seedPosition(index, data.nodes.length);
    graph.addNode(node.id, {
      x: seed.x,
      y: seed.y,
      size: nodeSizeFromPercentile(node.linkEquityPercentile),
      color: nodeColor(node, options.colorMode),
      label: urlLabel(node.url),
      url: node.url,
      depth: node.depth,
      statusClass: node.statusClass,
      equityPercentile: node.linkEquityPercentile,
      clusterId: null,
      kind: 'page'
    });
  });

  for (const edge of data.edges) {
    if (!graph.hasNode(edge.source) || !graph.hasNode(edge.target)) continue;
    if (graph.hasEdge(edge.source, edge.target)) continue;
    const weight = options.weighted ? edge.weight ?? 1 : 1;
    graph.addEdge(edge.source, edge.target, {
      weight,
      size: options.weighted ? Math.min(4, 0.5 + weight) : 0.6,
      color: '#3d4a63',
      kind: 'link'
    });
  }
  return graph;
}

/** Builds the link graph from explorer link edges (zone-weighted equity). */
export function buildLinkGraphFromExplorer(
  nodes: readonly GraphNode[],
  edges: readonly LinkEdge[],
  options: LinkGraphOptions
): CrawlGraph {
  const mapped: GraphEdge[] = edges.map((edge) => ({
    source: edge.sourceUrlId,
    target: edge.targetUrlId,
    weight: linkEdgeWeight(edge, options.weighted)
  }));
  return buildLinkGraph({ nodes, edges: mapped }, options);
}

/** Semantic-graph options. */
export interface SemanticGraphOptions {
  /** Overlay recommended links (violet dashed edges). */
  readonly showOpportunities: boolean;
  /** Node budget (deterministic cap). */
  readonly maxNodes?: number;
}

/** Default semantic node budget. */
export const DEFAULT_SEMANTIC_MAX_NODES = 5_000;

/**
 * Builds the semantic similarity network: cluster members colored by
 * cluster, similarity edges to the centroid, and optional opportunity
 * overlay edges. Deterministic ordering (clusterId, urlId).
 */
export function buildSemanticGraph(
  clusters: readonly SemanticCluster[],
  opportunities: readonly LinkOpportunity[],
  options: SemanticGraphOptions
): CrawlGraph {
  const graph: CrawlGraph = new DirectedGraph();
  const maxNodes = options.maxNodes ?? DEFAULT_SEMANTIC_MAX_NODES;
  const clusterColor = new Map<string, string>();
  const sortedClusters = [...clusters].sort((a, b) => (a.clusterId < b.clusterId ? -1 : 1));
  sortedClusters.forEach((cluster, index) => {
    clusterColor.set(cluster.clusterId, categoryColor(index));
  });

  const members: { clusterId: string; urlId: string; url: string; similarity: number }[] = [];
  for (const cluster of sortedClusters) {
    for (const member of cluster.members) {
      members.push({
        clusterId: cluster.clusterId,
        urlId: member.urlId,
        url: member.canonicalUrl,
        similarity: member.similarity
      });
    }
  }
  members.sort((a, b) => (a.clusterId < b.clusterId ? -1 : a.clusterId > b.clusterId ? 1 : a.urlId < b.urlId ? -1 : 1));

  const kept = members.slice(0, Math.max(0, maxNodes));
  kept.forEach((member, index) => {
    if (graph.hasNode(member.urlId)) return;
    const seed = seedPosition(index, kept.length);
    graph.addNode(member.urlId, {
      x: seed.x,
      y: seed.y,
      size: 3 + 8 * Math.max(0, Math.min(1, member.similarity)),
      color: clusterColor.get(member.clusterId) ?? '#8b9ab3',
      label: urlLabel(member.url),
      url: member.url,
      depth: 0,
      statusClass: null,
      equityPercentile: null,
      clusterId: member.clusterId,
      kind: 'page'
    });
  });

  for (const cluster of sortedClusters) {
    const centroid = cluster.centroidUrlId ?? cluster.members[0]?.urlId;
    if (!centroid || !graph.hasNode(centroid)) continue;
    for (const member of cluster.members) {
      if (member.urlId === centroid || !graph.hasNode(member.urlId)) continue;
      if (graph.hasEdge(member.urlId, centroid)) continue;
      graph.addEdge(member.urlId, centroid, {
        weight: member.similarity,
        size: 0.5 + member.similarity,
        color: clusterColor.get(cluster.clusterId) ?? '#3d4a63',
        kind: 'similarity'
      });
    }
  }

  if (options.showOpportunities) {
    for (const opportunity of opportunities) {
      if (!graph.hasNode(opportunity.sourceUrlId) || !graph.hasNode(opportunity.targetUrlId)) continue;
      if (graph.hasEdge(opportunity.sourceUrlId, opportunity.targetUrlId)) continue;
      graph.addEdge(opportunity.sourceUrlId, opportunity.targetUrlId, {
        weight: opportunity.similarity,
        size: 1,
        color: '#ae3ec9',
        kind: 'opportunity',
        type: 'dashed'
      });
    }
  }
  return graph;
}

/** Applies worker layout positions onto the graph (immutable attribute merge). */
export function applyPositions(
  graph: CrawlGraph,
  positions: Readonly<Record<string, { x: number; y: number }>>
): void {
  for (const [nodeId, position] of Object.entries(positions)) {
    if (graph.hasNode(nodeId)) {
      graph.mergeNodeAttributes(nodeId, { x: position.x, y: position.y });
    }
  }
}
