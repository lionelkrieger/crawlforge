/**
 * Graph sampling suite (node env): deterministic LOD top-K by equity,
 * edge capping by weight, the "showing X of Y" honesty banner driven by
 * GraphData totals/truncated, and drill-down neighborhood extraction
 * (WP-06-04, FS-CRAWL-002 §8.5 — 500k+ scale never renders at once).
 */

import { describe, expect, it } from 'vitest';
import type { GraphData } from '../../../src/frontend/src/contracts/api/audits';
import {
  DEFAULT_LOD_BUDGET,
  neighborhoodSubgraph,
  sampleGraphData
} from '../../../src/frontend/src/graphs/graphSampling';
import { graphDataFixture } from './helpers';

describe('sampleGraphData', () => {
  it('keeps the top-K nodes by equity percentile (null last)', () => {
    const sampled = sampleGraphData(graphDataFixture, { maxNodes: 3, maxEdges: 60_000 });
    expect(sampled.nodes.map((node) => node.id)).toEqual(['u1', 'u2', 'u3']);
    expect(sampled.truncated).toBe(true);
  });

  it('drops edges whose endpoints were sampled out', () => {
    const sampled = sampleGraphData(graphDataFixture, { maxNodes: 3, maxEdges: 60_000 });
    expect(sampled.edges.map((edge) => `${edge.source}->${edge.target}`)).toEqual(['u1->u2', 'u1->u3']);
  });

  it('caps edges by weight descending with stable tie-break', () => {
    const sampled = sampleGraphData(graphDataFixture, { maxNodes: 5, maxEdges: 2 });
    expect(sampled.edges.map((edge) => `${edge.source}->${edge.target}`)).toEqual(['u1->u2', 'u5->u1']);
    expect(sampled.banner).toContain('2 of 1,200,000 edges');
  });

  it('sorts null edge weights last without NaN', () => {
    const data: GraphData = {
      ...graphDataFixture,
      edges: [
        { source: 'u1', target: 'u2', weight: null },
        { source: 'u1', target: 'u3', weight: 0.5 }
      ]
    };
    const sampled = sampleGraphData(data, { maxNodes: 5, maxEdges: 1 });
    expect(sampled.edges[0]).toEqual({ source: 'u1', target: 'u3', weight: 0.5 });
  });

  it('emits the honesty banner from full-graph totals', () => {
    const sampled = sampleGraphData(graphDataFixture, { maxNodes: 3, maxEdges: 2 });
    expect(sampled.banner).toBe(
      'Showing 3 of 500,000 nodes · 2 of 1,200,000 edges — drill down or filter to explore more'
    );
    expect(sampled.totalNodeCount).toBe(500_000);
    expect(sampled.totalEdgeCount).toBe(1_200_000);
  });

  it('omits the banner when the full bounded graph fits', () => {
    const fits: GraphData = { ...graphDataFixture, totalNodeCount: 5, totalEdgeCount: 5, truncated: false };
    const sampled = sampleGraphData(fits, DEFAULT_LOD_BUDGET);
    expect(sampled.banner).toBeNull();
    expect(sampled.truncated).toBe(false);
    expect(sampled.nodes.length).toBe(5);
    expect(sampled.edges.length).toBe(5);
  });

  it('honors the server truncated flag even when the payload fits the budget', () => {
    const flagged: GraphData = { ...graphDataFixture, totalNodeCount: 5, totalEdgeCount: 5, truncated: true };
    const sampled = sampleGraphData(flagged, DEFAULT_LOD_BUDGET);
    expect(sampled.truncated).toBe(true);
    expect(sampled.banner).not.toBeNull();
  });

  it('is deterministic regardless of input ordering', () => {
    const shuffled: GraphData = {
      ...graphDataFixture,
      nodes: [...graphDataFixture.nodes].reverse(),
      edges: [...graphDataFixture.edges].reverse()
    };
    const a = sampleGraphData(graphDataFixture, { maxNodes: 3, maxEdges: 2 });
    const b = sampleGraphData(shuffled, { maxNodes: 3, maxEdges: 2 });
    expect(a.nodes.map((node) => node.id)).toEqual(b.nodes.map((node) => node.id));
    expect(a.edges).toEqual(b.edges);
    expect(a.banner).toBe(b.banner);
  });

  it('breaks equity ties by node id for stability', () => {
    const data: GraphData = {
      ...graphDataFixture,
      nodes: [
        { id: 'b', url: 'https://example.com/b', label: null, statusClass: '2xx', depth: 0, linkEquityPercentile: 50 },
        { id: 'a', url: 'https://example.com/a', label: null, statusClass: '2xx', depth: 0, linkEquityPercentile: 50 }
      ],
      edges: []
    };
    const sampled = sampleGraphData(data, { maxNodes: 1, maxEdges: 10 });
    expect(sampled.nodes[0].id).toBe('a');
  });

  it('handles an empty graph and zero budgets safely', () => {
    const empty: GraphData = { nodes: [], edges: [], totalNodeCount: 0, totalEdgeCount: 0, truncated: false };
    expect(sampleGraphData(empty).banner).toBeNull();
    const zero = sampleGraphData(graphDataFixture, { maxNodes: 0, maxEdges: 0 });
    expect(zero.nodes).toEqual([]);
    expect(zero.edges).toEqual([]);
    expect(zero.truncated).toBe(true);
  });
});

describe('neighborhoodSubgraph (drill-down)', () => {
  it('extracts the induced radius-1 neighborhood of the focus node', () => {
    const sampled = sampleGraphData(graphDataFixture, DEFAULT_LOD_BUDGET);
    const local = neighborhoodSubgraph(sampled, 'u1', 1);
    expect(local.nodes.map((node) => node.id).sort()).toEqual(['u1', 'u2', 'u3', 'u5']);
    // Induced edges include u3->u5 because both endpoints are in the neighborhood.
    expect(local.edges.map((edge) => `${edge.source}->${edge.target}`).sort()).toEqual([
      'u1->u2',
      'u1->u3',
      'u3->u5',
      'u5->u1'
    ]);
    expect(local.banner).toContain('Neighborhood of u1');
    expect(local.banner).toContain('500,000 total');
    expect(local.totalNodeCount).toBe(500_000);
  });

  it('radius 0 returns only the focus node', () => {
    const sampled = sampleGraphData(graphDataFixture, DEFAULT_LOD_BUDGET);
    const local = neighborhoodSubgraph(sampled, 'u2', 0);
    expect(local.nodes.map((node) => node.id)).toEqual(['u2']);
    expect(local.edges).toEqual([]);
  });

  it('radius 2 reaches second-hop neighbors', () => {
    const sampled = sampleGraphData(graphDataFixture, DEFAULT_LOD_BUDGET);
    const local = neighborhoodSubgraph(sampled, 'u4', 2);
    // u4 <- u2 <- u1: two undirected hops reach u1, not the u3/u5 branch.
    expect(local.nodes.map((node) => node.id).sort()).toEqual(['u1', 'u2', 'u4']);
  });

  it('unknown focus yields an empty local view without throwing', () => {
    const sampled = sampleGraphData(graphDataFixture, DEFAULT_LOD_BUDGET);
    const local = neighborhoodSubgraph(sampled, 'ghost', 1);
    expect(local.nodes).toEqual([]);
    expect(local.edges).toEqual([]);
  });
});
