// @vitest-environment jsdom
/**
 * Graph surface mount/lifecycle suite (jsdom, mocked sigma port + fake
 * layout workers — no WebGL): worker layout application, LOD honesty
 * banner, drill-down/reset, hover inspection, weighted/color toggles,
 * opportunity overlay, worker-error fallback, and strict kill/terminate
 * disposal with no leaked instances, handlers, or workers
 * (WP-06-04, FS-CRAWL-002 §8.5 / §7.4).
 */

import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { LinkGraphSurface } from '../../../src/frontend/src/graphs/LinkGraphSurface';
import { SemanticGraphSurface } from '../../../src/frontend/src/graphs/SemanticGraphSurface';
import { statusColors, depthColor } from '../../../src/frontend/src/charts/chartTheme';
import {
  byTestId,
  click,
  clustersFixture,
  createFakeWorkerFactory,
  createMockGraphFactory,
  flush,
  graphDataFixture,
  opportunitiesFixture,
  render
} from './helpers';

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  document.body.replaceChildren();
});

describe('LinkGraphSurface lifecycle', () => {
  it('lays out in the worker, mounts one renderer, and shows the honesty banner', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const view = await render(
      <LinkGraphSurface
        data={graphDataFixture}
        weighted
        colorMode="status"
        rendererFactory={renderer.factory}
        workerFactory={worker.factory}
      />
    );
    await flush();
    expect(renderer.instances.length).toBe(1);
    expect(worker.workers.length).toBe(1);
    expect(worker.workers[0].terminateCount).toBe(1);
    // Layout moved nodes off their circular seeds.
    const home = renderer.instances[0].graph.getNodeAttributes('u1');
    expect(Number.isFinite(home.x)).toBe(true);
    const banner = byTestId(view.container, 'cf-linkgraph-banner');
    expect(banner?.textContent).toContain('Showing 5 of 500,000 nodes');
    expect(byTestId(view.container, 'cf-linkgraph-layout')).toBeNull();
    view.unmount();
    expect(renderer.instances[0].killCount).toBe(1);
  });

  it('kills the renderer, clears handlers, and terminates workers on unmount', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const view = await render(
      <LinkGraphSurface
        data={graphDataFixture}
        weighted
        colorMode="status"
        rendererFactory={renderer.factory}
        workerFactory={worker.factory}
      />
    );
    await flush();
    const instance = renderer.instances[0];
    expect(instance.clickHandlers.size).toBe(1);
    expect(instance.hoverHandlers.size).toBe(1);
    view.unmount();
    expect(instance.killCount).toBe(1);
    expect(instance.clickHandlers.size).toBe(0);
    expect(instance.hoverHandlers.size).toBe(0);
    expect(worker.workers.every((entry) => entry.terminateCount === 1)).toBe(true);
  });

  it('falls back to the bounded sync layout when the worker errors', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('error');
    const view = await render(
      <LinkGraphSurface
        data={graphDataFixture}
        weighted
        colorMode="status"
        rendererFactory={renderer.factory}
        workerFactory={worker.factory}
      />
    );
    await flush();
    expect(worker.workers[0].terminateCount).toBe(1);
    expect(renderer.instances.length).toBe(1);
    expect(Number.isFinite(renderer.instances[0].graph.getNodeAttributes('u2').x)).toBe(true);
    view.unmount();
  });

  it('renders the empty state without mounting a renderer when data is null', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <LinkGraphSurface data={null} weighted colorMode="status" rendererFactory={renderer.factory} workerFactory={null} />
    );
    expect(byTestId(view.container, 'cf-linkgraph-empty')).not.toBeNull();
    expect(renderer.instances.length).toBe(0);
    view.unmount();
  });

  it('repeated mount/unmount cycles never leak renderers or workers', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    for (let cycle = 0; cycle < 3; cycle++) {
      const view = await render(
        <LinkGraphSurface
          data={graphDataFixture}
          weighted
          colorMode="status"
          rendererFactory={renderer.factory}
          workerFactory={worker.factory}
        />
      );
      await flush();
      view.unmount();
    }
    expect(renderer.instances.length).toBe(3);
    expect(renderer.instances.every((instance) => instance.killCount === 1)).toBe(true);
    expect(worker.workers.length).toBe(3);
    expect(worker.workers.every((entry) => entry.terminateCount === 1)).toBe(true);
  });
});

