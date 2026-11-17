/**
 * EChart mount component (FS-CRAWL-002 §8.4/§7.4): lazy renderer creation
 * behind the injected ChartRendererFactory port, resize-observer driven,
 * dispose on unmount (no leaked canvases or listeners), click → cross-filter
 * callback, and local export-to-image. Presentational: the option object is
 * produced by the pure transforms; this component never computes data.
 *
 * @module charts/EChart
 */

import { useEffect, useRef } from 'react';
import type { EChartsOption } from 'echarts';
import type { ChartClickParams, ChartRenderer, ChartRendererFactory } from './chartPorts';
import { createEChartsRendererFactory } from './echartsRenderer';

/** Default factory singleton (real canvas renderer; tests inject a mock). */
let defaultFactory: ChartRendererFactory | null = null;
function getDefaultFactory(): ChartRendererFactory {
  defaultFactory ??= createEChartsRendererFactory();
  return defaultFactory;
}

/** EChart props. */
export interface EChartProps {
  readonly option: EChartsOption;
  /** Renderer port override (tests inject a mock; default = real canvas). */
  readonly factory?: ChartRendererFactory;
  readonly height?: number;
  /** Click → cross-filter bridge (selection mapping lives in the surface). */
  readonly onChartClick?: (params: ChartClickParams) => void;
  /** When set, shows an export-to-image button (local download only). */
  readonly exportName?: string;
  readonly ariaLabel: string;
}

/** Downloads a data URL as a local file (never uploads; E2/E9 posture). */
export function downloadDataUrl(dataUrl: string, fileName: string): void {
  if (!dataUrl) return;
  const anchor = document.createElement('a');
  anchor.href = dataUrl;
  anchor.download = fileName;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}

/** One mounted ECharts surface with strict cleanup. */
export function EChart({ option, factory, height = 260, onChartClick, exportName, ariaLabel }: EChartProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const rendererRef = useRef<ChartRenderer | null>(null);
  const optionRef = useRef(option);
  optionRef.current = option;
  const clickRef = useRef(onChartClick);
  clickRef.current = onChartClick;
  const lastAppliedRef = useRef<EChartsOption | null>(null);

  // Mount/dispose the renderer (factory identity is the only mount dep).
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const activeFactory = factory ?? getDefaultFactory();
    const renderer = activeFactory(container);
    rendererRef.current = renderer;
    renderer.setOption(optionRef.current, true);
    lastAppliedRef.current = optionRef.current;

    const handleClick = (params: ChartClickParams): void => clickRef.current?.(params);
    renderer.on('click', handleClick);

    let observer: ResizeObserver | null = null;
    if (typeof ResizeObserver !== 'undefined') {
      // Guard against callbacks queued before disconnect: a disposed
      // renderer must never receive a resize (§7.4).
      observer = new ResizeObserver(() => {
        if (rendererRef.current === renderer) renderer.resize();
      });
      observer.observe(container);
    }

    return () => {
      observer?.disconnect();
      renderer.off('click', handleClick);
      renderer.dispose();
      rendererRef.current = null;
    };
  }, [factory]);

  // Option updates never remount the renderer (and never re-apply the
  // option the mount effect just applied).
  useEffect(() => {
    if (lastAppliedRef.current === option) return;
    lastAppliedRef.current = option;
    rendererRef.current?.setOption(option, true);
  }, [option]);

  const handleExport = (): void => {
    const renderer = rendererRef.current;
    if (!renderer || !exportName) return;
    downloadDataUrl(renderer.getDataURL({ type: 'png', pixelRatio: 2 }), `${exportName}.png`);
  };

  return (
    <div data-testid="cf-echart" style={{ position: 'relative', width: '100%', height }}>
      <div
        ref={containerRef}
        role="img"
        aria-label={ariaLabel}
        data-testid="cf-echart-canvas"
        style={{ width: '100%', height: '100%' }}
      />
      {exportName ? (
        <button
          type="button"
          data-testid="cf-echart-export"
          onClick={handleExport}
          aria-label={`Export ${ariaLabel} as image`}
          style={{
            position: 'absolute',
            top: 4,
            right: 4,
            background: '#1d2436',
            color: '#aab4c8',
            border: '1px solid #2b3348',
            borderRadius: 4,
            fontSize: 11,
            padding: '2px 8px',
            cursor: 'pointer'
          }}
        >
          Export PNG
        </button>
      ) : null}
    </div>
  );
}
