// @vitest-environment jsdom
/**
 * IssueExplorer component proofs (WP-09-08, UI-12 / FS-CRAWL-002 §7.1/§8.3):
 *  - virtualization: ≤50 DOM rows at ANY dataset size (10,000-row fixture),
 *    server-windowed totals, mid-dataset scroll keeps the invariant;
 *  - category/rule/status/severity filters hit the locked findings query;
 *  - finding detail + generic evidence drill-down + onOpenUrl handoff;
 *  - suppress/unsuppress against the real suppression wire with synchronous
 *    score recompute deltas and full reversibility; suppressed view.
 * All data flows through the typed loopback client over real wire JSON.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { IssueExplorer } from '../../../src/frontend/src/surfaces/explorers/issues/IssueExplorer';
import {
  AUDIT,
  PROJECT,
  reportClient,
  render,
  flush,
  byTestId,
  click,
  type,
  selectOption,
  installGridStubs,
  primeGrid,
  setScrollGeometry,
  callsTo,
  seedFindings,
  sampleFindings,
  structuredDataInvalid,
  hreflangNoReturn,
  duplicateTitle,
  suppressionFixture,
  ruleSuppressionFixture,
  scoresBefore,
  scoresAfter,
  ok,
  err,
  type FetchCall
} from './helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

/** Mounts the explorer over the fake wire and primes the grid viewport. */
async function mountExplorer(options: Parameters<typeof reportClient>[0] = {}, props: Record<string, unknown> = {}) {
  installGridStubs();
  const { api, calls } = reportClient(options);
  const view = await render(
    <IssueExplorer api={api} auditId={AUDIT} projectId={PROJECT} {...props} />
  );
  await flush(20);
  await primeGrid(view.container);
  return { view, calls, api };
}

/** Counts mounted grid row DOM nodes. */
function domRowCount(container: HTMLElement): number {
  return container.querySelectorAll('[data-testid^="cf-grid-row-"]').length;
}

/** Clicks a data cell to activate the row (selection column avoided). */
function activateRow(container: HTMLElement, rowIndex: number): void {
  const cell = container.querySelector(`[data-cf-row="${rowIndex}"][data-cf-col="1"]`);
  expect(cell).not.toBeNull();
  click(cell!);
}

describe('IssueExplorer — virtualization (FS-CRAWL-002 §7.1)', () => {
  it('keeps ≤50 DOM rows over a 10,000-row dataset with server-true totals', async () => {
    const { view, calls } = await mountExplorer({ findings: seedFindings(10000) });
    expect(byTestId(view.container, 'issues-grid')).not.toBeNull();
    expect(domRowCount(view.container)).toBeGreaterThan(0);
    expect(domRowCount(view.container)).toBeLessThanOrEqual(50);
    expect(byTestId(view.container, 'cf-grid-total')!.textContent).toMatch(/10[,.]000 rows|10000 rows/);
    // Server-windowed: only bounded pages were fetched, never the dataset.
    const findingsCalls = callsTo(calls, '/findings');
    expect(findingsCalls.length).toBeLessThanOrEqual(3);
    expect(findingsCalls[0].url).toContain('limit=200');
    view.unmount();
  });

  it('keeps ≤50 DOM rows after scrolling into the middle of the dataset', async () => {
    const { view } = await mountExplorer({ findings: seedFindings(10000) });
    const body = byTestId(view.container, 'cf-grid-body')!;
    setScrollGeometry(body, { scrollTop: 5000 * 36 });
    await flush(40);
    expect(domRowCount(view.container)).toBeLessThanOrEqual(50);
    expect(byTestId(view.container, 'cf-grid-row-5000')).not.toBeNull();
    expect(byTestId(view.container, 'cf-grid-row-0')).toBeNull();
    view.unmount();
  });
});