describe('LinkGraphSurface interaction', () => {
  it('selects nodes, drills into the neighborhood, and resets the view', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const selected: { nodeId: string; url: string }[] = [];
    const view = await render(
      <LinkGraphSurface
        data={graphDataFixture}
        weighted
        colorMode="status"
        rendererFactory={renderer.factory}
        workerFactory={worker.factory}
        onNodeSelect={(nodeId, url) => selected.push({ nodeId, url })}
      />
    );
    await flush();
    act(() => renderer.instances[0].emitClick('u1'));
    expect(selected).toEqual([{ nodeId: 'u1', url: 'https://example.com/' }]);
    expect(byTestId(view.container, 'cf-linkgraph-selected')?.textContent).toContain('https://example.com/');

    const drill = byTestId(view.container, 'cf-linkgraph-drill');
    expect(drill).not.toBeNull();
    click(drill!);
    await flush();
    expect(renderer.instances.length).toBe(2);
    expect(renderer.instances[0].killCount).toBe(1);
    const local = renderer.instances[1].graph;
    expect(local.order).toBe(4);
    expect(local.hasNode('u4')).toBe(false);
    expect(byTestId(view.container, 'cf-linkgraph-banner')?.textContent).toContain('Neighborhood of u1');

    const reset = byTestId(view.container, 'cf-linkgraph-reset');
    click(reset!);
    await flush();
    expect(renderer.instances.length).toBe(3);
    expect(renderer.instances[2].graph.order).toBe(5);
    expect(byTestId(view.container, 'cf-linkgraph-banner')?.textContent).toContain('Showing 5 of 500,000');
    view.unmount();
    expect(renderer.instances.every((instance) => instance.killCount === 1)).toBe(true);
  });

  it('shows the hover inspection tooltip and clears it on leave', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <LinkGraphSurface
        data={graphDataFixture}
        weighted
        colorMode="status"
        rendererFactory={renderer.factory}
        workerFactory={null}
      />
    );
    await flush();
    act(() => renderer.instances[0].emitHover('u3'));
    const tooltip = byTestId(view.container, 'cf-linkgraph-tooltip');
    expect(tooltip?.textContent).toContain('boots');
    expect(tooltip?.textContent).toContain('depth 1');
    expect(tooltip?.textContent).toContain('4xx');
    expect(tooltip?.textContent).toContain('equity p40');
    act(() => renderer.instances[0].emitHover(null));
    expect(byTestId(view.container, 'cf-linkgraph-tooltip')).toBeNull();
    view.unmount();
  });

  it('rebuilds with unweighted edges when the weighted toggle flips', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <LinkGraphSurface data={graphDataFixture} weighted colorMode="status" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(renderer.instances[0].graph.getEdgeAttributes('u1', 'u2').weight).toBe(3);
    view.rerender(
      <LinkGraphSurface data={graphDataFixture} weighted={false} colorMode="status" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(renderer.instances.length).toBe(2);
    expect(renderer.instances[0].killCount).toBe(1);
    expect(renderer.instances[1].graph.getEdgeAttributes('u1', 'u2').weight).toBe(1);
    view.unmount();
  });

  it('recolors by depth when the color mode changes', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <LinkGraphSurface data={graphDataFixture} weighted colorMode="status" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(renderer.instances[0].graph.getNodeAttributes('u3').color).toBe(statusColors['4xx']);
    view.rerender(
      <LinkGraphSurface data={graphDataFixture} weighted colorMode="depth" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(renderer.instances[1].graph.getNodeAttributes('u5').color).toBe(depthColor(2));
    view.unmount();
  });
});

describe('SemanticGraphSurface lifecycle', () => {
  it('mounts the cluster network with the overlay banner and counts', async () => {
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const view = await render(
      <SemanticGraphSurface
        clusters={clustersFixture}
        opportunities={opportunitiesFixture}
        showOpportunities
        rendererFactory={renderer.factory}
        workerFactory={worker.factory}
      />
    );
    await flush();
    expect(renderer.instances.length).toBe(1);
    const banner = byTestId(view.container, 'cf-semgraph-banner');
    expect(banner?.textContent).toContain('4 pages');
    expect(banner?.textContent).toContain('2 clusters');
    expect(banner?.textContent).toContain('1 link opportunities');
    const graph = renderer.instances[0].graph;
    expect(graph.hasEdge('u4', 'u2')).toBe(true);
    expect(graph.getEdgeAttributes('u4', 'u2').kind).toBe('opportunity');
    view.unmount();
    expect(renderer.instances[0].killCount).toBe(1);
    expect(worker.workers[0].terminateCount).toBe(1);
  });

  it('removes the opportunity overlay when toggled off', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <SemanticGraphSurface
        clusters={clustersFixture}
        opportunities={opportunitiesFixture}
        showOpportunities
        rendererFactory={renderer.factory}
        workerFactory={null}
      />
    );
    await flush();
    view.rerender(
      <SemanticGraphSurface
        clusters={clustersFixture}
        opportunities={opportunitiesFixture}
        showOpportunities={false}
        rendererFactory={renderer.factory}
        workerFactory={null}
      />
    );
    await flush();
    expect(renderer.instances.length).toBe(2);
    expect(renderer.instances[1].graph.hasEdge('u4', 'u2')).toBe(false);
    expect(byTestId(view.container, 'cf-semgraph-banner')?.textContent).not.toContain('link opportunities');
    view.unmount();
  });

  it('selects nodes and shows the empty state without clusters', async () => {
    const renderer = createMockGraphFactory();
    const selected: string[] = [];
    const view = await render(
      <SemanticGraphSurface
        clusters={clustersFixture}
        opportunities={[]}
        showOpportunities={false}
        rendererFactory={renderer.factory}
        workerFactory={null}
        onNodeSelect={(nodeId) => selected.push(nodeId)}
      />
    );
    await flush();
    act(() => renderer.instances[0].emitClick('u3'));
    expect(selected).toEqual(['u3']);
    view.unmount();

    const empty = await render(
      <SemanticGraphSurface clusters={[]} opportunities={[]} showOpportunities={false} rendererFactory={renderer.factory} workerFactory={null} />
    );
    expect(byTestId(empty.container, 'cf-semgraph-empty')).not.toBeNull();
    expect(renderer.instances.length).toBe(1);
    empty.unmount();
  });
});
