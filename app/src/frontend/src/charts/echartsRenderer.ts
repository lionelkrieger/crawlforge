/**
 * Real ECharts adapter behind the ChartRendererFactory port. Canvas
 * rendering only (FS-CRAWL-002 §8.4: no SVG DOM for large datasets); the
 * WebGL extension can be swapped in for very large series without touching
 * consumers. Tree-shaken echarts/core registration keeps the offline bundle
 * lean. This module is the ONLY runtime echarts import in the charts area.
 *
 * @module charts/echartsRenderer
 */

import * as echarts from 'echarts/core';
import { BarChart, LineChart, PieChart, GaugeChart, TreemapChart } from 'echarts/charts';
import {
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';
import type { EChartsOption } from 'echarts';
import { crawlForgeChartTheme, crawlForgeChartThemeName } from './chartTheme';
import type { ChartClickParams, ChartExportOptions, ChartRenderer, ChartRendererFactory } from './chartPorts';

echarts.use([
  BarChart,
  LineChart,
  PieChart,
  GaugeChart,
  TreemapChart,
  TitleComponent,
  TooltipComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  CanvasRenderer
]);

echarts.registerTheme(crawlForgeChartThemeName, crawlForgeChartTheme);

/** Structural shape of the echarts click event we consume. */
interface EChartsClickEvent {
  name?: string;
  seriesName?: string;
  dataIndex?: number;
  value?: number | string | (number | string)[];
}

function toClickParams(event: EChartsClickEvent): ChartClickParams {
  const raw = event.value;
  const value = typeof raw === 'number' ? raw : Array.isArray(raw) && typeof raw[1] === 'number' ? raw[1] : null;
  return {
    name: event.name ?? '',
    seriesName: event.seriesName ?? null,
    dataIndex: event.dataIndex ?? 0,
    value
  };
}

/** Creates the production canvas renderer factory (lazy per-container init). */
export function createEChartsRendererFactory(): ChartRendererFactory {
  return (container: HTMLElement): ChartRenderer => {
    const instance = echarts.init(container, crawlForgeChartThemeName, { renderer: 'canvas' });
    const handlers = new Map<(params: ChartClickParams) => void, (event: EChartsClickEvent) => void>();
    let disposed = false;

    return {
      setOption(option: EChartsOption, notMerge = true): void {
        if (disposed) return;
        instance.setOption(option, { notMerge });
      },
      resize(): void {
        if (disposed) return;
        instance.resize();
      },
      getDataURL(options?: ChartExportOptions): string {
        if (disposed) return '';
        return instance.getDataURL({
          type: options?.type ?? 'png',
          pixelRatio: options?.pixelRatio ?? 2,
          backgroundColor: options?.backgroundColor ?? '#141a29'
        });
      },
      on(event: 'click', handler: (params: ChartClickParams) => void): void {
        if (disposed || event !== 'click') return;
        const wrapped = (raw: EChartsClickEvent): void => handler(toClickParams(raw));
        handlers.set(handler, wrapped);
        instance.on('click', wrapped as (params: unknown) => void);
      },
      off(event: 'click', handler: (params: ChartClickParams) => void): void {
        if (event !== 'click') return;
        const wrapped = handlers.get(handler);
        if (wrapped) {
          instance.off('click', wrapped as (params: unknown) => void);
          handlers.delete(handler);
        }
      },
      dispose(): void {
        if (disposed) return;
        disposed = true;
        handlers.clear();
        instance.dispose();
      }
    };
  };
}
