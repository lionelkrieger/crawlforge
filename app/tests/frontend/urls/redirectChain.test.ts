/**
 * Redirect-chain normalization unit proofs (WP-09-09). Pure wire folding:
 * rich WP-09-06 payload, flat RedirectHopWire[], flat rich hops, legacy
 * redirectTargetUrl, and absent chains — one rendering model, honest
 * loop/cap verdicts.
 */

import { describe, expect, it } from 'vitest';
import { normalizeRedirectChain } from '../../../src/frontend/src/surfaces/explorers/urls/redirectChain';

const BASE = 'http://127.0.0.1:47311';
const record = { canonicalUrl: `${BASE}/redirect/chain-1`, statusCode: 301 };

describe('rich object payload (WP-09-06 storage → wire)', () => {
  it('preserves ordinals, targets, and the engine verdicts', () => {
    const chain = normalizeRedirectChain({
      hops: [
        { ordinal: 1, url: `${BASE}/redirect/chain-1`, toUrl: `${BASE}/redirect/chain-2`, statusCode: 301 },
        { ordinal: 2, url: `${BASE}/redirect/chain-2`, toUrl: `${BASE}/redirect/chain-3`, statusCode: 302 },
        { ordinal: 3, url: `${BASE}/redirect/chain-3`, toUrl: `${BASE}/pages/redirect-final.html`, statusCode: 301 }
      ],
      finalUrl: `${BASE}/pages/redirect-final.html`,
      finalStatusCode: 200,
      loopDetected: false,
      chainCapExceeded: false
    }, record);

    expect(chain.hops).toHaveLength(3);
    expect(chain.hops.map((hop) => hop.ordinal)).toEqual([1, 2, 3]);
    expect(chain.hops[1].toUrl).toBe(`${BASE}/redirect/chain-3`);
    expect(chain.finalUrl).toBe(`${BASE}/pages/redirect-final.html`);
    expect(chain.finalStatusCode).toBe(200);
    expect(chain.loopDetected).toBe(false);
    expect(chain.chainCapExceeded).toBe(false);
  });

  it('carries loop and cap verdicts verbatim', () => {
    const chain = normalizeRedirectChain({
      hops: [{ ordinal: 1, url: `${BASE}/a`, toUrl: `${BASE}/b`, statusCode: 301 }],
      loopDetected: true,
      chainCapExceeded: true
    }, record);
    expect(chain.loopDetected).toBe(true);
    expect(chain.chainCapExceeded).toBe(true);
  });

  it('derives a display-level loop when the payload omits loopDetected', () => {
    const chain = normalizeRedirectChain({
      hops: [
        { url: `${BASE}/a`, toUrl: `${BASE}/b`, statusCode: 301 },
        { url: `${BASE}/b`, toUrl: `${BASE}/a`, statusCode: 302 }
      ]
    }, record);
    expect(chain.loopDetected).toBe(true);
    expect(chain.hops.map((hop) => hop.ordinal)).toEqual([1, 2]);
  });

  it('accepts the legacy location field name inside rich hops', () => {
    const chain = normalizeRedirectChain({
      hops: [{ url: `${BASE}/a`, location: `${BASE}/b`, statusCode: 301 }]
    }, record);
    expect(chain.hops[0].toUrl).toBe(`${BASE}/b`);
  });
});

describe('flat RedirectHopWire[] (current wire)', () => {
  it('maps location → target and assigns ordinals', () => {
    const chain = normalizeRedirectChain([
      { url: `${BASE}/redirect/chain-1`, statusCode: 301, location: `${BASE}/redirect/chain-2` },
      { url: `${BASE}/redirect/chain-2`, statusCode: 302, location: `${BASE}/redirect/chain-3` },
      { url: `${BASE}/redirect/chain-3`, statusCode: 301, location: `${BASE}/pages/redirect-final.html` }
    ], record);
    expect(chain.hops).toHaveLength(3);
    expect(chain.hops[2].ordinal).toBe(3);
    expect(chain.hops[2].toUrl).toBe(`${BASE}/pages/redirect-final.html`);
    expect(chain.finalUrl).toBe(`${BASE}/pages/redirect-final.html`);
    expect(chain.finalStatusCode).toBe(301);
    expect(chain.loopDetected).toBe(false);
  });

  it('falls back to the next hop URL when location is null', () => {
    const chain = normalizeRedirectChain([
      { url: `${BASE}/a`, statusCode: 301, location: null },
      { url: `${BASE}/b`, statusCode: 200, location: null }
    ], record);
    expect(chain.hops[0].toUrl).toBe(`${BASE}/b`);
    expect(chain.hops[1].toUrl).toBeNull();
  });

  it('detects a repeated hop URL as a loop', () => {
    const chain = normalizeRedirectChain([
      { url: `${BASE}/a`, statusCode: 301, location: `${BASE}/b` },
      { url: `${BASE}/b`, statusCode: 302, location: `${BASE}/a` },
      { url: `${BASE}/a`, statusCode: 301, location: `${BASE}/b` }
    ], record);
    expect(chain.loopDetected).toBe(true);
  });
});

describe('legacy fallbacks', () => {
  it('synthesizes one hop from redirectTargetUrl when no chain row exists', () => {
    const chain = normalizeRedirectChain([], { ...record, redirectTargetUrl: `${BASE}/redirect/chain-2` });
    expect(chain.hops).toHaveLength(1);
    expect(chain.hops[0]).toEqual({
      ordinal: 1,
      url: record.canonicalUrl,
      toUrl: `${BASE}/redirect/chain-2`,
      statusCode: 301
    });
    expect(chain.finalUrl).toBe(`${BASE}/redirect/chain-2`);
  });

  it('flags a self-target as a loop', () => {
    const chain = normalizeRedirectChain(null, { ...record, redirectTargetUrl: record.canonicalUrl });
    expect(chain.loopDetected).toBe(true);
  });

  it('anchors an absent chain on the record itself', () => {
    for (const absent of [null, undefined, []]) {
      const chain = normalizeRedirectChain(absent, record);
      expect(chain.hops).toHaveLength(0);
      expect(chain.finalUrl).toBe(record.canonicalUrl);
      expect(chain.finalStatusCode).toBe(301);
      expect(chain.loopDetected).toBe(false);
      expect(chain.chainCapExceeded).toBe(false);
    }
  });
});
