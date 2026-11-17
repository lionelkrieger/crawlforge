// @vitest-environment jsdom
/**
 * Dashboards surface suite (jsdom, mocked renderer ports + fake fetch):
 * aggregates-only loading per surface, offline degradation to honest empty
 * states (never raw-row recompute), cross-filter dispatch from chart
 * clicks, and the live visualization control workbench (weighted/overlay
 * toggles, label density, layout iterations, LOD budget, drill radius,
 * reload) (WP-06-04, FS-CRAWL-002 §8.3–8.5).
 */

import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { AuditDashboard } from '../../../src/frontend/src/surfaces/dashboards/AuditDashboard';
import { CrawlHealthPanel } from '../../../src/frontend/src/surfaces/dashboards/CrawlHealthPanel';
import { ScoreTrendSurface } from '../../../src/frontend/src/surfaces/dashboards/ScoreTrendSurface';
import {
  AnalyticsCoveragePanel,
  GeoCoveragePanel,
  LogIntelligencePanel,
  NgramTopicalPanel
} from '../../../src/frontend/src/surfaces/dashboards/IntelligenceDashboards';
import { VisualizationSurface } from '../../../src/frontend/src/surfaces/dashboards/VisualizationSurface';
import {
  loadDashboardAggregates,
  loadVisualizationAggregates
} from '../../../src/frontend/src/surfaces/dashboards/dashboardData';
import type { ChartSelection } from '../../../src/frontend/src/charts/crossFilter';
import { statusColors, depthColor } from '../../../src/frontend/src/charts/chartTheme';
import {
  byTestId,
  changeValue,
  click,
  clustersFixture,
  consentFixture,
  coverageFixture,
  createFakeWorkerFactory,
  createMockChartFactory,
  createMockGraphFactory,
  fakeApiClient,
  flush,
  gapsFixture,
  governanceFixture,
  graphDataFixture,
  hostsFixture,
  llmsFixture,
  logCorrelationFixture,
  ngramRowsFixture,
  ok,
  opportunitiesFixture,
  pointsFixture,
  render,
  scoresFixture,
  summaryFixture,
  wafFixture,
  type MockGraphInstance
} from './helpers';

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  document.body.replaceChildren();
});

/** Full aggregate route table (specific fragments before general ones). */
function aggregateRoutes() {
  return {
    '/waf-blocks/summary': () => ok(wafFixture),
    '/summary': () => ok(summaryFixture),
    '/scores': () => ok(scoresFixture),
    '/charts/score-trend': () => ok({ metric: 'score-trend', points: pointsFixture }),
    '/charts/log-hits': () => ok({ metric: 'log-hits', points: pointsFixture }),
    '/hosts': () => ok(hostsFixture, { offset: 0, limit: 100, total: 2 }),
    '/ngrams/gaps': () => ok(gapsFixture, { offset: 0, limit: 20, total: 2 }),
    '/ngrams': () => ok(ngramRowsFixture, { offset: 0, limit: 50, total: 4 }),
    '/logs/correlation': () => ok({ summary: logCorrelationFixture, rows: [] }),
    '/geo/llms-txt': () => ok(llmsFixture, { offset: 0, limit: 100, total: 3 }),
    '/geo/governance': () => ok(governanceFixture, { offset: 0, limit: 200, total: 3 }),
    '/analytics/coverage': () => ok(coverageFixture, { offset: 0, limit: 50, total: 2 }),
    '/analytics': () => ok({ tags: [], consent: consentFixture, findings: [] })
  };
}

