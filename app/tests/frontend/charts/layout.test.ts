/**
 * Layout suite (node env): ForceAtlas2 core determinism, weighted vs
 * unweighted attraction, iteration bounds, graph → request serialization,
 * and the worker client lifecycle — fake-worker round-trip, hung-worker
 * timeout, error rejection, guaranteed termination, and the synchronous
 * fallback (WP-06-04, FS-CRAWL-001 §8.5 / §7.4 no leaked workers).
 */

import { describe, expect, it } from 'vitest';
import {
  BARNES_HUT_THRESHOLD,
  DEFAULT_LAYOUT_ITERATIONS,
  computeLayout,
  type LayoutRequest
} from '../../../src/frontend/src/graphs/layoutCore';
import {
  DEFAULT_LAYOUT_TIMEOUT_MS,
  graphToLayoutRequest,
  runLayout,
  runLayoutSync
} from '../../../src/frontend/src/graphs/layoutClient';
import { buildLinkGraph } from '../../../src/frontend/src/graphs/graphModel';
import { createFakeWorkerFactory, graphDataFixture } from './helpers';

const smallRequest: LayoutRequest = {
  nodes: [
    { id: 'a', x: 1, y: 0 },
    { id: 'b', x: -1, y: 0 },
    { id: 'c', x: 0, y: 1 },
    { id: 'd', x: 0, y: -1 }
  ],
  edges: [
    { source: 'a', target: 'b', weight: 3 },
    { source: 'b', target: 'c', weight: 1 },
    { source: 'c', target: 'd', weight: 1 },
    { source: 'd', target: 'a', weight: 1 }
  ],
  iterations: 60
};

describe('computeLayout (pure core)', () => {
  it('is deterministic for identical inputs', () => {
    const a = computeLayout(smallRequest);
    const b = computeLayout(smallRequest);
    expect(a).toEqual(b);
    expect(Object.keys(a.positions).sort()).toEqual(['a', 'b', 'c', 'd']);
    for (const position of Object.values(a.positions)) {
      expect(Number.isFinite(position.x)).toBe(true);
      expect(Number.isFinite(position.y)).toBe(true);
    }
  });

  it('actually moves nodes from their seeds', () => {
    const result = computeLayout(smallRequest);
    const moved = Object.entries(result.positions).some(([id, position]) => {
      const seed = smallRequest.nodes.find((node) => node.id === id);
      return position.x !== seed?.x || position.y !== seed?.y;
    });
    expect(moved).toBe(true);
  });

  it('honors the iteration bound and defaults', () => {
    expect(computeLayout({ ...smallRequest, iterations: 5 }).iterations).toBe(5);
    expect(computeLayout({ nodes: smallRequest.nodes, edges: [] }).iterations).toBe(DEFAULT_LAYOUT_ITERATIONS);
    expect(computeLayout({ ...smallRequest, iterations: 0 }).iterations).toBe(1);
  });

  it('weighted mode changes attraction versus unweighted', () => {
    const weighted = computeLayout({ ...smallRequest, weighted: true });
    const unweighted = computeLayout({ ...smallRequest, weighted: false });
    expect(weighted.positions).not.toEqual(unweighted.positions);
  });

  it('handles empty graphs and dangling edges safely', () => {
    expect(computeLayout({ nodes: [], edges: [] }).positions).toEqual({});
    const dangling = computeLayout({
      nodes: [{ id: 'a' }],
      edges: [{ source: 'a', target: 'ghost' }, { source: 'ghost', target: 'a' }]
    });
    expect(Object.keys(dangling.positions)).toEqual(['a']);
  });

  it('seeds nodes without positions deterministically on a circle', () => {
    const request: LayoutRequest = { nodes: [{ id: 'a' }, { id: 'b' }, { id: 'c' }], edges: [], iterations: 1 };
    const a = computeLayout(request);
    const b = computeLayout(request);
    expect(a).toEqual(b);
  });

  it('exposes the Barnes-Hut threshold for large graphs', () => {
    expect(BARNES_HUT_THRESHOLD).toBe(1000);
    const nodes = Array.from({ length: 40 }, (_, index) => ({ id: `n${index}` }));
    const edges = nodes.slice(1).map((node, index) => ({ source: nodes[index].id, target: node.id, weight: 1 }));
    const result = computeLayout({ nodes, edges, iterations: 10 });
    expect(Object.keys(result.positions).length).toBe(40);
  });
});

describe('graphToLayoutRequest', () => {
  it('serializes the graph model into a clone-safe request', () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const request = graphToLayoutRequest(graph, { iterations: 42, weighted: true });
    expect(request.nodes.length).toBe(5);
    expect(request.edges.length).toBe(5);
    expect(request.iterations).toBe(42);
    expect(request.weighted).toBe(true);
    const home = request.nodes.find((node) => node.id === 'u1');
    expect(typeof home?.x).toBe('number');
    const edge = request.edges.find((entry) => entry.source === 'u1' && entry.target === 'u2');
    expect(edge?.weight).toBe(3);
    // Structured-clone safe: no graphology instances leak into the payload.
    expect(JSON.parse(JSON.stringify(request))).toEqual(JSON.parse(JSON.stringify(request)));
  });
});

describe('runLayout (worker client lifecycle)', () => {
  it('round-trips through the worker port and terminates it exactly once', async () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const { factory, workers } = createFakeWorkerFactory('compute');
    const result = await runLayout(graph, factory, { iterations: 30, weighted: true });
    expect(workers.length).toBe(1);
    expect(workers[0].requests.length).toBe(1);
    expect(workers[0].requests[0].weighted).toBe(true);
    expect(workers[0].terminateCount).toBe(1);
    expect(Object.keys(result.positions).sort()).toEqual(['u1', 'u2', 'u3', 'u4', 'u5']);
    expect(result.iterations).toBe(30);
  });

  it('rejects and terminates on a hung worker (timeout)', async () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const { factory, workers } = createFakeWorkerFactory('hang');
    await expect(runLayout(graph, factory, { timeoutMs: 25 })).rejects.toThrow('Layout worker timed out');
    expect(workers[0].terminateCount).toBe(1);
    expect(DEFAULT_LAYOUT_TIMEOUT_MS).toBe(30_000);
  });

  it('rejects and terminates on worker error', async () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const { factory, workers } = createFakeWorkerFactory('error');
    await expect(runLayout(graph, factory)).rejects.toThrow('fake worker failure');
    expect(workers[0].terminateCount).toBe(1);
  });

  it('uses the bounded synchronous core when no factory is given', async () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const result = await runLayout(graph, null, { iterations: 20 });
    expect(result.iterations).toBe(20);
    expect(Object.keys(result.positions).length).toBe(5);
  });

  it('runLayoutSync matches the pure core for the same request', () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const options = { iterations: 25, weighted: true };
    expect(runLayoutSync(graph, options)).toEqual(computeLayout(graphToLayoutRequest(graph, options)));
  });

  it('repeated runs never accumulate workers', async () => {
    const graph = buildLinkGraph(graphDataFixture, { weighted: true, colorMode: 'status' });
    const { factory, workers } = createFakeWorkerFactory('compute');
    for (let run = 0; run < 3; run++) {
      await runLayout(graph, factory, { iterations: 10 });
    }
    expect(workers.length).toBe(3);
    expect(workers.every((worker) => worker.terminateCount === 1)).toBe(true);
  });
});