describe('IssueExplorer — filters (locked findings query)', () => {
  it('filters by severity on the wire and renders only matching rows', async () => {
    const { view, calls } = await mountExplorer({ findings: seedFindings(400) });
    selectOption(byTestId(view.container, 'issues-filter-severity') as HTMLSelectElement, 'error');
    await flush(40);
    const last = callsTo(calls, '/findings').at(-1)!;
    expect(last.url).toContain('severity=error');
    const badges = view.container.querySelectorAll('[data-cf-col="0"] .cf-badge');
    expect(badges.length).toBeGreaterThan(0);
    for (const badge of badges) expect(badge.textContent).toBe('error');
    view.unmount();
  });

  it('filters by category on the wire (lowercase enum keys)', async () => {
    const { view, calls } = await mountExplorer({ findings: seedFindings(400) });
    selectOption(byTestId(view.container, 'issues-filter-category') as HTMLSelectElement, 'structureddata');
    await flush(40);
    expect(callsTo(calls, '/findings').at(-1)!.url).toContain('category=structureddata');
    view.unmount();
  });

  it('debounces the rule-key filter into the wire query', async () => {
    const { view, calls } = await mountExplorer({ findings: sampleFindings });
    type(byTestId(view.container, 'issues-filter-rule') as HTMLInputElement, 'structured-data.invalid');
    await flush(320);
    expect(callsTo(calls, '/findings').at(-1)!.url).toContain('ruleKey=structured-data.invalid');
    expect(byTestId(view.container, 'cf-grid-total')!.textContent).toMatch(/1 rows|1 row/);
    view.unmount();
  });

  it('combines severity + category filters in one server query', async () => {
    const { view, calls } = await mountExplorer({ findings: seedFindings(400) });
    selectOption(byTestId(view.container, 'issues-filter-severity') as HTMLSelectElement, 'warning');
    selectOption(byTestId(view.container, 'issues-filter-category') as HTMLSelectElement, 'content');
    await flush(40);
    const url = callsTo(calls, '/findings').at(-1)!.url;
    expect(url).toContain('severity=warning');
    expect(url).toContain('category=content');
    view.unmount();
  });
});

describe('IssueExplorer — detail + evidence drill-down', () => {
  it('opens the finding detail with generic key/value evidence and URL links', async () => {
    const onOpenUrl = vi.fn();
    const { view } = await mountExplorer({ findings: sampleFindings }, { onOpenUrl });
    activateRow(view.container, 0);
    await flush();

    const detail = byTestId(view.container, 'issues-detail')!;
    expect(detail).not.toBeNull();
    expect(byTestId(view.container, 'issues-detail-title')!.textContent).toBe('structured-data.invalid');
    // Generic evidence keys from the wave-9A structured-data.invalid shape.
    expect(byTestId(view.container, 'issues-evidence-format')!.textContent).toContain('json-ld');
    expect(byTestId(view.container, 'issues-evidence-missingRequired')!.textContent).toContain('name');
    expect(byTestId(view.container, 'issues-evidence-errors')!.textContent).toContain('rich-result-missing-required');
    expect(byTestId(view.container, 'issues-evidence-richResult')!.textContent).toContain('missing-required-field');
    // URL-valued evidence renders as a link.
    const link = byTestId(view.container, 'issues-evidence-url')!.querySelector('a')!;
    expect(link.getAttribute('href')).toBe('http://127.0.0.1:47311/pages/structured-data.html');
    // Snippet + selector surfaces.
    expect(byTestId(view.container, 'issues-detail-snippet')!.textContent).toContain('application/ld+json');
    // Affected-URL drill-down handoff.
    click(byTestId(view.container, 'issues-detail-open-url')!);
    expect(onOpenUrl).toHaveBeenCalledWith('7');
    // Close.
    click(byTestId(view.container, 'issues-detail-close')!);
    expect(byTestId(view.container, 'issues-detail')).toBeNull();
    view.unmount();
  });

  it('drills hreflang reciprocity evidence URL ids through onOpenUrl', async () => {
    const onOpenUrl = vi.fn();
    const { view } = await mountExplorer({ findings: [hreflangNoReturn] }, { onOpenUrl });
    activateRow(view.container, 0);
    await flush();
    expect(byTestId(view.container, 'issues-evidence-pageUrlId')!.textContent).toContain('21');
    expect(byTestId(view.container, 'issues-evidence-targetUrlId')!.textContent).toContain('22');
    expect(byTestId(view.container, 'issues-evidence-targetAnnotations')!.textContent).toContain('[]');
    click(byTestId(view.container, 'issues-evidence-open-targetUrlId')!);
    expect(onOpenUrl).toHaveBeenCalledWith('22');
    view.unmount();
  });

  it('renders an honest empty-evidence state', async () => {
    const bare = { ...duplicateTitle, evidence: null };
    const { view } = await mountExplorer({ findings: [bare] });
    activateRow(view.container, 0);
    await flush();
    expect(byTestId(view.container, 'issues-detail-evidence-empty')).not.toBeNull();
    view.unmount();
  });
});