describe('aggregate loaders (offline-from-aggregates invariant)', () => {
  it('loads every dashboard family from aggregate endpoints only', async () => {
    const { api, calls } = fakeApiClient(aggregateRoutes());
    const aggregates = await loadDashboardAggregates(api, 'a1');
    expect(aggregates.summary).toEqual(summaryFixture);
    expect(aggregates.scores).toEqual(scoresFixture);
    expect(aggregates.scoreTrend).toEqual(pointsFixture);
    expect(aggregates.waf).toEqual(wafFixture);
    expect(aggregates.hosts).toEqual(hostsFixture);
    expect(aggregates.ngrams).toEqual(ngramRowsFixture);
    expect(aggregates.gaps).toEqual(gapsFixture);
    expect(aggregates.logCorrelation?.summary).toEqual(logCorrelationFixture);
    expect(aggregates.logTimeline).toEqual(pointsFixture);
    expect(aggregates.llms).toEqual(llmsFixture);
    expect(aggregates.governance).toEqual(governanceFixture);
    expect(aggregates.analytics?.consent).toEqual(consentFixture);
    expect(aggregates.analyticsCoverage).toEqual(coverageFixture);
    // Never touches raw-row explorer endpoints — aggregates only.
    expect(calls.some((call) => /\/(urls|findings|links)(\?|$)/.test(call.url))).toBe(false);
    expect(calls.every((call) => call.url.startsWith('http://127.0.0.1:5000/api/'))).toBe(true);
  });

  it('degrades every family to null when the backend is offline', async () => {
    const { api } = fakeApiClient({});
    const aggregates = await loadDashboardAggregates(api, 'a1');
    for (const value of Object.values(aggregates)) {
      expect(value).toBeNull();
    }
  });

  it('loads the visualization bundle and degrades offline', async () => {
    const { api, calls } = fakeApiClient({
      '/graph': () => ok(graphDataFixture),
      '/semantics/clusters': () => ok(clustersFixture, { offset: 0, limit: 100, total: 2 }),
      '/semantics/link-opportunities': () => ok(opportunitiesFixture, { offset: 0, limit: 200, total: 1 })
    });
    const aggregates = await loadVisualizationAggregates(api, 'a1');
    expect(aggregates.linkGraph).toEqual(graphDataFixture);
    expect(aggregates.clusters).toEqual(clustersFixture);
    expect(aggregates.opportunities).toEqual(opportunitiesFixture);
    const graphCall = calls.find((call) => call.url.includes('/graph'));
    expect(graphCall?.url).toContain('maxNodes=20000');
    expect(graphCall?.url).toContain('type=link-network');

    const offline = await loadVisualizationAggregates(fakeApiClient({}).api, 'a1');
    expect(offline.linkGraph).toBeNull();
    expect(offline.clusters).toBeNull();
    expect(offline.opportunities).toBeNull();
  });
});

describe('AuditDashboard', () => {
  it('renders summary cards and cross-filters from chart clicks', async () => {
    const charts = createMockChartFactory();
    const selections: ChartSelection[] = [];
    const view = await render(
      <AuditDashboard summary={summaryFixture} factory={charts.factory} onCrossFilter={(selection) => selections.push(selection)} />
    );
    expect(byTestId(view.container, 'cf-card-total')?.textContent).toContain('100');
    expect(byTestId(view.container, 'cf-card-waf')?.textContent).toContain('5.5%');
    expect(charts.instances.length).toBe(4);

    act(() => charts.instances[0].emitClick({ name: '4xx', seriesName: null, dataIndex: 2, value: 7 }));
    act(() => charts.instances[1].emitClick({ name: 'error', seriesName: null, dataIndex: 0, value: 4 }));
    act(() => charts.instances[2].emitClick({ name: 'content', seriesName: null, dataIndex: 0, value: 10 }));
    expect(selections).toEqual([
      { kind: 'status', statusClass: '4xx' },
      { kind: 'severity', severity: 'error' },
      { kind: 'category', category: 'content' }
    ]);
    view.unmount();
    expect(charts.instances.every((instance) => instance.disposeCount === 1)).toBe(true);
  });

  it('cross-filters from the category breakdown list', async () => {
    const charts = createMockChartFactory();
    const selections: ChartSelection[] = [];
    const view = await render(
      <AuditDashboard summary={summaryFixture} factory={charts.factory} onCrossFilter={(selection) => selections.push(selection)} />
    );
    click(byTestId(view.container, 'cf-category-crawl')!);
    expect(selections).toEqual([{ kind: 'category', category: 'crawl' }]);
    view.unmount();
  });

  it('shows the honest offline empty state without aggregates', async () => {
    const view = await render(<AuditDashboard summary={null} />);
    expect(byTestId(view.container, 'cf-dashboard-empty')?.textContent).toContain('offline');
    view.unmount();
  });
});

