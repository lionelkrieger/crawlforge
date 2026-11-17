/**
 * Graph-model suite (node env): graphology construction from locked DTOs —
 * equity sizing, status/depth coloring, weighted vs unweighted dom_zone
 * equity, semantic cluster + opportunity overlays, budgets, determinism,
 * and position application (WP-06-04, FS-CRAWL-001 §8.5 / FS-CRAWL-002 §15.6).
 */

import { describe, expect, it } from 'vitest';
import type { LinkEdge } from '../../../src/frontend/src/contracts/api/audits';
import {
  applyPositions,
  buildLinkGraph,
  buildLinkGraphFromExplorer,
  buildSemanticGraph,
  linkEdgeWeight,
  nodeSizeFromPercentile,
  urlLabel
} from '../../../src/frontend/src/graphs/graphModel';
import { statusColors, depthColor } from '../../../src/frontend/src/charts/chartTheme';
import { clustersFixture, graphDataFixture, opportunitiesFixture } from './helpers';

describe('linkEdgeWeight / nodeSizeFromPercentile / urlLabel', () => {
  const edge: LinkEdge = {
    sourceUrlId: 'u1',
    targetUrlId: 'u2',
    sourceUrl: 'https://example.com/',
    targetUrl: 'https://example.com/shoes',
    internal: true,
    anchorText: 'shoes',
    domZone: 'body',
    equityWeight: 0.75
  };

  it('honors zone-weighted equity in weighted mode and counts 1 unweighted', () => {
    expect(linkEdgeWeight(edge, true)).toBe(0.75);
    expect(linkEdgeWeight(edge, false)).toBe(1);
    expect(linkEdgeWeight({ ...edge, equityWeight: undefined }, true)).toBe(1);
  });

  it('maps equity percentile to a 3..12 node radius with clamping', () => {
    expect(nodeSizeFromPercentile(0)).toBe(3);
    expect(nodeSizeFromPercentile(100)).toBe(12);
    expect(nodeSizeFromPercentile(50)).toBe(7.5);
    expect(nodeSizeFromPercentile(null)).toBe(3);
    expect(nodeSizeFromPercentile(-20)).toBe(3);
    expect(nodeSizeFromPercentile(150)).toBe(12);
  });

  it('derives short labels from URLs', () => {
    expect(urlLabel('https://example.com/shoes/running')).toBe('running');
    expect(urlLabel('https://example.com/')).toBe('example.com');
    expect(urlLabel('https://example.com')).toBe('example.com');
    expect(urlLabel('not a url')).toBe('not a url');
  });
});

describe('buildLinkGraph', () => {
  it('sizes nodes by equity and colors by status class', () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    expect(graph.order).toBe(5);
    expect(graph.size).toBe(5);
    const home = graph.getNodeAttributes('u1');
    expect(home.size).toBe(nodeSizeFromPercentile(99));
    expect(home.color).toBe(statusColors['2xx']);
    expect(home.label).toBe('example.com');
    expect(home.kind).toBe('page');
    const broken = graph.getNodeAttributes('u3');
    expect(broken.color).toBe(statusColors['4xx']);
    const unknown = graph.getNodeAttributes('u4');
    expect(unknown.color).toBe(statusColors['2xx']);
    expect(unknown.size).toBe(3);
    const noStatus = buildLinkGraph(
      { nodes: [{ id: 'x', url: 'https://example.com/x', label: null, statusClass: null, depth: 0, linkEquityPercentile: null }], edges: [] },
      { weighted: true, colorMode: 'status' }
    );
    expect(noStatus.getNodeAttributes('x').color).toBe('#8b9ab3');
  });

  it('colors by crawl depth when requested', () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'depth' });
    expect(graph.getNodeAttributes('u1').color).toBe(depthColor(0));
    expect(graph.getNodeAttributes('u5').color).toBe(depthColor(2));
  });

  it('uses edge weights in weighted mode and 1 in unweighted mode', () => {
    const weighted = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    expect(weighted.getEdgeAttributes('u1', 'u2').weight).toBe(3);
    // Null wire weight defaults to 1 (never NaN).
    expect(weighted.getEdgeAttributes('u2', 'u4').weight).toBe(1);
    const unweighted = buildLinkGraph(graphDataFixture, { weighted: false, colorMode: 'status' });
    expect(unweighted.getEdgeAttributes('u1', 'u2').weight).toBe(1);
    expect(unweighted.getEdgeAttributes('u3', 'u5').weight).toBe(1);
    expect(unweighted.getEdgeAttributes('u1', 'u2').size).toBe(0.6);
  });

  it('seeds deterministic circular positions', () => {
    const a = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const b = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    expect(a.getNodeAttributes('u1').x).toBe(b.getNodeAttributes('u1').x);
    expect(a.getNodeAttributes('u1').y).toBe(b.getNodeAttributes('u1').y);
    expect(Number.isFinite(a.getNodeAttributes('u3').x)).toBe(true);
  });

  it('skips duplicate edges and edges with missing endpoints', () => {
    const graph = buildLinkGraph(
      {
        nodes: graphDataFixture.nodes.slice(0, 2),
        edges: [
          { source: 'u1', target: 'u2', weight: 1 },
          { source: 'u1', target: 'u2', weight: 9 },
          { source: 'u1', target: 'ghost', weight: 1 },
          { source: 'ghost', target: 'u2', weight: 1 }
        ]
      },
      { weighted: true, colorMode: 'status' }
    );
    expect(graph.size).toBe(1);
    expect(graph.getEdgeAttributes('u1', 'u2').weight).toBe(1);
  });

  it('handles an empty payload', () => {
    const graph = buildLinkGraph({ nodes: [], edges: [] }, { weighted: true, colorMode: 'status' });
    expect(graph.order).toBe(0);
    expect(graph.size).toBe(0);
  });
});

