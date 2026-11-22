// @vitest-environment jsdom
/**
 * Rendered comparison surface component proofs (WP-10-03, UI-16; probe-2 G01
 * "rendered comparison surface absent" → the surface now exists and lists
 * every page with a response-vs-rendered difference, severity-sorted).
 *
 * Acceptance stand-in: the surface lists BOTH /pages/late-meta.html and
 * /pages/js-content.html. Data flows through the locked issue-explorer query
 * surface via the real typed client; the grid engine virtualizes the rows.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { RenderedComparisonSurface } from '../../../src/frontend/src/surfaces/explorers/rendered/RenderedComparisonSurface';
import { MAX_DOM_ROWS } from '../../../src/frontend/src/grid/types';
import type { Finding, UrlRecordSummary } from '../../../src/frontend/src/contracts/api/audits';
import { renderTool, fakeServices } from '../tools/helpers';
import {
  AUDIT,
  renderedClient,
  flush,
  byTestId,
  textOf,
  click,
  installGridStubs,
  primeGrid,
  renderFinding,
  urlRecord,
  type Mounted
} from './helpers';

afterEach(() => {
  document.body.innerHTML = '';
});

/** Mounts the comparison surface and primes the grid viewport. */
async function mountSurface(options: {
  api?: ReturnType<typeof renderedClient>['api'] | null;
  findings?: readonly Finding[];
  urls?: readonly UrlRecordSummary[];
  onOpenUrl?: (urlId: string) => void;
} = {}): Promise<{ view: Mounted; calls: ReturnType<typeof renderedClient>['calls'] }> {
  installGridStubs();
  const { api, calls } = renderedClient({ findings: options.findings, urls: options.urls });
  const view = await renderTool(
    <RenderedComparisonSurface
      api={options.api === undefined ? api : options.api}
      auditId={AUDIT}
      onOpenUrl={options.onOpenUrl}
    />,
    fakeServices().services
  );
  await flush(30);
  await primeGrid(view.container);
  await flush(10);
  return { view, calls };
}

/** URL cell testids in DOM (row) order. */
function urlOrder(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll<HTMLElement>('[data-testid^="rendered-comparison-url-"]'))
    .map((node) => node.getAttribute('data-testid')!.replace('rendered-comparison-url-', ''));
}

/** Counts mounted grid row DOM nodes. */
function domRowCount(container: HTMLElement): number {
  return container.querySelectorAll('[data-testid^="cf-grid-row-"]').length;
}

describe('RenderedComparisonSurface — lists differing pages (UI-16 acceptance)', () => {
  it('lists both late-meta and js-content (and every other differing page)', async () => {
    const { view } = await mountSurface();
    expect(byTestId(view.container, 'rendered-comparison')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-late-meta')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-js-content')).not.toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-js-link')).not.toBeNull();
    // service-worker-only and clean pages are absent
    expect(byTestId(view.container, 'rendered-comparison-url-url-service-worker')).toBeNull();
    expect(byTestId(view.container, 'rendered-comparison-url-url-clean')).toBeNull();
  });

  it('is severity-sorted: critical noindex page first, then the warning page', async () => {
    const { view } = await mountSurface();
    const order = urlOrder(view.container);
    expect(order[0]).toBe('url-late-meta');
    expect(order[1]).toBe('url-status');
    expect(byTestId(view.container, 'rendered-comparison-url-url-late-meta')?.getAttribute('data-critical')).toBe('true');
  });

  it('summarizes the differing-page and critical counts', async () => {
    const { view } = await mountSurface();
    expect(textOf(byTestId(view.container, 'rendered-comparison-count'))).toContain('6 pages');
    expect(textOf(byTestId(view.container, 'rendered-comparison'))).toContain('1 critical');
  });

  it('drills into the URL detail rendered tab on row activation', async () => {
    const onOpenUrl = vi.fn();
    const { view } = await mountSurface({ onOpenUrl });
    const cell = view.container.querySelector('[data-cf-row="0"][data-cf-col="1"]');
    expect(cell).not.toBeNull();
    click(cell!);
    expect(onOpenUrl).toHaveBeenCalledWith('url-late-meta');
  });
});

describe('RenderedComparisonSurface — virtualization invariant', () => {
  it('mounts ≤ MAX_DOM_ROWS rows even with hundreds of differing pages', async () => {
    const findings: Finding[] = [];
    const urls: UrlRecordSummary[] = [];
    for (let i = 0; i < 300; i += 1) {
      const urlId = `url-big-${i}`;
      urls.push(urlRecord({ id: urlId, canonicalUrl: `http://127.0.0.1:47311/pages/big-${i}.html`, captureSource: 'rendered' }));
      findings.push(renderFinding({
        id: `f-big-${i}`,
        ruleKey: 'render.text-delta',
        category: 'content',
        urlId,
        evidence: { details: { field: 'text', rawWordCount: 0, renderedWordCount: 200 } }
      }));
    }
    const { view } = await mountSurface({ findings, urls });
    expect(domRowCount(view.container)).toBeLessThanOrEqual(MAX_DOM_ROWS);
    expect(domRowCount(view.container)).toBeGreaterThan(0);
  });
});

describe('RenderedComparisonSurface — empty / offline states', () => {
  it('shows an honest empty state when no page differs', async () => {
    const { view } = await mountSurface({ findings: [] });
    expect(byTestId(view.container, 'rendered-comparison-empty')).not.toBeNull();
  });

  it('degrades offline without throwing', async () => {
    const { view } = await mountSurface({ api: null });
    expect(byTestId(view.container, 'rendered-comparison-offline')).not.toBeNull();
  });
});
