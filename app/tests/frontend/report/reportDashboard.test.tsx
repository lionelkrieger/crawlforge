// @vitest-environment jsdom
/**
 * ReportDashboard component proofs (WP-09-08, UI-11): real wire counters on
 * the locked keys, category/overall scores, severity/category distributions
 * through the accepted chart surfaces, crawl-health rollup, bounded top
 * issues with drill-down handoff, and honest empty/loading/error states.
 * Charts run through the mock renderer port — jsdom never touches canvas.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { ReportDashboard } from '../../../src/frontend/src/surfaces/dashboards/ReportDashboard';
import {
  AUDIT,
  reportClient,
  render,
  flush,
  byTestId,
  click,
  createMockChartFactory,
  callsTo,
  scoresBefore,
  auditDetailFixture,
  sampleFindings
} from './helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('ReportDashboard — real data (UI-11)', () => {
  it('renders the dashboard-charts root with real crawl counters on the wire keys', async () => {
    const { api, calls } = reportClient();
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);

    expect(byTestId(view.container, 'dashboard-charts')).not.toBeNull();
    expect(byTestId(view.container, 'dashboard-counter-queued')!.textContent).toBe('0');
    expect(byTestId(view.container, 'dashboard-counter-fetched')!.textContent).toBe('115');
    expect(byTestId(view.container, 'dashboard-counter-error')!.textContent).toBe('5');
    expect(byTestId(view.container, 'dashboard-counter-blocked')!.textContent).toBe('3');
    expect(byTestId(view.container, 'dashboard-counter-wafBlocked')!.textContent).toBe('2');
    // Counters come from the real audit record route.
    expect(callsTo(calls, `/audits/${AUDIT}`).some((call) => !call.url.includes('/findings'))).toBe(true);
    view.unmount();
  });

  it('prefers the live counter override (liveProgress store handoff)', async () => {
    const { api } = reportClient();
    const live = { queued: 9, fetched: 40, error: 1, blocked: 0, wafBlocked: 1 };
    const view = await render(
      <ReportDashboard api={api} auditId={AUDIT} liveCounters={live} factory={createMockChartFactory().factory} />
    );
    await flush(20);
    expect(byTestId(view.container, 'dashboard-counter-queued')!.textContent).toBe('9');
    expect(byTestId(view.container, 'dashboard-counter-fetched')!.textContent).toBe('40');
    expect(byTestId(view.container, 'dashboard-counter-wafBlocked')!.textContent).toBe('1');
    view.unmount();
  });

  it('renders overall + category scores from the scores route', async () => {
    const { api, calls } = reportClient();
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);

    expect(byTestId(view.container, 'dashboard-score-overall')!.textContent).toContain('72');
    expect(byTestId(view.container, 'dashboard-score-structureddata')!.textContent).toBe('60');
    expect(byTestId(view.container, 'dashboard-score-content')!.textContent).toBe('80');
    expect(byTestId(view.container, 'dashboard-score-indexability')!.textContent).toBe('70');
    expect(callsTo(calls, '/scores').length).toBeGreaterThan(0);
    view.unmount();
  });

  it('renders severity/category/status distributions through the chart port and bridges cross-filter clicks', async () => {
    const { factory, instances } = createMockChartFactory();
    const onCrossFilter = vi.fn();
    const { api } = reportClient();
    const view = await render(
      <ReportDashboard api={api} auditId={AUDIT} factory={factory} onCrossFilter={onCrossFilter} />
    );
    await flush(20);

    expect(byTestId(view.container, 'cf-dashboard')).not.toBeNull();
    // AuditDashboard: status/severity/category/crawl-health; CrawlHealthPanel: hosts/waf.
    expect(instances.length).toBeGreaterThanOrEqual(6);
    expect(instances.every((instance) => instance.options.length > 0)).toBe(true);
    // Severity distribution is the second AuditDashboard chart.
    instances[1].emitClick({ name: 'error' } as never);
    expect(onCrossFilter).toHaveBeenCalledWith({ kind: 'severity', severity: 'error' });
    view.unmount();
    // Chart lifecycle hygiene: every renderer disposed on unmount.
    expect(instances.every((instance) => instance.disposeCount === 1)).toBe(true);
  });

  it('renders the crawl-health rollup from the summary wire', async () => {
    const { api } = reportClient();
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);
    const health = byTestId(view.container, 'dashboard-crawl-health')!;
    expect(health.textContent).toContain('Total URLs 120');
    expect(health.textContent).toContain('Fetched 115');
    expect(health.textContent).toContain('Errors 5');
    expect(health.textContent).toContain('Blocked 3');
    expect(health.textContent).toContain('143 ms');
    view.unmount();
  });

  it('lists the bounded top-issues window and hands URL drill-down to onOpenUrl', async () => {
    const onOpenUrl = vi.fn();
    const { api, calls } = reportClient();
    const view = await render(
      <ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} onOpenUrl={onOpenUrl} />
    );
    await flush(20);

    expect(byTestId(view.container, 'dashboard-top-issues')).not.toBeNull();
    for (const finding of sampleFindings) {
      expect(byTestId(view.container, `dashboard-top-issue-${finding.id}`)).not.toBeNull();
    }
    // The window request is bounded and severity-sorted.
    const findingsCall = callsTo(calls, '/findings')[0];
    expect(findingsCall.url).toContain('limit=10');
    expect(findingsCall.url).toContain('sort=severity');
    click(byTestId(view.container, 'dashboard-top-issue-open-f-sd-1')!);
    expect(onOpenUrl).toHaveBeenCalledWith('7');
    view.unmount();
  });
});

describe('ReportDashboard — empty / loading / error states', () => {
  it('shows the no-audit state without an audit selection', async () => {
    const { api } = reportClient();
    const view = await render(<ReportDashboard api={api} auditId={null} />);
    await flush();
    expect(byTestId(view.container, 'dashboard-charts')).not.toBeNull();
    expect(byTestId(view.container, 'dashboard-no-audit')).not.toBeNull();
    view.unmount();
  });

  it('shows the offline state without a backend client', async () => {
    const view = await render(<ReportDashboard api={null} auditId={AUDIT} />);
    await flush();
    expect(byTestId(view.container, 'dashboard-offline')).not.toBeNull();
    view.unmount();
  });

  it('shows the loading state while aggregates are in flight', async () => {
    const { api } = reportClient({ findingsDelayMs: 60 });
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush();
    expect(byTestId(view.container, 'dashboard-loading')).not.toBeNull();
    await flush(120);
    expect(byTestId(view.container, 'dashboard-loading')).toBeNull();
    expect(byTestId(view.container, 'dashboard-counter-fetched')!.textContent).toBe('115');
    view.unmount();
  });

  it('surfaces an envelope error from the audit record route', async () => {
    const { api } = reportClient({ detail: null });
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);
    const error = byTestId(view.container, 'dashboard-error')!;
    expect(error.textContent).toContain('No audit');
    expect(byTestId(view.container, 'dashboard-counters-unavailable')).not.toBeNull();
    view.unmount();
  });

  it('degrades a missing summary to the accepted empty dashboard (no fake frames)', async () => {
    const { api } = reportClient({ summary: null });
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);
    expect(byTestId(view.container, 'cf-dashboard-empty')).not.toBeNull();
    // Scores still render from their own route.
    expect(byTestId(view.container, 'dashboard-score-overall')!.textContent).toContain(String(scoresBefore.overall));
    view.unmount();
  });

  it('shows the clean-audit state when the findings window is empty', async () => {
    const { api } = reportClient({ findings: [] });
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={createMockChartFactory().factory} />);
    await flush(20);
    expect(byTestId(view.container, 'dashboard-top-issues-empty')).not.toBeNull();
    view.unmount();
  });

  it('reloads aggregates when refreshKey changes (suppression handoff)', async () => {
    const { api, calls } = reportClient();
    const { factory } = createMockChartFactory();
    const view = await render(<ReportDashboard api={api} auditId={AUDIT} factory={factory} refreshKey={0} />);
    await flush(20);
    const before = callsTo(calls, '/summary').length;
    view.rerender(<ReportDashboard api={api} auditId={AUDIT} factory={factory} refreshKey={1} />);
    await flush(20);
    expect(callsTo(calls, '/summary').length).toBeGreaterThan(before);
    expect(auditDetailFixture.counters.fetched).toBe(115);
    view.unmount();
  });
});
