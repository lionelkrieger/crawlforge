/**
 * Redirect-chain normalization (WP-09-09, FS-CRAWL-002 §8.2 URL detail).
 *
 * One pure function folds every accepted wire form into the hop-by-hop
 * rendering model:
 *  - rich object payload {hops, finalUrl, finalStatusCode, loopDetected,
 *    chainCapExceeded} (WP-09-06 storage → wire mapping);
 *  - flat RedirectHopWire[] (Url, StatusCode, Location) — the current wire;
 *  - flat rich-hop arrays ({ordinal, url, toUrl, statusCode});
 *  - absent chain + legacy redirectTargetUrl → one synthetic hop.
 *
 * Loop/cap badges prefer the engine's verdicts; when the wire omits them
 * (legacy flat form) a repeated hop URL is flagged as a display-level loop
 * so the operator still sees the cycle. No crawl storage is read here.
 *
 * @module surfaces/explorers/urls/redirectChain
 */

import type {
  NormalizedRedirectChain,
  NormalizedRedirectHop,
  RedirectChainHopWire,
  RedirectChainObjectWire,
  RedirectChainWire
} from './types';

/** Record facts used for the legacy fallback + final-target defaults. */
export interface ChainRecordFacts {
  readonly canonicalUrl: string;
  readonly statusCode: number;
  readonly redirectTargetUrl?: string | null;
}

/**
 * True when the chain revisits a URL: a repeated hop URL, or a target that
 * points at the current hop or any earlier one (pointing FORWARD at the
 * next hop is the normal chain shape, not a cycle).
 */
function hasRepeatedUrl(hops: readonly NormalizedRedirectHop[]): boolean {
  const firstIndex = new Map<string, number>();
  for (let i = 0; i < hops.length; i += 1) {
    const url = hops[i].url;
    if (firstIndex.has(url)) return true;
    firstIndex.set(url, i);
  }
  for (let i = 0; i < hops.length; i += 1) {
    const target = hops[i].toUrl;
    if (!target) continue;
    const targetIndex = firstIndex.get(target);
    if (targetIndex !== undefined && targetIndex <= i) return true;
  }
  return false;
}

/** Narrows the rich object form (non-array object with optional hops). */
function isChainObject(chain: RedirectChainWire): chain is RedirectChainObjectWire {
  return typeof chain === 'object' && chain !== null && !Array.isArray(chain);
}

/** Normalizes one raw hop entry (rich or legacy field names). */
function normalizeHop(
  raw: RedirectChainHopWire,
  index: number,
  next: RedirectChainHopWire | undefined
): NormalizedRedirectHop {
  const toUrl = raw.toUrl ?? raw.location ?? next?.url ?? null;
  return {
    ordinal: raw.ordinal ?? index + 1,
    url: raw.url,
    toUrl: toUrl && toUrl.length > 0 ? toUrl : null,
    statusCode: raw.statusCode
  };
}

/**
 * Folds any accepted redirect-chain wire form into the rendering model.
 * Pure and total: unknown/absent chains degrade to the legacy single-hop
 * fallback or an empty chain anchored on the record itself.
 */
export function normalizeRedirectChain(
  chain: RedirectChainWire,
  record: ChainRecordFacts
): NormalizedRedirectChain {
  // ── Rich object payload (WP-09-06) ──────────────────────────────────────
  if (isChainObject(chain)) {
    const rawHops = chain.hops ?? [];
    const hops = rawHops.map((hop, index) => normalizeHop(hop, index, rawHops[index + 1]));
    const last = hops[hops.length - 1];
    return {
      hops,
      finalUrl: chain.finalUrl ?? last?.toUrl ?? (hops.length > 0 ? record.canonicalUrl : null),
      finalStatusCode: chain.finalStatusCode ?? (hops.length > 0 ? record.statusCode : null),
      loopDetected: chain.loopDetected ?? hasRepeatedUrl(hops),
      chainCapExceeded: chain.chainCapExceeded ?? false
    };
  }

  // ── Flat array (current RedirectHopWire[] or flat rich hops) ───────────
  if (Array.isArray(chain) && chain.length > 0) {
    const rawHops = chain as readonly RedirectChainHopWire[];
    const hops = rawHops.map((hop, index) => normalizeHop(hop, index, rawHops[index + 1]));
    const last = hops[hops.length - 1];
    return {
      hops,
      finalUrl: last?.toUrl ?? record.canonicalUrl,
      finalStatusCode: record.statusCode,
      loopDetected: hasRepeatedUrl(hops),
      chainCapExceeded: false
    };
  }

  // ── Legacy single-hop fallback (redirectTargetUrl) ─────────────────────
  if (record.redirectTargetUrl) {
    const hop: NormalizedRedirectHop = {
      ordinal: 1,
      url: record.canonicalUrl,
      toUrl: record.redirectTargetUrl,
      statusCode: record.statusCode
    };
    return {
      hops: [hop],
      finalUrl: record.redirectTargetUrl,
      finalStatusCode: record.statusCode,
      loopDetected: record.redirectTargetUrl === record.canonicalUrl,
      chainCapExceeded: false
    };
  }

  // ── No chain: the record is its own final target ───────────────────────
  return {
    hops: [],
    finalUrl: record.canonicalUrl,
    finalStatusCode: record.statusCode,
    loopDetected: false,
    chainCapExceeded: false
  };
}
