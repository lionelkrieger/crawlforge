/**
 * Dashboard aggregate loaders (FS-CRAWL-002 §8.4: charts render ONLY from
 * precomputed local aggregates — metric_summaries-backed chart series,
 * dashboard summary counts, bounded explorer windows). Every call goes
 * through the typed loopback client; every failure degrades to null so the
 * dashboards stay usable offline. No raw-row recomputation ever happens
 * client-side.
 *
 * @module surfaces/dashboards/dashboardData
 */

import { useEffect, useState } from 'react';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type {
  AuditDashboardSummary,
  AuditScores,
  ChartPoint,
  GraphData
} from '../../contracts/api/audits';
import type { HostState } from '../../contracts/api/hosts';
import type { WafBlocksSummary } from '../../contracts/api/waf';
import type { NgramRow, TopicalGap } from '../../contracts/api/ngrams';
import type { LogCorrelationResponse } from '../../contracts/api/logs';
import type { LlmsTxtValidation, BotGovernanceRow } from '../../contracts/api/geo';
import type { AnalyticsResponse, ContainerCoverage } from '../../contracts/api/analytics';
import type { LinkOpportunity, SemanticCluster } from '../../contracts/api/semantics';

/** The aggregate bundle for the audit dashboards (null = unavailable offline). */
export interface DashboardAggregates {
  readonly summary: AuditDashboardSummary | null;
  readonly scores: AuditScores | null;
  readonly scoreTrend: readonly ChartPoint[] | null;
  readonly waf: WafBlocksSummary | null;
  readonly hosts: readonly HostState[] | null;
  readonly ngrams: readonly NgramRow[] | null;
  readonly gaps: readonly TopicalGap[] | null;
  readonly logCorrelation: LogCorrelationResponse | null;
  readonly logTimeline: readonly ChartPoint[] | null;
  readonly llms: readonly LlmsTxtValidation[] | null;
  readonly governance: readonly BotGovernanceRow[] | null;
  readonly analytics: AnalyticsResponse | null;
  readonly analyticsCoverage: readonly ContainerCoverage[] | null;
}

/** Empty aggregate bundle (offline / pre-load state). */
export const EMPTY_AGGREGATES: DashboardAggregates = {
  summary: null,
  scores: null,
  scoreTrend: null,
  waf: null,
  hosts: null,
  ngrams: null,
  gaps: null,
  logCorrelation: null,
  logTimeline: null,
  llms: null,
  governance: null,
  analytics: null,
  analyticsCoverage: null
};

/** Degrades any loader failure to null (offline-first, never throws). */
async function safe<T>(load: () => Promise<T>): Promise<T | null> {
  try {
    return await load();
  } catch {
    return null;
  }
}

/** Loads the full dashboard aggregate bundle for one audit (bounded calls). */
export async function loadDashboardAggregates(
  api: LoopbackApiClient,
  auditId: string,
  signal?: AbortSignal
): Promise<DashboardAggregates> {
  const params = { params: { id: auditId } } as const;
  const [
    summary,
    scores,
    scoreTrend,
    waf,
    hosts,
    ngrams,
    gaps,
    logCorrelation,
    logTimeline,
    llms,
    governance,
    analytics,
    analyticsCoverage
  ] = await Promise.all([
    safe(async () => (await api.request('audits.summary', { ...params, signal })).data),
    safe(async () => (await api.request('audits.scores', { ...params, signal })).data),
    safe(async () =>
      (await api.request('audits.chartSeries', {
        params: { id: auditId, metric: 'score-trend' },
        signal
      })).data?.points ?? null
    ),
    safe(async () => (await api.request('waf.blocksSummary', { ...params, signal })).data),
    safe(async () =>
      (await api.request('hosts.list', {
        ...params,
        query: { includeHistory: true, offset: 0, limit: 100 },
        signal
      })).data
    ),
    safe(async () =>
      (await api.request('ngrams.list', {
        ...params,
        query: { offset: 0, limit: 50 },
        signal
      })).data
    ),
    safe(async () =>
      (await api.request('ngrams.gaps', {
        ...params,
        query: { offset: 0, limit: 20 },
        signal
      })).data
    ),
    safe(async () => (await api.request('logs.correlation', { ...params, query: { offset: 0, limit: 1 }, signal })).data),
    safe(async () =>
      (await api.request('audits.chartSeries', {
        params: { id: auditId, metric: 'log-hits' },
        signal
      })).data?.points ?? null
    ),
    safe(async () =>
      (await api.request('geo.llmsTxt', { ...params, query: { offset: 0, limit: 100 }, signal })).data
    ),
    safe(async () =>
      (await api.request('geo.governance', { ...params, query: { offset: 0, limit: 200 }, signal })).data
    ),
    safe(async () => (await api.request('analytics.list', { ...params, query: { view: 'tags' }, signal })).data),
    safe(async () =>
      (await api.request('analytics.coverage', { ...params, query: { offset: 0, limit: 50 }, signal })).data
    )
  ]);

  return {
    summary,
    scores,
    scoreTrend,
    waf,
    hosts,
    ngrams,
    gaps,
    logCorrelation,
    logTimeline,
    llms,
    governance,
    analytics,
    analyticsCoverage
  };
}

