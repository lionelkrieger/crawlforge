/**
 * Internal link-network surface (FS-CRAWL-001 §8.5, FS-CRAWL-002 §8.5/§15.6):
 * force-directed sigma graph with bounded LOD sampling, the "showing X of Y"
 * honesty banner, weighted/unweighted equity (node sizing honors dom_zone
 * weights through the server's equityWeight), status/depth color modes,
 * hover inspection, and drill-down neighborhood navigation. All rendering
 * sits behind injected ports — jsdom suites run without WebGL.
 *
 * @module graphs/LinkGraphSurface
 */

import { useMemo, useState, type CSSProperties } from 'react';
import type { GraphData } from '../contracts/api/audits';
import type { GraphRendererFactory } from './graphPorts';
import type { LayoutWorkerFactory } from './layoutClient';
import { buildLinkGraph, urlLabel } from './graphModel';
import {
  DEFAULT_LOD_BUDGET,
  neighborhoodSubgraph,
  sampleGraphData,
  type LodBudget
} from './graphSampling';
import { useGraphRenderer } from './useGraphRenderer';

/** Link-graph surface props. */
export interface LinkGraphSurfaceProps {
  readonly data: GraphData | null;
  readonly weighted: boolean;
  readonly colorMode: 'status' | 'depth';
  readonly budget?: LodBudget;
  /** Live label-density control (sigma labelRenderedSizeThreshold). */
  readonly labelThreshold?: number;
  /** Live ForceAtlas2 iteration budget. */
  readonly iterations?: number;
  /** Drill-down neighborhood radius (undirected hops). */
  readonly drillRadius?: number;
  readonly rendererFactory?: GraphRendererFactory;
  readonly workerFactory?: LayoutWorkerFactory | null;
  /** Node selection callback (drill-down to URL detail in the shell). */
  readonly onNodeSelect?: (nodeId: string, url: string) => void;
  readonly height?: number;
}

/** The force-directed internal link graph with LOD + drill-down. */
export function LinkGraphSurface({
  data,
  weighted,
  colorMode,
  budget = DEFAULT_LOD_BUDGET,
  labelThreshold = 8,
  iterations,
  drillRadius = 1,
  rendererFactory,
  workerFactory,
  onNodeSelect,
  height = 480
}: LinkGraphSurfaceProps) {
  const [focusId, setFocusId] = useState<string | null>(null);
  const [hoverId, setHoverId] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const baseSample = useMemo(
    () => (data ? sampleGraphData(data, budget) : null),
    [data, budget]
  );
  const sampled = useMemo(
    () => (baseSample && focusId ? neighborhoodSubgraph(baseSample, focusId, drillRadius) : baseSample),
    [baseSample, focusId, drillRadius]
  );
  const graph = useMemo(
    () => buildLinkGraph(sampled ?? { nodes: [], edges: [] }, { weighted, colorMode }),
    [sampled, weighted, colorMode]
  );
  const settings = useMemo(
    () => ({ labelRenderedSizeThreshold: labelThreshold }),
    [labelThreshold]
  );

  const { containerRef, layoutState } = useGraphRenderer(graph, {
    rendererFactory,
    workerFactory,
    weighted,
    iterations,
    settings,
    onNodeClick: (nodeId) => {
      setSelectedId(nodeId);
      const node = graph.hasNode(nodeId) ? graph.getNodeAttributes(nodeId) : null;
      if (node) onNodeSelect?.(nodeId, node.url);
    },
    onNodeHover: setHoverId
  });

  const hoverAttributes = hoverId && graph.hasNode(hoverId) ? graph.getNodeAttributes(hoverId) : null;
  const selectedAttributes = selectedId && graph.hasNode(selectedId) ? graph.getNodeAttributes(selectedId) : null;

  if (!data) {
    return (
      <div data-testid="cf-linkgraph-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No link graph loaded — start an audit or open a completed one.
      </div>
    );
  }

  return (
    <div data-testid="cf-linkgraph" style={{ position: 'relative', width: '100%', height }}>
      {sampled?.banner ? (
        <div
          data-testid="cf-linkgraph-banner"
          style={{
            position: 'absolute',
            top: 8,
            left: 8,
            zIndex: 2,
            background: '#1d2436',
            border: '1px solid #2b3348',
            borderRadius: 4,
            color: '#aab4c8',
            fontSize: 11,
            padding: '4px 8px'
          }}
        >
          {sampled.banner}
        </div>
      ) : null}

      <div style={{ position: 'absolute', top: 8, right: 8, zIndex: 2, display: 'flex', gap: 6 }}>
        {selectedId ? (
          <button
            type="button"
            data-testid="cf-linkgraph-drill"
            onClick={() => setFocusId(selectedId)}
            style={controlStyle}
          >
            Drill into neighborhood
          </button>
        ) : null}
        {focusId ? (
          <button
            type="button"
            data-testid="cf-linkgraph-reset"
            onClick={() => setFocusId(null)}
            style={controlStyle}
          >
            Reset view
          </button>
        ) : null}
      </div>

      {layoutState === 'running' ? (
        <div data-testid="cf-linkgraph-layout" style={{ position: 'absolute', bottom: 8, left: 8, zIndex: 2, color: '#66d9e8', fontSize: 11 }}>
          Computing layout in worker…
        </div>
      ) : null}

      {hoverAttributes ? (
        <div
          data-testid="cf-linkgraph-tooltip"
          style={{
            position: 'absolute',
            bottom: 8,
            right: 8,
            zIndex: 2,
            maxWidth: 360,
            background: '#1d2436',
            border: '1px solid #2b3348',
            borderRadius: 4,
            color: '#c9d1e0',
            fontSize: 11,
            padding: '4px 8px',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap'
          }}
        >
          {urlLabel(hoverAttributes.url)} · depth {hoverAttributes.depth}
          {hoverAttributes.statusClass ? ` · ${hoverAttributes.statusClass}` : ''}
          {hoverAttributes.equityPercentile !== null ? ` · equity p${Math.round(hoverAttributes.equityPercentile)}` : ''}
        </div>
      ) : null}

      {selectedAttributes ? (
        <div data-testid="cf-linkgraph-selected" style={{ position: 'absolute', bottom: 8, left: '50%', transform: 'translateX(-50%)', zIndex: 2, color: '#8892a8', fontSize: 11 }}>
          Selected: {selectedAttributes.url}
        </div>
      ) : null}

      <div ref={containerRef} data-testid="cf-linkgraph-canvas" style={{ width: '100%', height: '100%' }} />
    </div>
  );
}

const controlStyle: CSSProperties = {
  background: '#1d2436',
  color: '#aab4c8',
  border: '1px solid #2b3348',
  borderRadius: 4,
  fontSize: 11,
  padding: '4px 8px',
  cursor: 'pointer'
};
