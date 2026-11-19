/**
 * Element-inventory derivation unit proofs (WP-09-09). Pure wire folding:
 * document_head payloads, header directives, Link-header parsing, finding
 * evidence fallbacks, canonical dedup, and asset lists.
 */

import { describe, expect, it } from 'vitest';
import {
  deriveInventoryFacts,
  deriveAssets,
  elementPayload,
  parseLinkHeader
} from '../../../src/frontend/src/surfaces/explorers/urls/inventory';
import type { UrlDetailWireView } from '../../../src/frontend/src/surfaces/explorers/urls/types';

const BASE = 'http://127.0.0.1:47311';

/** Minimal detail view for derivation tests. */
function detail(partial: Partial<UrlDetailWireView>): UrlDetailWireView {
  return {
    id: 'url-x',
    canonicalUrl: `${BASE}/pages/x.html`,
    statusCode: 200,
    statusClass: '2xx',
    contentType: 'text/html',
    depth: 1,
    indexable: true,
    title: null,
    responseHeaders: {},
    elementInventory: { counts: {}, totalElements: 0 },
    findings: [],
    inboundLinks: [],
    outboundLinks: [],
    ...partial
  } as UrlDetailWireView;
}

describe('parseLinkHeader', () => {
  it('parses canonical and hreflang alternate entries', () => {
    const entries = parseLinkHeader(`<${BASE}/pages/target.html>; rel="canonical", <${BASE}/pages/x-de.html>; rel="alternate"; hreflang="de"`);
    expect(entries).toHaveLength(2);
    expect(entries[0]).toEqual({ url: `${BASE}/pages/target.html`, rel: 'canonical', hreflang: null });
    expect(entries[1]).toEqual({ url: `${BASE}/pages/x-de.html`, rel: 'alternate', hreflang: 'de' });
  });

  it('tolerates unquoted and single-quoted params', () => {
    const entries = parseLinkHeader(`<${BASE}/a>; rel=canonical; hreflang='en'`);
    expect(entries[0].rel).toBe('canonical');
    expect(entries[0].hreflang).toBe('en');
  });

  it('returns empty for header-less values', () => {
    expect(parseLinkHeader('not a link header')).toHaveLength(0);
  });
});

describe('elementPayload', () => {
  it('prefers the parsed payload object', () => {
    expect(elementPayload({ elementGroup: 'document_head', payload: { title: 'T' } })).toEqual({ title: 'T' });
  });

  it('parses payloadJson and degrades malformed JSON to empty', () => {
    expect(elementPayload({ elementGroup: 'document_head', payloadJson: '{"title":"T"}' })).toEqual({ title: 'T' });
    expect(elementPayload({ elementGroup: 'document_head', payloadJson: '{broken' })).toEqual({});
  });
});

describe('deriveInventoryFacts — document_head payload', () => {
  const head = detail({
    elements: [{
      elementGroup: 'document_head',
      payloadJson: JSON.stringify({
        title: 'Head title',
        metaDescription: 'Head description',
        metaRobots: 'noindex,follow',
        canonicalUrl: `${BASE}/pages/canonical.html`,
        alternates: [{ hreflang: 'en', href: `${BASE}/pages/x.html` }, { hreflang: 'de', href: `${BASE}/pages/x-de.html` }]
      })
    }]
  });

  it('extracts title, description, meta robots, canonical, alternates', () => {
    const facts = deriveInventoryFacts(head);
    expect(facts.title).toBe('Head title');
    expect(facts.metaDescription).toBe('Head description');
    expect(facts.metaRobots).toBe('noindex,follow');
    expect(facts.noindex).toBe(true);
    expect(facts.canonicals).toEqual([{ value: `${BASE}/pages/canonical.html`, source: 'html' }]);
    expect(facts.hreflang).toHaveLength(2);
    expect(facts.hreflang[1]).toEqual({ hreflang: 'de', href: `${BASE}/pages/x-de.html`, source: 'html' });
  });

  it('falls back to the record title when document_head is absent', () => {
    const facts = deriveInventoryFacts(detail({ title: 'Record title' }));
    expect(facts.title).toBe('Record title');
    expect(facts.noindex).toBe(false);
    expect(facts.canonicals).toHaveLength(0);
  });
});

