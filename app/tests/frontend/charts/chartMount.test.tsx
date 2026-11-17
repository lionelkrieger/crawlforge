// @vitest-environment jsdom
/**
 * EChart mount/lifecycle suite (jsdom, mocked canvas renderer port):
 * mount → setOption, option updates without remount, resize-observer
 * wiring, click → cross-filter callback, local export-to-image download,
 * and strict dispose on unmount (no leaked instances or listeners)
 * (WP-06-04, FS-CRAWL-002 §7.4).
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EChart, downloadDataUrl } from '../../../src/frontend/src/charts/EChart';
import { statusDistributionOption } from '../../../src/frontend/src/charts/chartTransforms';
import { byTestId, click, createMockChartFactory, flush, render } from './helpers';

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

describe('EChart mounting', () => {
  it('creates exactly one renderer and applies the option', async () => {
    const { factory, instances } = createMockChartFactory();
    const option = statusDistributionOption({ '2xx': 10, '4xx': 2 });
    const view = await render(<EChart factory={factory} option={option} ariaLabel="Status distribution" />);
    expect(instances.length).toBe(1);
    expect(instances[0].options.length).toBe(1);
    expect(instances[0].options[0]).toEqual(option);
    expect(byTestId(view.container, 'cf-echart-canvas')).not.toBeNull();
    view.unmount();
  });

  it('applies option updates without remounting the renderer', async () => {
    const { factory, instances } = createMockChartFactory();
    const first = statusDistributionOption({ '2xx': 10 });
    const second = statusDistributionOption({ '5xx': 3 });
    const view = await render(<EChart factory={factory} option={first} ariaLabel="Status" />);
    view.rerender(<EChart factory={factory} option={second} ariaLabel="Status" />);
    await flush();
    expect(instances.length).toBe(1);
    expect(instances[0].options.length).toBe(2);
    expect(instances[0].options[1]).toEqual(second);
    view.unmount();
  });

  it('wires the ResizeObserver and disconnects it on unmount', async () => {
    const callbacks: ResizeObserverCallback[] = [];
    const disconnects: number[] = [];
    class CapturingResizeObserver {
      constructor(callback: ResizeObserverCallback) {
        callbacks.push(callback);
      }
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {
        disconnects.push(1);
      }
    }
    const original = globalThis.ResizeObserver;
    globalThis.ResizeObserver = CapturingResizeObserver as unknown as typeof ResizeObserver;
    try {
      const { factory, instances } = createMockChartFactory();
      const view = await render(
        <EChart factory={factory} option={statusDistributionOption({})} ariaLabel="Status" />
      );
      expect(callbacks.length).toBe(1);
      callbacks[0]([], {} as ResizeObserver);
      expect(instances[0].resizeCount).toBe(1);
      view.unmount();
      expect(disconnects.length).toBe(1);
      // Resize after unmount must not reach the disposed renderer.
      callbacks[0]([], {} as ResizeObserver);
      expect(instances[0].resizeCount).toBe(1);
    } finally {
      globalThis.ResizeObserver = original;
    }
  });
});

describe('EChart interaction', () => {
  it('forwards renderer clicks to the cross-filter callback', async () => {
    const { factory, instances } = createMockChartFactory();
    const selections: { name: string }[] = [];
    const view = await render(
      <EChart
        factory={factory}
        option={statusDistributionOption({ '4xx': 7 })}
        ariaLabel="Status"
        onChartClick={(params) => selections.push({ name: params.name })}
      />
    );
    instances[0].emitClick({ name: '4xx', seriesName: null, dataIndex: 2, value: 7 });
    expect(selections).toEqual([{ name: '4xx' }]);
    view.unmount();
    // After unmount the handler is removed — no post-dispose callbacks.
    instances[0].emitClick({ name: '5xx', seriesName: null, dataIndex: 3, value: 3 });
    expect(selections.length).toBe(1);
  });

  it('exports a local PNG download (data URL, never uploaded)', async () => {
    const { factory } = createMockChartFactory();
    const anchors: HTMLAnchorElement[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      anchors.push(this);
    });
    const view = await render(
      <EChart
        factory={factory}
        option={statusDistributionOption({ '2xx': 1 })}
        ariaLabel="Status distribution"
        exportName="status-distribution"
      />
    );
    const button = byTestId(view.container, 'cf-echart-export');
    expect(button).not.toBeNull();
    click(button!);
    expect(anchors.length).toBe(1);
    expect(anchors[0].download).toBe('status-distribution.png');
    expect(anchors[0].href).toContain('data:image/png;base64,MOCKCHART');
    view.unmount();
  });

  it('downloadDataUrl ignores empty payloads', () => {
    const spy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    downloadDataUrl('', 'empty.png');
    expect(spy).not.toHaveBeenCalled();
  });

  it('shows no export button without exportName', async () => {
    const { factory } = createMockChartFactory();
    const view = await render(
      <EChart factory={factory} option={statusDistributionOption({})} ariaLabel="Status" />
    );
    expect(byTestId(view.container, 'cf-echart-export')).toBeNull();
    view.unmount();
  });
});

describe('EChart disposal discipline', () => {
  it('disposes the renderer and removes listeners on unmount', async () => {
    const { factory, instances } = createMockChartFactory();
    const view = await render(
      <EChart factory={factory} option={statusDistributionOption({})} ariaLabel="Status" />
    );
    expect(instances[0].clickHandlers.size).toBe(1);
    view.unmount();
    expect(instances[0].disposeCount).toBe(1);
    expect(instances[0].clickHandlers.size).toBe(0);
  });

  it('repeated mount/unmount cycles never leak instances or handlers', async () => {
    const { factory, instances } = createMockChartFactory();
    for (let cycle = 0; cycle < 3; cycle++) {
      const view = await render(
        <EChart factory={factory} option={statusDistributionOption({ '2xx': cycle })} ariaLabel="Status" />
      );
      await flush();
      view.unmount();
    }
    expect(instances.length).toBe(3);
    expect(instances.every((instance) => instance.disposeCount === 1)).toBe(true);
    expect(instances.every((instance) => instance.clickHandlers.size === 0)).toBe(true);
  });

  it('swapping the factory disposes the old renderer and mounts the new one', async () => {
    const first = createMockChartFactory();
    const second = createMockChartFactory();
    const option = statusDistributionOption({ '2xx': 1 });
    const view = await render(<EChart factory={first.factory} option={option} ariaLabel="Status" />);
    view.rerender(<EChart factory={second.factory} option={option} ariaLabel="Status" />);
    await flush();
    expect(first.instances[0].disposeCount).toBe(1);
    expect(second.instances.length).toBe(1);
    expect(second.instances[0].options[0]).toEqual(option);
    view.unmount();
    expect(second.instances[0].disposeCount).toBe(1);
  });
});
