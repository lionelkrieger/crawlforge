/**
 * Chart renderer ports (dependency-injection boundary). Pure transforms
 * produce option objects; mounting is isolated behind these ports so jsdom
 * suites run with a mocked canvas renderer — the same port-injection posture
 * the Electron shell used. The real adapter (echartsRenderer.ts) is the only
 * module that imports echarts at runtime.
 *
 * @module charts/chartPorts
 */

import type { EChartsOption } from 'echarts';

/** Click parameters surfaced by the renderer port (renderer-neutral). */
export interface ChartClickParams {
  /** Category / data-item name (e.g. '4xx', 'error', a provider key). */
  readonly name: string;
  /** Series name when present (e.g. a host in a multi-series timeline). */
  readonly seriesName: string | null;
  readonly dataIndex: number;
  readonly value: number | null;
}

/** Image-export request (local download only — never uploaded). */
export interface ChartExportOptions {
  readonly type?: 'png' | 'jpeg';
  readonly pixelRatio?: number;
  readonly backgroundColor?: string;
}

/**
 * Minimal renderer surface consumed by the EChart component. Implementations
 * own their canvas lifecycle; dispose MUST release the canvas, listeners,
 * and any internal timers (§7.4: charts dispose on unmount).
 */
export interface ChartRenderer {
  setOption(option: EChartsOption, notMerge?: boolean): void;
  resize(): void;
  getDataURL(options?: ChartExportOptions): string;
  on(event: 'click', handler: (params: ChartClickParams) => void): void;
  off(event: 'click', handler: (params: ChartClickParams) => void): void;
  dispose(): void;
}

/** Factory port: creates a renderer bound to a container element. */
export type ChartRendererFactory = (container: HTMLElement) => ChartRenderer;
