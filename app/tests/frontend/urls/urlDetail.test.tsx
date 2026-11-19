// @vitest-environment jsdom
/**
 * UrlDetailPanel proofs (WP-09-09, FS-CRAWL-002 §8.2 + §15.6): the phase-gate
 * detail renderings against ACTUAL REST wire JSON — /redirect/chain-1 three
 * hops (rich WP-09-06 payload + legacy flat RedirectHopWire[] + legacy
 * redirectTargetUrl), loop/cap badges, /pages/noindex.html noindex inventory
 * (CR-024), /pages/canonical-conflict.html BOTH canonical targets, hreflang,
 * inlinks/outlinks with anchor + domZone, asset lists, and the SERP pixel
 * preview with truncation indicators through the bundled measurer.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { UrlDetailPanel } from '../../../src/frontend/src/surfaces/explorers/urls/UrlDetailPanel';
import type { UrlDetailWireView, UrlOpenTarget } from '../../../src/frontend/src/surfaces/explorers/urls/types';
import { measureTextPx, TITLE_FONT, DESKTOP_BUDGET, MOBILE_BUDGET } from '../../../src/frontend/src/tools/serp/fontMetrics';
import {
  FIXTURE_BASE,
  render,
  byTestId,
  byTestIdAll,
  textOf,
  click,
  flush,
  installDomStubs,
  chainDetail,
  legacyFlatChainDetail,
  legacyTargetDetail,
  loopChainDetail,
  cappedChainDetail,
  noindexDetail,
  canonicalConflictDetail,
  serpLongDetail,
  serpShortDetail,
  assetsDetail,
  makeDetail,
  linkEdge
} from './helpers';

beforeEach(() => {
  document.body.replaceChildren();
  installDomStubs();
});

/** Renders the panel standalone (pure presentational — no shell needed). */
async function renderPanel(detail: UrlDetailWireView, props: { onOpenUrl?: (target: UrlOpenTarget) => void; onClose?: () => void } = {}) {
  return render(<UrlDetailPanel detail={detail} auditId="1" {...props} />);
}