describe('IssueExplorer — suppression (WP-03-08 engine over REST)', () => {
  it('suppresses a finding, recomputes scores synchronously, and shows the delta', async () => {
    const scores = { current: scoresBefore };
    const onSuppressionsChanged = vi.fn();
    const { view, calls } = await mountExplorer(
      { findings: [structuredDataInvalid, hreflangNoReturn], scores },
      { onSuppressionsChanged }
    );
    expect(byTestId(view.container, 'issues-score-overall')!.textContent).toContain('72');
    expect(byTestId(view.container, 'issues-suppress-f-sd-1')).not.toBeNull();

    click(byTestId(view.container, 'issues-suppress-f-sd-1')!);
    await flush(40);

    // Wire: finding-scoped create against the project route.
    const post = callsTo(calls, '/suppressions').find((call) => call.method === 'POST')!;
    expect(post.url).toContain(`/projects/${PROJECT}/suppressions`);
    expect(JSON.parse(post.body!)).toEqual({ ruleKey: 'structured-data.invalid', scope: { urlId: '7' } });
    // Score recompute delta (backend recomputes synchronously on create).
    const delta = byTestId(view.container, 'issues-score-delta')!;
    expect(delta.textContent).toContain('72 → 80');
    expect(delta.textContent).toContain('+8');
    expect(byTestId(view.container, 'issues-score-delta-structureddata')!.textContent).toContain('+15');
    expect(byTestId(view.container, 'issues-score-overall')!.textContent).toContain('80');
    // Row flips to suppressed + reversible action; store handoff notified.
    expect(byTestId(view.container, 'issues-unsuppress-f-sd-1')).not.toBeNull();
    expect(onSuppressionsChanged).toHaveBeenCalledWith([suppressionFixture]);
    const statusCell = view.container.querySelector('[data-cf-row="0"][data-cf-col="5"]')!;
    expect(statusCell.textContent).toContain('suppressed');
    view.unmount();
  });

  it('unsuppresses through DELETE and reverses the score delta (reversibility)', async () => {
    const scores = { current: scoresBefore };
    const { view, calls } = await mountExplorer(
      { findings: [structuredDataInvalid], scores },
      { initialSuppressions: [] }
    );
    click(byTestId(view.container, 'issues-suppress-f-sd-1')!);
    await flush(40);
    expect(byTestId(view.container, 'issues-unsuppress-f-sd-1')).not.toBeNull();

    click(byTestId(view.container, 'issues-unsuppress-f-sd-1')!);
    await flush(40);

    const del = callsTo(calls, '/suppressions').find((call) => call.method === 'DELETE')!;
    expect(del.url).toContain(`/projects/${PROJECT}/suppressions/${encodeURIComponent(suppressionFixture.key)}`);
    const delta = byTestId(view.container, 'issues-score-delta')!;
    expect(delta.textContent).toContain('80 → 72');
    expect(delta.textContent).toContain('-8');
    expect(byTestId(view.container, 'issues-suppress-f-sd-1')).not.toBeNull();
    const statusCell = view.container.querySelector('[data-cf-row="0"][data-cf-col="5"]')!;
    expect(statusCell.textContent).toContain('active');
    view.unmount();
  });

  it('suppresses rule-scoped from the detail panel (no URL dimension)', async () => {
    const scores = { current: scoresBefore };
    const onSuppression = (call: FetchCall) => {
      if (call.method === 'POST') {
        scores.current = scoresAfter;
        return ok(ruleSuppressionFixture);
      }
      scores.current = scoresBefore;
      return ok({ ...ruleSuppressionFixture, suppressed: false });
    };
    const { view, calls } = await mountExplorer({ findings: [duplicateTitle], scores, onSuppression });
    activateRow(view.container, 0);
    await flush();
    click(byTestId(view.container, 'issues-detail-suppress-rule')!);
    await flush(40);

    const post = callsTo(calls, '/suppressions').find((call) => call.method === 'POST')!;
    expect(JSON.parse(post.body!)).toEqual({ ruleKey: 'content.duplicate-title' });
    expect(byTestId(view.container, 'issues-detail-status')!.textContent).toContain('suppressed');
    expect(byTestId(view.container, 'issues-detail-unsuppress')).not.toBeNull();
    // Reverse from the detail panel.
    click(byTestId(view.container, 'issues-detail-unsuppress')!);
    await flush(40);
    expect(byTestId(view.container, 'issues-detail-status')!.textContent).toContain('active');
    view.unmount();
  });

  it('shows the suppressed view scoped to the selected suppression dimensions', async () => {
    const { view, calls } = await mountExplorer(
      { findings: sampleFindings },
      { initialSuppressions: [suppressionFixture] }
    );
    selectOption(byTestId(view.container, 'issues-filter-status') as HTMLSelectElement, 'suppressed');
    await flush(40);

    expect(byTestId(view.container, 'issues-status-note')).not.toBeNull();
    const picker = byTestId(view.container, 'issues-suppression-picker') as HTMLSelectElement;
    expect(picker).not.toBeNull();
    expect(picker.value).toBe(suppressionFixture.key);
    const url = callsTo(calls, '/findings').at(-1)!.url;
    expect(url).toContain('ruleKey=structured-data.invalid');
    expect(url).toContain('urlId=7');
    expect(byTestId(view.container, 'cf-grid-total')!.textContent).toMatch(/1 rows|1 row/);
    view.unmount();
  });

  it('shows the empty suppressed state when nothing is suppressed yet', async () => {
    const { view } = await mountExplorer({ findings: sampleFindings });
    selectOption(byTestId(view.container, 'issues-filter-status') as HTMLSelectElement, 'suppressed');
    await flush(20);
    expect(byTestId(view.container, 'issues-suppressed-empty')).not.toBeNull();
    view.unmount();
  });

  it('surfaces suppression envelope errors without touching scores', async () => {
    const onSuppression = () => err('conflict', 'Suppression already exists');
    const { view } = await mountExplorer({ findings: [structuredDataInvalid], onSuppression });
    click(byTestId(view.container, 'issues-suppress-f-sd-1')!);
    await flush(40);
    expect(byTestId(view.container, 'issues-suppression-error')!.textContent).toContain('Suppression already exists');
    expect(byTestId(view.container, 'issues-score-delta')).toBeNull();
    expect(byTestId(view.container, 'issues-suppress-f-sd-1')).not.toBeNull();
    view.unmount();
  });

  it('shows the busy state while the suppression round-trip is in flight', async () => {
    const onSuppression = async () => {
      await new Promise((resolve) => setTimeout(resolve, 60));
      return ok(suppressionFixture);
    };
    const { view } = await mountExplorer({ findings: [structuredDataInvalid], onSuppression });
    click(byTestId(view.container, 'issues-suppress-f-sd-1')!);
    await flush();
    expect(byTestId(view.container, 'issues-suppression-busy')).not.toBeNull();
    await flush(120);
    expect(byTestId(view.container, 'issues-suppression-busy')).toBeNull();
    view.unmount();
  });

  it('disables suppression actions without a project (project-scoped engine)', async () => {
    const { view } = await mountExplorer({ findings: [structuredDataInvalid] }, { projectId: null });
    expect(byTestId(view.container, 'issues-no-project-note')).not.toBeNull();
    expect((byTestId(view.container, 'issues-suppress-f-sd-1') as HTMLButtonElement).disabled).toBe(true);
    view.unmount();
  });
});

describe('IssueExplorer — empty / offline / error states', () => {
  it('shows the no-audit state', async () => {
    installGridStubs();
    const { api } = reportClient();
    const view = await render(<IssueExplorer api={api} auditId={null} projectId={PROJECT} />);
    await flush();
    expect(byTestId(view.container, 'issues-grid')).not.toBeNull();
    expect(byTestId(view.container, 'issues-no-audit')).not.toBeNull();
    view.unmount();
  });

  it('shows the offline state without a backend client', async () => {
    installGridStubs();
    const view = await render(<IssueExplorer api={null} auditId={AUDIT} projectId={PROJECT} />);
    await flush();
    expect(byTestId(view.container, 'issues-offline')).not.toBeNull();
    view.unmount();
  });

  it('shows the clean empty state for a zero-row window', async () => {
    const { view } = await mountExplorer({ findings: [] });
    expect(byTestId(view.container, 'issues-empty')).not.toBeNull();
    view.unmount();
  });

  it('surfaces findings envelope errors through the grid error banner', async () => {
    const { view } = await mountExplorer({ findingsError: 'findings unavailable' });
    expect(byTestId(view.container, 'cf-grid-error')!.textContent).toContain('findings unavailable');
    view.unmount();
  });
});