describe('CrawlHealthPanel / ScoreTrendSurface', () => {
  it('renders host + WAF charts and cross-filters WAF providers', async () => {
    const charts = createMockChartFactory();
    const selections: ChartSelection[] = [];
    const view = await render(
      <CrawlHealthPanel hosts={hostsFixture} waf={wafFixture} factory={charts.factory} onCrossFilter={(selection) => selections.push(selection)} />
    );
    expect(charts.instances.length).toBe(4);
    act(() => charts.instances[2].emitClick({ name: 'cloudflare', seriesName: null, dataIndex: 0, value: 3 }));
    expect(selections).toEqual([{ kind: 'waf-provider', provider: 'cloudflare' }]);
    view.unmount();
  });

  it('degrades to honest empty states without host/WAF data', async () => {
    const view = await render(<CrawlHealthPanel hosts={null} waf={null} />);
    expect(byTestId(view.container, 'cf-hosts-empty')).not.toBeNull();
    expect(byTestId(view.container, 'cf-waf-empty')).not.toBeNull();
    view.unmount();
  });

  it('renders score trend + category scores and degrades without history', async () => {
    const charts = createMockChartFactory();
    const view = await render(<ScoreTrendSurface trend={pointsFixture} scores={scoresFixture} factory={charts.factory} />);
    expect(charts.instances.length).toBe(2);
    view.unmount();
    const empty = await render(<ScoreTrendSurface trend={null} scores={null} />);
    expect(byTestId(empty.container, 'cf-trend-empty')).not.toBeNull();
    expect(byTestId(empty.container, 'cf-scores-empty')).not.toBeNull();
    empty.unmount();
  });
});

describe('intelligence panels', () => {
  it('cross-filters n-gram zones from the treemap', async () => {
    const charts = createMockChartFactory();
    const selections: ChartSelection[] = [];
    const view = await render(
      <NgramTopicalPanel rows={ngramRowsFixture} gaps={gapsFixture} factory={charts.factory} onCrossFilter={(selection) => selections.push(selection)} />
    );
    expect(charts.instances.length).toBe(3);
    // Treemap is the second chart; zone names cross-filter, n-gram names do not.
    act(() => charts.instances[1].emitClick({ name: 'title', seriesName: null, dataIndex: 0, value: 65 }));
    act(() => charts.instances[1].emitClick({ name: 'running shoes', seriesName: null, dataIndex: 0, value: 40 }));
    expect(selections).toEqual([{ kind: 'ngram-zone', zone: 'title' }]);
    view.unmount();
  });

  it('renders log/GEO/analytics panels and their empty states', async () => {
    const charts = createMockChartFactory();
    const view = await render(
      <div>
        <LogIntelligencePanel correlation={logCorrelationFixture} botCounts={{ gptbot: 30, googlebot: 10 }} timeline={pointsFixture} factory={charts.factory} />
        <GeoCoveragePanel llms={llmsFixture} governance={governanceFixture} factory={charts.factory} />
        <AnalyticsCoveragePanel coverage={coverageFixture} consent={consentFixture} factory={charts.factory} />
      </div>
    );
    expect(byTestId(view.container, 'cf-log-panel')).not.toBeNull();
    expect(byTestId(view.container, 'cf-geo-panel')).not.toBeNull();
    expect(byTestId(view.container, 'cf-analytics-panel')).not.toBeNull();
    expect(charts.instances.length).toBe(7);
    view.unmount();

    const empty = await render(
      <div>
        <LogIntelligencePanel correlation={null} botCounts={null} timeline={null} />
        <GeoCoveragePanel llms={null} governance={null} />
        <AnalyticsCoveragePanel coverage={null} consent={null} />
        <NgramTopicalPanel rows={null} gaps={null} />
      </div>
    );
    expect(byTestId(empty.container, 'cf-logs-empty')).not.toBeNull();
    expect(byTestId(empty.container, 'cf-geo-empty')).not.toBeNull();
    expect(byTestId(empty.container, 'cf-analytics-empty')).not.toBeNull();
    expect(byTestId(empty.container, 'cf-ngrams-empty')).not.toBeNull();
    empty.unmount();
  });
});