describe('phase gate — /redirect/chain-1 renders THREE hops', () => {
  it('renders the full chain hop-by-hop from the WP-09-06 payload', async () => {
    const mounted = await renderPanel(chainDetail());
    const hops = byTestIdAll(mounted.container, 'url-detail-hop');
    expect(hops).toHaveLength(3);

    expect(hops[0].getAttribute('data-hop-ordinal')).toBe('1');
    expect(textOf(byTestId(hops[0], 'url-detail-hop-url'))).toBe(`${FIXTURE_BASE}/redirect/chain-1`);
    expect(textOf(byTestId(hops[0], 'url-detail-hop-status'))).toBe('301');
    expect(textOf(byTestId(hops[0], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/redirect/chain-2`);

    expect(hops[1].getAttribute('data-hop-ordinal')).toBe('2');
    expect(textOf(byTestId(hops[1], 'url-detail-hop-url'))).toBe(`${FIXTURE_BASE}/redirect/chain-2`);
    expect(textOf(byTestId(hops[1], 'url-detail-hop-status'))).toBe('302');
    expect(textOf(byTestId(hops[1], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/redirect/chain-3`);

    expect(hops[2].getAttribute('data-hop-ordinal')).toBe('3');
    expect(textOf(byTestId(hops[2], 'url-detail-hop-url'))).toBe(`${FIXTURE_BASE}/redirect/chain-3`);
    expect(textOf(byTestId(hops[2], 'url-detail-hop-status'))).toBe('301');
    expect(textOf(byTestId(hops[2], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/pages/redirect-final.html`);

    const final = byTestId(mounted.container, 'url-detail-chain-final')!;
    expect(final.textContent).toContain('/pages/redirect-final.html');
    expect(final.textContent).toContain('200');
    expect(byTestId(mounted.container, 'url-detail-loop-badge')).toBeNull();
    expect(byTestId(mounted.container, 'url-detail-cap-badge')).toBeNull();
    mounted.unmount();
  });

  it('renders the same three hops from the legacy flat RedirectHopWire[] wire', async () => {
    const mounted = await renderPanel(legacyFlatChainDetail());
    const hops = byTestIdAll(mounted.container, 'url-detail-hop');
    expect(hops).toHaveLength(3);
    expect(textOf(byTestId(hops[0], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/redirect/chain-2`);
    expect(textOf(byTestId(hops[2], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/pages/redirect-final.html`);
    // The flat wire carries no finalStatusCode — the record's final status
    // (301 for the chain-1 record) anchors the final badge honestly.
    expect(byTestId(mounted.container, 'url-detail-chain-final')!.textContent).toContain('301');
    mounted.unmount();
  });

  it('falls back to the legacy single-hop redirectTargetUrl', async () => {
    const mounted = await renderPanel(legacyTargetDetail());
    const hops = byTestIdAll(mounted.container, 'url-detail-hop');
    expect(hops).toHaveLength(1);
    expect(textOf(byTestId(hops[0], 'url-detail-hop-url'))).toBe(`${FIXTURE_BASE}/redirect/chain-1`);
    expect(textOf(byTestId(hops[0], 'url-detail-hop-target'))).toBe(`${FIXTURE_BASE}/redirect/chain-2`);
    mounted.unmount();
  });

  it('shows the direct-fetch state when no chain exists', async () => {
    const mounted = await renderPanel(makeDetail({ id: 'url-home', canonicalUrl: `${FIXTURE_BASE}/` }));
    expect(textOf(byTestId(mounted.container, 'url-detail-chain-empty'))).toContain('No redirects');
    expect(byTestIdAll(mounted.container, 'url-detail-hop')).toHaveLength(0);
    mounted.unmount();
  });
});

describe('loop and cap badges', () => {
  it('renders the engine loop verdict on a cyclic chain', async () => {
    const mounted = await renderPanel(loopChainDetail());
    expect(textOf(byTestId(mounted.container, 'url-detail-loop-badge'))).toContain('Redirect loop');
    expect(byTestIdAll(mounted.container, 'url-detail-hop')).toHaveLength(3);
    mounted.unmount();
  });

  it('renders the chain-cap badge when the engine capped the chain', async () => {
    const mounted = await renderPanel(cappedChainDetail());
    expect(textOf(byTestId(mounted.container, 'url-detail-cap-badge'))).toContain('Chain cap exceeded');
    mounted.unmount();
  });

  it('flags a repeated hop URL even when the legacy wire omits loopDetected', async () => {
    const detail = makeDetail({
      id: 'url-loop',
      canonicalUrl: `${FIXTURE_BASE}/redirect/loop-a`,
      statusCode: 301,
      statusClass: '3xx',
      redirectChain: [
        { url: `${FIXTURE_BASE}/redirect/loop-a`, statusCode: 301, location: `${FIXTURE_BASE}/redirect/loop-b` },
        { url: `${FIXTURE_BASE}/redirect/loop-b`, statusCode: 302, location: `${FIXTURE_BASE}/redirect/loop-a` }
      ]
    });
    const mounted = await renderPanel(detail);
    expect(byTestId(mounted.container, 'url-detail-loop-badge')).not.toBeNull();
    mounted.unmount();
  });
});

describe('phase gate — /pages/noindex.html inventory (CR-024)', () => {
  it('shows noindex + NOT search-indexable while fetchable at 200', async () => {
    const mounted = await renderPanel(noindexDetail());
    expect(textOf(byTestId(mounted.container, 'url-detail-status'))).toContain('200');
    expect(textOf(byTestId(mounted.container, 'url-detail-indexable'))).toContain('Not search-indexable');
    expect(textOf(byTestId(mounted.container, 'url-detail-noindex'))).toContain('noindex');
    expect(textOf(byTestId(mounted.container, 'url-detail-meta-robots'))).toBe('noindex,follow');
    expect(textOf(byTestId(mounted.container, 'url-detail-title'))).toBe('Noindex page');
    expect(textOf(byTestId(mounted.container, 'url-detail-meta-description'))).toContain('excluded from search indexing');
    const finding = byTestId(mounted.container, 'url-detail-finding')!;
    expect(finding.getAttribute('data-rule-key')).toBe('indexability.noindex');
    mounted.unmount();
  });

  it('detects noindex from the X-Robots-Tag header alone', async () => {
    const detail = makeDetail({
      id: 'url-header-noindex',
      canonicalUrl: `${FIXTURE_BASE}/pages/header-noindex.html`,
      indexable: false,
      responseHeaders: { 'x-robots-tag': 'noindex, nofollow' }
    });
    const mounted = await renderPanel(detail);
    expect(byTestId(mounted.container, 'url-detail-noindex')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-x-robots-tag'))).toBe('noindex, nofollow');
    mounted.unmount();
  });

  it('detects noindex from the engine finding when element rows are absent', async () => {
    const detail = makeDetail({
      id: 'url-finding-noindex',
      canonicalUrl: `${FIXTURE_BASE}/pages/finding-noindex.html`,
      indexable: false,
      findings: [{
        id: 'f-1',
        ruleKey: 'indexability.noindex',
        severity: 'warning',
        category: 'indexability',
        urlId: 'url-finding-noindex',
        message: 'noindex directive observed'
      }]
    });
    const mounted = await renderPanel(detail);
    expect(byTestId(mounted.container, 'url-detail-noindex')).not.toBeNull();
    mounted.unmount();
  });
});

describe('phase gate — /pages/canonical-conflict.html shows BOTH targets', () => {
  it('renders the HTML and header canonical targets with provenance', async () => {
    const mounted = await renderPanel(canonicalConflictDetail());
    const canonicals = byTestIdAll(mounted.container, 'url-detail-canonical');
    expect(canonicals).toHaveLength(2);

    const html = canonicals.find((entry) => entry.getAttribute('data-source') === 'html')!;
    const header = canonicals.find((entry) => entry.getAttribute('data-source') === 'header')!;
    expect(html.textContent).toContain('/pages/canonical-target-html.html');
    expect(header.textContent).toContain('/pages/canonical-target-header.html');

    // The conflict diff panel shows both sides side by side (§15.6).
    const conflict = byTestId(mounted.container, 'url-detail-conflict')!;
    expect(conflict.getAttribute('data-conflict')).toBe('true');
    expect(textOf(byTestId(conflict, 'url-detail-conflict-header'))).toContain('/pages/canonical-target-header.html');
    expect(textOf(byTestId(conflict, 'url-detail-conflict-html'))).toContain('/pages/canonical-target-html.html');
    mounted.unmount();
  });

  it('renders hreflang alternates from the document_head payload', async () => {
    const mounted = await renderPanel(canonicalConflictDetail());
    const entries = byTestIdAll(mounted.container, 'url-detail-hreflang-entry');
    expect(entries).toHaveLength(1);
    expect(entries[0].textContent).toContain('en');
    expect(entries[0].textContent).toContain('/pages/canonical-conflict.html');
    mounted.unmount();
  });

  it('derives both targets from conflict evidence when element rows are absent', async () => {
    const detail = makeDetail({
      id: 'url-canonical-conflict',
      canonicalUrl: `${FIXTURE_BASE}/pages/canonical-conflict.html`,
      headerConflicts: [{
        headerName: 'x-robots-tag',
        headerValue: `<${FIXTURE_BASE}/pages/canonical-target-header.html>; rel="canonical"`,
        htmlValue: `${FIXTURE_BASE}/pages/canonical-target-html.html`,
        conflict: true
      }]
    });
    const mounted = await renderPanel(detail);
    const values = byTestIdAll(mounted.container, 'url-detail-canonical').map((entry) => entry.textContent ?? '');
    expect(values.some((value) => value.includes('/pages/canonical-target-header.html'))).toBe(true);
    expect(values.some((value) => value.includes('/pages/canonical-target-html.html'))).toBe(true);
    mounted.unmount();
  });
});

describe('inlinks / outlinks — anchor text + domZone', () => {
  it('renders outlinks with anchors, zones, and internal/external badges', async () => {
    const mounted = await renderPanel(serpLongDetail());
    const outlinks = byTestId(mounted.container, 'url-detail-outlinks')!;
    const rows = byTestIdAll(outlinks, 'url-detail-link');
    expect(rows).toHaveLength(3);
    expect(outlinks.textContent).toContain('“Broken target”');
    expect(outlinks.textContent).toContain('“Noindex page”');
    expect(outlinks.textContent).toContain('“External reference”');
    expect(outlinks.querySelector('[data-dom-zone="mainContent"]')).not.toBeNull();
    expect(outlinks.querySelector('[data-dom-zone="navigation"]')).not.toBeNull();
    expect(outlinks.querySelector('[data-dom-zone="footer"]')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-inlinks-empty'))).toContain('No inbound links');
    mounted.unmount();
  });

  it('renders inlinks with anchor + zone and drills through onOpenUrl', async () => {
    const opened: UrlOpenTarget[] = [];
    const mounted = await renderPanel(noindexDetail(), { onOpenUrl: (target) => opened.push(target) });
    const inlinks = byTestId(mounted.container, 'url-detail-inlinks')!;
    expect(inlinks.textContent).toContain('“Noindex page”');
    expect(inlinks.querySelector('[data-dom-zone="navigation"]')).not.toBeNull();

    click(inlinks.querySelector('[data-testid="url-detail-link-target"]')!);
    expect(opened).toEqual([{ auditId: '1', urlId: 'url-home', canonicalUrl: `${FIXTURE_BASE}/` }]);
    mounted.unmount();
  });
});

describe('asset lists', () => {
  it('renders assets from the record and element rows, deduplicated', async () => {
    const mounted = await renderPanel(assetsDetail());
    const assets = byTestIdAll(mounted.container, 'url-detail-asset');
    const urls = assets.map((asset) => asset.textContent ?? '');
    expect(urls.some((value) => value.includes('/assets/logo.png'))).toBe(true);
    expect(urls.some((value) => value.includes('/assets/photo.jpg'))).toBe(true);
    expect(urls.some((value) => value.includes('/assets/style.css'))).toBe(true);
    expect(urls.some((value) => value.includes('/assets/app.js'))).toBe(true);
    expect(urls.some((value) => value.includes('/assets/diagram.svg'))).toBe(true);
    // logo.png appears in both the record and an element row — once only.
    expect(urls.filter((value) => value.includes('/assets/logo.png'))).toHaveLength(1);
    expect(assets.find((asset) => asset.textContent?.includes('photo.jpg'))!.getAttribute('data-asset-kind')).toBe('image');
    expect(assets.find((asset) => asset.textContent?.includes('style.css'))!.getAttribute('data-asset-kind')).toBe('css');
    mounted.unmount();
  });

  it('shows the empty asset state honestly', async () => {
    const mounted = await renderPanel(serpShortDetail());
    expect(textOf(byTestId(mounted.container, 'url-detail-assets-empty'))).toContain('No assets recorded');
    mounted.unmount();
  });
});

describe('§15.6 — SERP pixel preview with truncation indicators', () => {
  it('flags an over-budget title and description on desktop', async () => {
    const mounted = await renderPanel(serpLongDetail());
    expect(byTestId(mounted.container, 'url-detail-serp-title-truncated')).not.toBeNull();
    expect(byTestId(mounted.container, 'url-detail-serp-description-truncated')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title-meter'))).toContain(`/ ${DESKTOP_BUDGET.titleMaxPx} px`);
    expect(byTestId(mounted.container, 'url-detail-serp-title-overflow')).not.toBeNull();
    // The rendered title is the measurer's truncated text (ellipsis).
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title'))).toContain('…');
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-stored'))).toContain('truncated');
    mounted.unmount();
  });

  it('shows fits badges for short title/description', async () => {
    const mounted = await renderPanel(serpShortDetail());
    expect(byTestId(mounted.container, 'url-detail-serp-title-fits')).not.toBeNull();
    expect(byTestId(mounted.container, 'url-detail-serp-description-fits')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title'))).toBe('Fixture Site');
    mounted.unmount();
  });

  it('switches budgets with the desktop/mobile toggle', async () => {
    // A title that fits desktop (580px) but not mobile (400px).
    const title = 'Fixture Site — Redirect Chains and Canonical Conflicts';
    const width = measureTextPx(title, TITLE_FONT);
    expect(width).toBeGreaterThan(MOBILE_BUDGET.titleMaxPx);
    expect(width).toBeLessThanOrEqual(DESKTOP_BUDGET.titleMaxPx);

    const detail = makeDetail({ id: 'url-home', canonicalUrl: `${FIXTURE_BASE}/`, title });
    const mounted = await renderPanel(detail);
    expect(byTestId(mounted.container, 'url-detail-serp-title-fits')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title-meter'))).toContain(`/ ${DESKTOP_BUDGET.titleMaxPx} px`);

    click(byTestId(mounted.container, 'url-detail-serp-mobile')!);
    await flush(5);
    expect(byTestId(mounted.container, 'url-detail-serp-title-truncated')).not.toBeNull();
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title-meter'))).toContain(`/ ${MOBILE_BUDGET.titleMaxPx} px`);

    click(byTestId(mounted.container, 'url-detail-serp-desktop')!);
    await flush(5);
    expect(byTestId(mounted.container, 'url-detail-serp-title-fits')).not.toBeNull();
    mounted.unmount();
  });

  it('renders the display URL and empty-text states honestly', async () => {
    const mounted = await renderPanel(makeDetail({ id: 'url-x', canonicalUrl: `${FIXTURE_BASE}/pages/x.html` }));
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-url'))).toBe(`${FIXTURE_BASE}/pages/x.html`);
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-title'))).toContain('No title');
    expect(textOf(byTestId(mounted.container, 'url-detail-serp-description'))).toContain('No meta description');
    mounted.unmount();
  });
});

describe('panel affordances', () => {
  it('fires onClose and renders element-group counts', async () => {
    let closed = 0;
    const detail = makeDetail({
      id: 'url-home',
      canonicalUrl: `${FIXTURE_BASE}/`,
      elementInventory: { counts: { document_head: 1, links: 8 }, totalElements: 9 },
      outboundLinks: [linkEdge({
        sourceUrlId: 'url-home',
        targetUrlId: 'url-noindex',
        sourceUrl: `${FIXTURE_BASE}/`,
        targetUrl: `${FIXTURE_BASE}/pages/noindex.html`,
        anchorText: 'Noindex page',
        domZone: 'navigation'
      })]
    });
    const mounted = await renderPanel(detail, { onClose: () => { closed += 1; } });
    const counts = byTestId(mounted.container, 'url-detail-element-counts')!;
    expect(counts.textContent).toContain('document_head');
    expect(counts.textContent).toContain('links');
    click(byTestId(mounted.container, 'url-detail-close')!);
    expect(closed).toBe(1);
    mounted.unmount();
  });
});
