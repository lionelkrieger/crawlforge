/**
 * Semantic similarity network surface (FS-CRAWL-002 §12.9): force-directed
 * graph of embedding clusters with similarity edges to centroids and an
 * optional internal-link opportunity overlay (violet dashed). Bounded node
 * budget, worker layout, port-injected rendering, strict disposal.
 *
 * @module graphs/SemanticGraphSurface
 */

import { useMemo, useState } from 'react';
import type { LinkOpportunity, SemanticCluster } from '../contracts/api/semantics';
import type { GraphRendererFactory } from './graphPorts';
import type { LayoutWorkerFactory } from './layoutClient';
import { buildSemanticGraph, DEFAULT_SEMANTIC_MAX_NODES, urlLabel } from './graphModel';
import { useGraphRenderer } from './useGraphRenderer';

/** Semantic-graph surface props. */
export interface SemanticGraphSurfaceProps {
  readonly clusters: readonly SemanticCluster[];
  readonly opportunities: readonly LinkOpportunity[];
  readonly showOpportunities: boolean;
  readonly maxNodes?: number;
  /** Live label-density control (sigma labelRenderedSizeThreshold). */
  readonly labelThreshold?: number;
  /** Live ForceAtlas2 iteration budget. */
  readonly iterations?: number;
  readonly rendererFactory?: GraphRendererFactory;
  readonly workerFactory?: LayoutWorkerFactory | null;
  readonly onNodeSelect?: (nodeId: string, url: string) => void;
  readonly height?: number;
}

/** The semantic cluster + opportunity network. */
export function SemanticGraphSurface({
  clusters,
  opportunities,
  showOpportunities,
  maxNodes = DEFAULT_SEMANTIC_MAX_NODES,
  labelThreshold = 8,
  iterations,
  rendererFactory,
  workerFactory,
  onNodeSelect,
  height = 480
}: SemanticGraphSurfaceProps) {
  const [hoverId, setHoverId] = useState<string | null>(null);

  const graph = useMemo(
    () => buildSemanticGraph(clusters, opportunities, { showOpportunities, maxNodes }),
    [clusters, opportunities, showOpportunities, maxNodes]
  );
  const settings = useMemo(
    () => ({ labelRenderedSizeThreshold: labelThreshold }),
    [labelThreshold]
  );

  const { containerRef, layoutState } = useGraphRenderer(graph, {
    rendererFactory,
    workerFactory,
    weighted: true,
    iterations,
    settings,
    onNodeClick: (nodeId) => {
      const node = graph.hasNode(nodeId) ? graph.getNodeAttributes(nodeId) : null;
      if (node) onNodeSelect?.(nodeId, node.url);
    },
    onNodeHover: setHoverId
  });

  const hoverAttributes = hoverId && graph.hasNode(hoverId) ? graph.getNodeAttributes(hoverId) : null;
  const memberCount = graph.order;
  const opportunityCount = graph.filterEdges((_edge, attributes) => attributes.kind === 'opportunity').length;

  if (clusters.length === 0) {
    return (
      <div data-testid="cf-semgraph-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No semantic clusters yet — run the local embedding job first.
      </div>
    );
  }

  return (
    <div data-testid="cf-semgraph" style={{ position: 'relative', width: '100%', height }}>
      <div
        data-testid="cf-semgraph-banner"
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
        {memberCount} pages · {clusters.length} clusters
        {showOpportunities ? ` · ${opportunityCount} link opportunities` : ''}
      </div>

      {layoutState === 'running' ? (
        <div data-testid="cf-semgraph-layout" style={{ position: 'absolute', bottom: 8, left: 8, zIndex: 2, color: '#66d9e8', fontSize: 11 }}>
          Computing layout in worker…
        </div>
      ) : null}

      {hoverAttributes ? (
        <div
          data-testid="cf-semgraph-tooltip"
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
          {urlLabel(hoverAttributes.url)}
          {hoverAttributes.clusterId ? ` · cluster ${hoverAttributes.clusterId}` : ''}
        </div>
      ) : null}

      <div ref={containerRef} data-testid="cf-semgraph-canvas" style={{ width: '100%', height: '100%' }} />
    </div>
  );
}
