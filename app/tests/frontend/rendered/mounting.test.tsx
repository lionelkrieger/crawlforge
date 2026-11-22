// @vitest-environment jsdom
/**
 * Mounting proofs (WP-10-04 mount-A, coordinator follow-ups) — UI-13 / UI-16 /
 * UI-10 reachability through the surfaces they are mounted in.
 *
 * Acceptance stand-ins (the phase gate drives these testids over CDP):
 *  - UI-13: the URL detail panel's "Rendered vs raw" section (root
 *    `url-detail-rendered`) mounts the accepted RenderedTab and shows the
 *    late-meta dual-evidence title diff — raw 'Raw title before script' vs
 *    rendered 'Rendered title after script';
 *  - UI-16: the report nav entry "Rendered comparison" (`report-nav-rendered`)
 *    mounts RenderedComparisonSurface, which lists BOTH the late-meta and
 *    js-content pages;
 *  - UI-16 drill-down: activating a comparison row opens the URL detail for
 *    THAT URL in the urls pane (UrlExplorer `openUrlId` → the same openDetail
 *    path row activation uses) — `url-detail-rendered` is visible for it;
 *  - UI-10: the run view shows `capture-mode-indicator` with the audit
 *    configuration's capture mode once `configurations.get` loads (the audit
 *    wire has no frozen configuration snapshot), and NO indicator at all when
 *    the configuration cannot be read — never a possibly-wrong mode.
 *
 * Fixtures and fake routes come from this folder's helpers (the WP-10-02
 * comparer evidence shapes); every fetch runs through the real typed loopback
 * client over real wire JSON.
 */

import { afterEach, describe, expect, it } from 'vitest';
import { ReportView } from '../../../src/frontend/src/shell/AuditViews';
import { UrlDetailPanel } from '../../../src/frontend/src/surfaces/explorers/urls/UrlDetailPanel';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import type { ShellServices } from '../../../src/frontend/src/shell/ShellContext';
import { upsertEntities } from '../../../src/frontend/src/store/entityTable';
import { renderTool, fakeServices, waitFor } from '../tools/helpers';
import {
  AUDIT,
  renderedClient,
  renderedRoutes,
  lateMetaDetail,
  installGridStubs,
  primeGrid,
  flush,
  byTestId,
  textOf,
  click,
  ok,
  err,
  type FetchCall
} from './helpers';

afterEach(() => {
  document.body.replaceChildren();
  localStorage.clear();
});

/** Seeds the store with one active audit — the run view's data source. */
function seedActiveAudit(services: ShellServices): void {
  const audit: AuditSummary = {
    id: AUDIT,
    projectId: '1',
    configurationId: '1',
    state: 'running',
    phase: 'fetching',
    startedAt: '2026-11-14T09:00:00Z',
    completedAt: null,
    counters: { queued: 20, fetched: 80, error: 2, blocked: 1, wafBlocked: 0 }
  };
  services.store.setState((state) => ({
    audits: {
      entities: upsertEntities(state.audits.entities, [audit]),
      activeAuditId: AUDIT,
      listPage: {
        key: '{}',
        offset: 0,
        limit: 50,
        total: 1,
        items: [audit],
        fetchedAt: '2026-11-14T12:00:00Z'
      }
    }
  }));
}

/**
 * Mounts the report frame over the rendered fixture wire. Extra routes ride
 * after the rendered ones (fakeFetch matches by substring in insertion order);
 * by default `configurations.get` is absent, so the capture-mode read fails.
 */
async function mountReport(extraRoutes: Record<string, (call: FetchCall) => unknown> = {}) {
  installGridStubs();
  const { services, calls } = fakeServices({ ...renderedRoutes(), ...extraRoutes });
  seedActiveAudit(services);
  const view = await renderTool(<ReportView />, services);
  await flush(20);
  return { view, calls };
}