describe('VisualizationSurface control workbench', () => {
  function vizRoutes() {
    return {
      '/graph': () => ok(graphDataFixture),
      '/semantics/clusters': () => ok(clustersFixture, { offset: 0, limit: 100, total: 2 }),
      '/semantics/link-opportunities': () => ok(opportunitiesFixture, { offset: 0, limit: 200, total: 1 })
    };
  }

  const linkInstances = (instances: MockGraphInstance[]) =>
    instances.filter((instance) => instance.container.dataset.testid === 'cf-linkgraph-canvas');
  const semInstances = (instances: MockGraphInstance[]) =>
    instances.filter((instance) => instance.container.dataset.testid === 'cf-semgraph-canvas');

  it('loads aggregates and mounts both networks with all eight controls visible', async () => {
    const { api } = fakeApiClient(vizRoutes());
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const view = await render(
      <VisualizationSurface api={api} auditId="a1" rendererFactory={renderer.factory} workerFactory={worker.factory} />
    );
    await flush();
    for (const id of ['cf-viz-weighted', 'cf-viz-colormode', 'cf-viz-opportunities', 'cf-viz-labels', 'cf-viz-iterations', 'cf-viz-budget', 'cf-viz-radius', 'cf-viz-reload']) {
      expect(byTestId(view.container, id)).not.toBeNull();
    }
    expect(linkInstances(renderer.instances).length).toBe(1);
    expect(semInstances(renderer.instances).length).toBe(1);
    expect(byTestId(view.container, 'cf-linkgraph-banner')?.textContent).toContain('Showing 5 of 500,000');
    view.unmount();
    expect(renderer.instances.every((instance) => instance.killCount === 1)).toBe(true);
    expect(worker.workers.every((entry) => entry.terminateCount === 1)).toBe(true);
  });

  it('weighted toggle rebuilds with unweighted edges; color mode recolors', async () => {
    const { api } = fakeApiClient(vizRoutes());
    const renderer = createMockGraphFactory();
    const view = await render(
      <VisualizationSurface api={api} auditId="a1" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    const first = linkInstances(renderer.instances)[0];
    expect(first.graph.getEdgeAttributes('u1', 'u2').weight).toBe(3);
    expect(first.graph.getNodeAttributes('u3').color).toBe(statusColors['4xx']);

    changeValue(byTestId(view.container, 'cf-viz-weighted') as HTMLInputElement, false);
    await flush();
    const unweighted = linkInstances(renderer.instances).at(-1)!;
    expect(unweighted.graph.getEdgeAttributes('u1', 'u2').weight).toBe(1);

    changeValue(byTestId(view.container, 'cf-viz-colormode') as HTMLSelectElement, 'depth');
    await flush();
    const depth = linkInstances(renderer.instances).at(-1)!;
    expect(depth.graph.getNodeAttributes('u5').color).toBe(depthColor(2));
    view.unmount();
  });

  it('label density applies live without remounting; iterations re-run the layout', async () => {
    const { api } = fakeApiClient(vizRoutes());
    const renderer = createMockGraphFactory();
    const worker = createFakeWorkerFactory('compute');
    const view = await render(
      <VisualizationSurface api={api} auditId="a1" rendererFactory={renderer.factory} workerFactory={worker.factory} />
    );
    await flush();
    const countBefore = renderer.instances.length;
    const linkBefore = linkInstances(renderer.instances).at(-1)!;
    expect(linkBefore.settings.labelRenderedSizeThreshold).toBe(8);

    changeValue(byTestId(view.container, 'cf-viz-labels') as HTMLInputElement, '3');
    await flush();
    expect(renderer.instances.length).toBe(countBefore);
    expect(linkBefore.settings.labelRenderedSizeThreshold).toBe(3);
    expect(semInstances(renderer.instances).at(-1)!.settings.labelRenderedSizeThreshold).toBe(3);

    changeValue(byTestId(view.container, 'cf-viz-iterations') as HTMLInputElement, '100');
    await flush();
    expect(renderer.instances.length).toBeGreaterThan(countBefore);
    expect(worker.workers.at(-1)!.requests[0].iterations).toBe(100);
    view.unmount();
  });

  it('node budget caps sampling and drill radius widens the neighborhood', async () => {
    const { api } = fakeApiClient(vizRoutes());
    const renderer = createMockGraphFactory();
    const view = await render(
      <VisualizationSurface api={api} auditId="a1" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    changeValue(byTestId(view.container, 'cf-viz-budget') as HTMLSelectElement, '1000');
    await flush();
    // Fixture has 5 nodes — budget 1000 still fits; verify the banner math holds.
    expect(linkInstances(renderer.instances).at(-1)!.graph.order).toBe(5);

    changeValue(byTestId(view.container, 'cf-viz-radius') as HTMLSelectElement, '2');
    await flush();
    const link = linkInstances(renderer.instances).at(-1)!;
    act(() => link.emitClick('u1'));
    click(byTestId(view.container, 'cf-linkgraph-drill')!);
    await flush();
    const drilled = linkInstances(renderer.instances).at(-1)!;
    // Radius 2 from u1 reaches u4 through u2 — the full 5-node fixture.
    expect(drilled.graph.order).toBe(5);
    expect(byTestId(view.container, 'cf-linkgraph-banner')?.textContent).toContain('Neighborhood of u1');
    view.unmount();
  });

  it('opportunities toggle adds/removes the overlay; reload refetches aggregates', async () => {
    const { api, calls } = fakeApiClient(vizRoutes());
    const renderer = createMockGraphFactory();
    const view = await render(
      <VisualizationSurface api={api} auditId="a1" rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(semInstances(renderer.instances).at(-1)!.graph.hasEdge('u4', 'u2')).toBe(true);
    changeValue(byTestId(view.container, 'cf-viz-opportunities') as HTMLInputElement, false);
    await flush();
    expect(semInstances(renderer.instances).at(-1)!.graph.hasEdge('u4', 'u2')).toBe(false);

    const graphCallsBefore = calls.filter((call) => call.url.includes('/graph')).length;
    click(byTestId(view.container, 'cf-viz-reload')!);
    await flush();
    expect(calls.filter((call) => call.url.includes('/graph')).length).toBe(graphCallsBefore + 1);
    view.unmount();
  });

  it('degrades to honest empty states offline and without an audit', async () => {
    const renderer = createMockGraphFactory();
    const view = await render(
      <VisualizationSurface api={null} auditId={null} rendererFactory={renderer.factory} workerFactory={null} />
    );
    await flush();
    expect(byTestId(view.container, 'cf-viz-noaudit')).not.toBeNull();
    expect(byTestId(view.container, 'cf-linkgraph-empty')).not.toBeNull();
    expect(byTestId(view.container, 'cf-semgraph-empty')).not.toBeNull();
    expect(renderer.instances.length).toBe(0);
    view.unmount();
  });
});