describe('buildLinkGraphFromExplorer', () => {
  it('maps explorer link rows through zone-weighted equity', () => {
    const edges: LinkEdge[] = [
      {
        sourceUrlId: 'u1',
        targetUrlId: 'u2',
        sourceUrl: 'https://example.com/',
        targetUrl: 'https://example.com/shoes',
        internal: true,
        anchorText: null,
        domZone: 'nav',
        equityWeight: 0.4
      },
      {
        sourceUrlId: 'u2',
        targetUrlId: 'u3',
        sourceUrl: 'https://example.com/shoes',
        targetUrl: 'https://example.com/boots',
        internal: true,
        anchorText: null,
        domZone: 'footer',
        equityWeight: 0.1
      }
    ];
    const weighted = buildLinkGraphFromExplorer(graphDataFixture.nodes, edges, { weighted: true, colorMode: 'status' });
    expect(weighted.getEdgeAttributes('u1', 'u2').weight).toBe(0.4);
    expect(weighted.getEdgeAttributes('u2', 'u3').weight).toBe(0.1);
    const unweighted = buildLinkGraphFromExplorer(graphDataFixture.nodes, edges, { weighted: false, colorMode: 'status' });
    expect(unweighted.getEdgeAttributes('u1', 'u2').weight).toBe(1);
  });
});

describe('buildSemanticGraph', () => {
  it('colors members by cluster and links them to the centroid', () => {
    const graph = buildSemanticGraph(clustersFixture, [], { showOpportunities: false });
    expect(graph.order).toBe(4);
    const shoes = graph.getNodeAttributes('u2');
    const boots = graph.getNodeAttributes('u3');
    const about = graph.getNodeAttributes('u4');
    expect(shoes.color).toBe(boots.color);
    expect(about.color).not.toBe(shoes.color);
    expect(shoes.clusterId).toBe('c1');
    expect(graph.hasEdge('u3', 'u2')).toBe(true);
    expect(graph.getEdgeAttributes('u3', 'u2').kind).toBe('similarity');
    expect(graph.getEdgeAttributes('u3', 'u2').weight).toBe(0.82);
    // c2 has no centroid → first member is the hub (single member: no edges).
    expect(graph.hasEdge('u4', 'u4')).toBe(false);
  });

  it('sizes nodes by similarity', () => {
    const graph = buildSemanticGraph(clustersFixture, [], { showOpportunities: false });
    expect(graph.getNodeAttributes('u3').size).toBeCloseTo(3 + 8 * 0.82, 6);
  });

  it('adds the violet dashed opportunity overlay only when enabled', () => {
    const withOverlay = buildSemanticGraph(clustersFixture, opportunitiesFixture, { showOpportunities: true });
    expect(withOverlay.hasEdge('u4', 'u2')).toBe(true);
    const overlay = withOverlay.getEdgeAttributes('u4', 'u2');
    expect(overlay.kind).toBe('opportunity');
    expect(overlay.color).toBe('#ae3ec9');
    expect(overlay.type).toBe('dashed');
    expect(overlay.weight).toBe(0.77);
    const without = buildSemanticGraph(clustersFixture, opportunitiesFixture, { showOpportunities: false });
    expect(without.hasEdge('u4', 'u2')).toBe(false);
  });

  it('caps nodes deterministically at the budget', () => {
    const graph = buildSemanticGraph(clustersFixture, opportunitiesFixture, { showOpportunities: true, maxNodes: 2 });
    expect(graph.order).toBe(2);
    // Deterministic order: clusterId then urlId → c1 members u2, u3 first.
    expect(graph.hasNode('u2')).toBe(true);
    expect(graph.hasNode('u3')).toBe(true);
    expect(graph.hasNode('u4')).toBe(false);
    // Opportunity endpoints outside the budget are skipped, never dangling.
    expect(graph.size).toBe(1);
  });

  it('deduplicates members appearing in multiple clusters', () => {
    const graph = buildSemanticGraph(
      [
        ...clustersFixture,
        { clusterId: 'c3', members: [{ urlId: 'u2', canonicalUrl: 'https://example.com/shoes', similarity: 0.5 }] }
      ],
      [],
      { showOpportunities: false }
    );
    expect(graph.order).toBe(4);
    expect(graph.getNodeAttributes('u2').clusterId).toBe('c1');
  });

  it('handles empty clusters', () => {
    const graph = buildSemanticGraph([], [], { showOpportunities: true });
    expect(graph.order).toBe(0);
  });
});

describe('applyPositions', () => {
  it('merges layout positions and ignores unknown nodes', () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const before = graph.getNodeAttributes('u1').size;
    applyPositions(graph, { u1: { x: 12.5, y: -4 }, ghost: { x: 0, y: 0 } });
    expect(graph.getNodeAttributes('u1').x).toBe(12.5);
    expect(graph.getNodeAttributes('u1').y).toBe(-4);
    expect(graph.getNodeAttributes('u1').size).toBe(before);
    expect(graph.hasNode('ghost')).toBe(false);
  });
});