/** Visualization aggregates: bounded link graph + semantic overlays. */
export interface VisualizationAggregates {
  readonly linkGraph: GraphData | null;
  readonly clusters: readonly SemanticCluster[] | null;
  readonly opportunities: readonly LinkOpportunity[] | null;
}

/** Loads the visualization bundle (LOD-bounded graph request). */
export async function loadVisualizationAggregates(
  api: LoopbackApiClient,
  auditId: string,
  signal?: AbortSignal
): Promise<VisualizationAggregates> {
  const params = { params: { id: auditId } } as const;
  const [linkGraph, clusters, opportunities] = await Promise.all([
    safe(async () =>
      (await api.request('audits.graph', {
        ...params,
        query: { type: 'link-network', maxDepth: 8, maxNodes: 20_000, maxEdges: 60_000 },
        signal
      })).data
    ),
    safe(async () =>
      (await api.request('semantics.clusters', { ...params, query: { offset: 0, limit: 100 }, signal })).data
    ),
    safe(async () =>
      (await api.request('semantics.linkOpportunities', { ...params, query: { offset: 0, limit: 200 }, signal })).data
    )
  ]);
  return { linkGraph, clusters, opportunities };
}

/** Hook state for aggregate loading. */
export interface AggregatesState<T> {
  readonly aggregates: T;
  readonly loading: boolean;
}

/** Loads dashboard aggregates on mount / audit change (cancellable). */
export function useDashboardAggregates(
  api: LoopbackApiClient | null,
  auditId: string | null
): AggregatesState<DashboardAggregates> {
  const [state, setState] = useState<AggregatesState<DashboardAggregates>>({
    aggregates: EMPTY_AGGREGATES,
    loading: false
  });

  useEffect(() => {
    if (!api || !auditId) {
      setState({ aggregates: EMPTY_AGGREGATES, loading: false });
      return;
    }
    const controller = new AbortController();
    setState((current) => ({ ...current, loading: true }));
    loadDashboardAggregates(api, auditId, controller.signal)
      .then((aggregates) => {
        if (!controller.signal.aborted) setState({ aggregates, loading: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ aggregates: EMPTY_AGGREGATES, loading: false });
      });
    return () => controller.abort();
  }, [api, auditId]);

  return state;
}

/** Loads visualization aggregates on mount / audit change (cancellable). */
export function useVisualizationAggregates(
  api: LoopbackApiClient | null,
  auditId: string | null,
  refreshKey = 0
): AggregatesState<VisualizationAggregates> {
  const [state, setState] = useState<AggregatesState<VisualizationAggregates>>({
    aggregates: { linkGraph: null, clusters: null, opportunities: null },
    loading: false
  });

  useEffect(() => {
    if (!api || !auditId) {
      setState({ aggregates: { linkGraph: null, clusters: null, opportunities: null }, loading: false });
      return;
    }
    const controller = new AbortController();
    setState((current) => ({ ...current, loading: true }));
    loadVisualizationAggregates(api, auditId, controller.signal)
      .then((aggregates) => {
        if (!controller.signal.aborted) setState({ aggregates, loading: false });
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          setState({ aggregates: { linkGraph: null, clusters: null, opportunities: null }, loading: false });
        }
      });
    return () => controller.abort();
  }, [api, auditId, refreshKey]);

  return state;
}
