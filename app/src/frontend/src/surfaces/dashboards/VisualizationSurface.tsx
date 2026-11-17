/**
 * Visualization surface (FS-CRAWL-001 §8.2/§8.5, FS-CRAWL-002 §8.5/§12.9):
 * force-directed internal link graph + semantic similarity network with
 * weighted/unweighted equity toggle (dom_zone weights), status/depth color
 * modes, opportunity overlay, LOD honesty banner, and drill-down. Data is
 * loaded as bounded aggregates through the typed loopback client; all
 * rendering sits behind injected ports (jsdom-safe).
 *
 * @module surfaces/dashboards/VisualizationSurface
 */

import { useMemo, useState, type CSSProperties } from 'react';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type { GraphRendererFactory } from '../../graphs/graphPorts';
import type { LayoutWorkerFactory } from '../../graphs/layoutClient';
import { LinkGraphSurface } from '../../graphs/LinkGraphSurface';
import { SemanticGraphSurface } from '../../graphs/SemanticGraphSurface';
import { useVisualizationAggregates } from './dashboardData';

/** Visualization surface props. */
export interface VisualizationSurfaceProps {
  readonly api: LoopbackApiClient | null;
  readonly auditId: string | null;
  readonly rendererFactory?: GraphRendererFactory;
  readonly workerFactory?: LayoutWorkerFactory | null;
  readonly onNodeSelect?: (nodeId: string, url: string) => void;
}

const controlStyle: CSSProperties = {
  background: '#1d2436',
  color: '#aab4c8',
  border: '1px solid #2b3348',
  borderRadius: 4,
  fontSize: 12,
  padding: '4px 8px',
  cursor: 'pointer'
};

const labelStyle: CSSProperties = {
  color: '#aab4c8',
  fontSize: 12,
  display: 'flex',
  gap: 6,
  alignItems: 'center'
};

/** Link + semantic network visualization with a live control workbench. */
export function VisualizationSurface({
  api,
  auditId,
  rendererFactory,
  workerFactory,
  onNodeSelect
}: VisualizationSurfaceProps) {
  const [reloadKey, setReloadKey] = useState(0);
  const { aggregates, loading } = useVisualizationAggregates(api, auditId, reloadKey);
  const [weighted, setWeighted] = useState(true);
  const [colorMode, setColorMode] = useState<'status' | 'depth'>('status');
  const [showOpportunities, setShowOpportunities] = useState(true);
  const [labelThreshold, setLabelThreshold] = useState(8);
  const [iterations, setIterations] = useState(200);
  const [nodeBudget, setNodeBudget] = useState(20_000);
  const [drillRadius, setDrillRadius] = useState(1);

  const budget = useMemo(
    () => ({ maxNodes: nodeBudget, maxEdges: nodeBudget * 3 }),
    [nodeBudget]
  );

  return (
    <div data-testid="cf-visualization" style={{ display: 'grid', gap: 12 }}>
      <div
        data-testid="cf-viz-controls"
        style={{
          display: 'flex',
          flexWrap: 'wrap',
          gap: 12,
          alignItems: 'center',
          background: '#171d2c',
          border: '1px solid #2b3348',
          borderRadius: 6,
          padding: '10px 12px'
        }}
      >
        <label style={labelStyle}>
          <input
            type="checkbox"
            data-testid="cf-viz-weighted"
            checked={weighted}
            onChange={(event) => setWeighted(event.target.checked)}
          />
          Weighted equity (dom_zone weights)
        </label>
        <label style={labelStyle}>
          Color by
          <select
            data-testid="cf-viz-colormode"
            value={colorMode}
            onChange={(event) => setColorMode(event.target.value as 'status' | 'depth')}
            style={controlStyle}
          >
            <option value="status">Status class</option>
            <option value="depth">Crawl depth</option>
          </select>
        </label>
        <label style={labelStyle}>
          <input
            type="checkbox"
            data-testid="cf-viz-opportunities"
            checked={showOpportunities}
            onChange={(event) => setShowOpportunities(event.target.checked)}
          />
          Link opportunities overlay
        </label>
        <label style={labelStyle}>
          Label density
          <input
            type="range"
            min={0}
            max={20}
            step={1}
            data-testid="cf-viz-labels"
            value={labelThreshold}
            onChange={(event) => setLabelThreshold(Number(event.target.value))}
            style={{ accentColor: '#ff8a3d', width: 110 }}
          />
          <span style={{ color: '#66d9e8', minWidth: 18 }}>{labelThreshold}</span>
        </label>
        <label style={labelStyle}>
          Layout iterations
          <input
            type="number"
            min={50}
            max={500}
            step={50}
            data-testid="cf-viz-iterations"
            value={iterations}
            onChange={(event) => {
              const next = Number(event.target.value);
              if (Number.isFinite(next)) setIterations(Math.max(50, Math.min(500, next)));
            }}
            style={{ ...controlStyle, width: 70 }}
          />
        </label>
        <label style={labelStyle}>
          Node budget
          <select
            data-testid="cf-viz-budget"
            value={nodeBudget}
            onChange={(event) => setNodeBudget(Number(event.target.value))}
            style={controlStyle}
          >
            <option value={1000}>1k (fast)</option>
            <option value={5000}>5k</option>
            <option value={20000}>20k (full LOD)</option>
          </select>
        </label>
        <label style={labelStyle}>
          Drill radius
          <select
            data-testid="cf-viz-radius"
            value={drillRadius}
            onChange={(event) => setDrillRadius(Number(event.target.value))}
            style={controlStyle}
          >
            <option value={1}>1 hop</option>
            <option value={2}>2 hops</option>
            <option value={3}>3 hops</option>
          </select>
        </label>
        <button
          type="button"
          data-testid="cf-viz-reload"
          onClick={() => setReloadKey((key) => key + 1)}
          style={controlStyle}
        >
          Reload aggregates
        </button>
        {loading ? (
          <span data-testid="cf-viz-loading" style={{ color: '#66d9e8', fontSize: 12 }}>Loading aggregates…</span>
        ) : null}
      </div>

      <section>
        <h3 style={{ color: '#c9d1e0', fontSize: 15, margin: '4px 0' }}>Internal link network</h3>
        <LinkGraphSurface
          data={aggregates.linkGraph}
          weighted={weighted}
          colorMode={colorMode}
          budget={budget}
          labelThreshold={labelThreshold}
          iterations={iterations}
          drillRadius={drillRadius}
          rendererFactory={rendererFactory}
          workerFactory={workerFactory}
          onNodeSelect={onNodeSelect}
        />
      </section>

      <section>
        <h3 style={{ color: '#c9d1e0', fontSize: 15, margin: '4px 0' }}>Semantic similarity network</h3>
        <SemanticGraphSurface
          clusters={aggregates.clusters ?? []}
          opportunities={aggregates.opportunities ?? []}
          showOpportunities={showOpportunities}
          labelThreshold={labelThreshold}
          iterations={iterations}
          rendererFactory={rendererFactory}
          workerFactory={workerFactory}
          onNodeSelect={onNodeSelect}
        />
      </section>
      {auditId === null ? (
        <div data-testid="cf-viz-noaudit" style={{ color: '#8892a8', fontSize: 12 }}>
          Select an audit to visualize its networks.
        </div>
      ) : null}
    </div>
  );
}