describe('mounting — UI-13 URL detail rendered section', () => {
  it('shows the rendered tab with the late-meta raw vs rendered title diff', async () => {
    const { api } = renderedClient();
    const view = await renderTool(
      <UrlDetailPanel detail={lateMetaDetail()} auditId={AUDIT} api={api} />,
      fakeServices().services
    );
    await flush(30);

    const section = byTestId(view.container, 'url-detail-rendered');
    expect(section).not.toBeNull();
    expect(textOf(section)).toContain('Rendered vs raw');

    // Dual-evidence title row from the WP-10-02 comparer evidence.
    expect(byTestId(view.container, 'rendered-diff-field-title')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'rendered-diff-title-raw'))).toBe('Raw title before script');
    expect(textOf(byTestId(view.container, 'rendered-diff-title-rendered'))).toBe('Rendered title after script');
    view.unmount();
  });
});

describe('mounting — UI-16 rendered comparison report pane', () => {
  it('report-nav-rendered lists the late-meta and js-content pages', async () => {
    const { view } = await mountReport();
    expect(byTestId(view.container, 'report-nav-rendered')).not.toBeNull();

    click(byTestId(view.container, 'report-nav-rendered')!);
    await flush(30);
    await primeGrid(view.container);
    await flush(10);

    expect(byTestId(view.container, 'rendered-comparison')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-late-meta')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-js-content')).not.toBeNull();
    view.unmount();
  });

  it('activating a comparison row opens URL detail for that URL (rendered section visible)', async () => {
    const { view } = await mountReport();
    click(byTestId(view.container, 'report-nav-rendered')!);
    await flush(30);
    await primeGrid(view.container);
    await flush(10);

    // Row 0 is the severity-sorted first page (late-meta); the URL column is
    // column 1 (severity, canonicalUrl, captureSource, changedFields, count).
    const cell = view.container.querySelector('[data-cf-row="0"][data-cf-col="1"]');
    expect(cell).not.toBeNull();
    click(cell!);

    // The urls pane opens its detail drawer for the clicked URL through the
    // same openDetail path row activation uses (UrlExplorer openUrlId).
    await waitFor(() => byTestId(view.container, 'rendered-tab') !== null);
    expect(byTestId(view.container, 'url-explorer')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison')).toBeNull();
    expect(byTestId(view.container, 'url-explorer-detail')).not.toBeNull();
    expect(byTestId(view.container, 'url-detail-rendered')).not.toBeNull();
    expect(textOf(byTestId(view.container, 'url-detail-url'))).toContain('/pages/late-meta.html');
    expect(byTestId(view.container, 'rendered-tab')!.getAttribute('data-url-id')).toBe('url-late-meta');
    view.unmount();
  });
});

describe('mounting — UI-10 capture-mode indicator', () => {
  it('shows the configuration capture mode once the configuration loads (dual)', async () => {
    const { view } = await mountReport({
      '/configurations/': () => ok({ id: '1', projectId: '1', name: 'Fixture configuration', captureMode: 'dual' })
    });
    await flush(20);

    const indicator = byTestId(view.container, 'capture-mode-indicator');
    expect(indicator).not.toBeNull();
    expect(indicator!.getAttribute('data-mode')).toBe('dual');
    expect(textOf(byTestId(view.container, 'capture-mode-indicator-value'))).toBe('Dual (raw + rendered)');
    view.unmount();
  });

  it('renders no indicator when the configuration cannot be read', async () => {
    const { view, calls } = await mountReport({
      '/configurations/': () => err('not-found', 'Configuration unavailable')
    });
    await flush(20);

    // The read was attempted and failed — no indicator at all (never a
    // possibly-wrong mode, never the baseline label for an unknown mode).
    expect(calls.some((call) => call.url.includes('/configurations/'))).toBe(true);
    expect(byTestId(view.container, 'capture-mode-indicator')).toBeNull();
    view.unmount();
  });
});