describe('deriveInventoryFacts — header directives', () => {
  it('reads X-Robots-Tag noindex and the canonical Link header', () => {
    const facts = deriveInventoryFacts(detail({
      responseHeaders: {
        'x-robots-tag': 'noindex',
        link: `<${BASE}/pages/header-canonical.html>; rel="canonical"`
      }
    }));
    expect(facts.noindex).toBe(true);
    expect(facts.xRobotsTag).toBe('noindex');
    expect(facts.canonicals).toEqual([{ value: `${BASE}/pages/header-canonical.html`, source: 'header' }]);
  });

  it('reads header_directives element rows when responseHeaders is empty', () => {
    const facts = deriveInventoryFacts(detail({
      elements: [{
        elementGroup: 'header_directives',
        payloadJson: JSON.stringify({ xRobotsTag: 'none', link: `<${BASE}/pages/h.html>; rel="canonical"` })
      }]
    }));
    expect(facts.xRobotsTag).toBe('none');
    expect(facts.noindex).toBe(false);
    expect(facts.canonicals[0].value).toBe(`${BASE}/pages/h.html`);
  });

  it('does not flag index/follow directives as noindex', () => {
    const facts = deriveInventoryFacts(detail({ responseHeaders: { 'x-robots-tag': 'index, follow' } }));
    expect(facts.noindex).toBe(false);
  });
});

describe('deriveInventoryFacts — finding evidence fallback', () => {
  it('flags noindex from the engine finding alone', () => {
    const facts = deriveInventoryFacts(detail({
      findings: [{
        id: 'f-1',
        ruleKey: 'indexability.noindex',
        severity: 'warning',
        category: 'indexability',
        urlId: 'url-x',
        message: 'noindex observed'
      }]
    }));
    expect(facts.noindex).toBe(true);
  });

  it('derives BOTH canonical targets from conflict evidence', () => {
    const facts = deriveInventoryFacts(detail({
      headerConflicts: [{
        headerName: 'x-robots-tag',
        headerValue: `<${BASE}/pages/canonical-target-header.html>; rel="canonical"`,
        htmlValue: `${BASE}/pages/canonical-target-html.html`,
        conflict: true
      }]
    }));
    expect(facts.canonicals).toHaveLength(2);
    expect(facts.canonicals.map((target) => target.value)).toEqual([
      `${BASE}/pages/canonical-target-header.html`,
      `${BASE}/pages/canonical-target-html.html`
    ]);
  });

  it('deduplicates identical canonical values across sources', () => {
    const facts = deriveInventoryFacts(detail({
      elements: [{
        elementGroup: 'document_head',
        payloadJson: JSON.stringify({ canonicalUrl: `${BASE}/pages/same.html` })
      }],
      headerConflicts: [{
        headerName: 'link',
        headerValue: `<${BASE}/pages/same.html>; rel="canonical"`,
        htmlValue: `${BASE}/pages/same.html`,
        conflict: false
      }]
    }));
    expect(facts.canonicals).toHaveLength(1);
    expect(facts.canonicals[0].source).toBe('html');
  });
});

describe('deriveAssets', () => {
  it('merges the assets record with element rows and deduplicates', () => {
    const assets = deriveAssets(detail({
      assets: {
        image: [`${BASE}/assets/logo.png`, { url: `${BASE}/assets/photo.jpg`, alt: 'Photo' }],
        css: `${BASE}/assets/style.css`
      },
      elements: [
        { elementGroup: 'images', payloadJson: JSON.stringify({ src: `${BASE}/assets/logo.png`, alt: 'Logo' }) },
        { elementGroup: 'images', payloadJson: JSON.stringify({ src: `${BASE}/assets/diagram.svg` }) }
      ]
    }));
    expect(assets).toHaveLength(4);
    expect(assets.filter((asset) => asset.url === `${BASE}/assets/logo.png`)).toHaveLength(1);
    expect(assets.find((asset) => asset.url === `${BASE}/assets/style.css`)!.kind).toBe('css');
    expect(assets.find((asset) => asset.url === `${BASE}/assets/diagram.svg`)!.kind).toBe('image');
  });

  it('returns empty when nothing is recorded', () => {
    expect(deriveAssets(detail({}))).toHaveLength(0);
  });
});
