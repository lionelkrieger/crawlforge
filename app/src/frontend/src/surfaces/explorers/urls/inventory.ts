/**
 * Element-inventory derivation (WP-09-09, FS-CRAWL-002 §8.2 + §15.6).
 *
 * Pure functions fold the detail wire into display facts:
 *  - document_head payload (title, metaDescription, metaRobots, canonicalUrl,
 *    alternates) when the additive element rows are present;
 *  - header directives (X-Robots-Tag, Link) from responseHeaders;
 *  - finding evidence (indexability.noindex, canonical/header-html-conflict)
 *    as the fallback provenance when element rows are absent.
 *
 * CR-024: `noindex` marks a fetchable page that is NOT search-indexable.
 * Both canonical targets (HTML + HTTP header) survive derivation so the
 * conflict panel can show them side by side.
 *
 * @module surfaces/explorers/urls/inventory
 */

import type {
  AssetEntry,
  CanonicalTarget,
  DocumentHeadPayload,
  HreflangAlternate,
  InventoryFacts,
  UrlDetailWireView,
  UrlElementRowWire
} from './types';

/** Parses one element row's payload (object form or raw payloadJson). */
export function elementPayload(row: UrlElementRowWire): Record<string, unknown> {
  if (row.payload && typeof row.payload === 'object') return row.payload as Record<string, unknown>;
  if (typeof row.payloadJson === 'string' && row.payloadJson.length > 0) {
    try {
      const parsed: unknown = JSON.parse(row.payloadJson);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return parsed as Record<string, unknown>;
      }
    } catch {
      // Malformed storage payload degrades to an empty inventory row.
    }
  }
  return {};
}

/** Finds the first element row of a group and parses its payload. */
function groupPayload(detail: UrlDetailWireView, group: string): Record<string, unknown> | null {
  const row = detail.elements?.find((entry) => entry.elementGroup === group);
  return row ? elementPayload(row) : null;
}

/** Case-insensitive response-header lookup (wire keys are lowercase). */
function headerValue(detail: UrlDetailWireView, name: string): string | null {
  const headers = detail.responseHeaders ?? {};
  const lower = name.toLowerCase();
  for (const [key, value] of Object.entries(headers)) {
    if (key.toLowerCase() === lower && typeof value === 'string' && value.length > 0) return value;
  }
  return null;
}

/** One parsed Link-header entry. */
export interface LinkHeaderEntry {
  readonly url: string;
  readonly rel: string | null;
  readonly hreflang: string | null;
}

/**
 * Parses an HTTP Link header into entries: `<url>; rel="canonical";
 * hreflang="en"`. Tolerates single quotes, unquoted params, and commas
 * inside angle brackets.
 */
export function parseLinkHeader(value: string): readonly LinkHeaderEntry[] {
  const entries: LinkHeaderEntry[] = [];
  const pattern = /<([^>]*)>\s*((?:;\s*[^;,]+)*)/g;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(value)) !== null) {
    const url = match[1].trim();
    if (!url) continue;
    const params = match[2] ?? '';
    const rel = linkParam(params, 'rel')?.toLowerCase() ?? null;
    const hreflang = linkParam(params, 'hreflang');
    entries.push({ url, rel, hreflang });
  }
  return entries;
}

/** Reads one Link-header param (double-quoted, single-quoted, or bare). */
function linkParam(params: string, name: string): string | null {
  const pattern = new RegExp(`${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)'|([^";,\\s]+))`, 'i');
  const match = pattern.exec(params);
  if (!match) return null;
  const value = (match[1] ?? match[2] ?? match[3] ?? '').trim();
  return value.length > 0 ? value : null;
}

/** Extracts a canonical-looking URL from a conflict-evidence value. */
function canonicalFromEvidence(value: string | null | undefined): string | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (trimmed.length === 0) return null;
  // Link-header form: </pages/target.html>; rel="canonical"
  const link = parseLinkHeader(trimmed);
  const canonicalEntry = link.find((entry) => entry.rel === 'canonical');
  if (canonicalEntry) return canonicalEntry.url;
  if (link.length > 0 && /canonical/i.test(trimmed)) return link[0].url;
  // Bare URL form (HTML href evidence).
  if (/^https?:\/\//i.test(trimmed) || trimmed.startsWith('/')) return trimmed;
  return null;
}

/** True when a directive string carries a noindex token. */
function hasNoindexToken(value: string | null): boolean {
  return value !== null && /(^|[\s,])noindex([\s,]|$)/i.test(value);
}

/**
 * Derives the display inventory facts from the detail wire. Prefers the
 * document_head element payload; falls back to header directives and
 * finding evidence so the current (pre-element-row) wire still renders
 * noindex + dual canonical targets honestly.
 */
export function deriveInventoryFacts(detail: UrlDetailWireView): InventoryFacts {
  const head = (groupPayload(detail, 'document_head') ?? {}) as DocumentHeadPayload;
  const headerDirectives = groupPayload(detail, 'header_directives');

  const title = (typeof head.title === 'string' && head.title.length > 0 ? head.title : null) ?? detail.title ?? null;
  const metaDescription =
    (typeof head.metaDescription === 'string' && head.metaDescription.length > 0 ? head.metaDescription : null) ??
    detail.metaDescription ??
    null;
  const metaRobots = typeof head.metaRobots === 'string' && head.metaRobots.length > 0 ? head.metaRobots : null;

  const xRobotsTag =
    headerValue(detail, 'x-robots-tag') ??
    (typeof headerDirectives?.xRobotsTag === 'string' && (headerDirectives.xRobotsTag as string).length > 0
      ? (headerDirectives.xRobotsTag as string)
      : null);
  const headerLink =
    headerValue(detail, 'link') ??
    (typeof headerDirectives?.link === 'string' && (headerDirectives.link as string).length > 0
      ? (headerDirectives.link as string)
      : null);

  // noindex: HTML meta OR X-Robots-Tag OR the engine's noindex finding.
  const noindexFinding = (detail.findings ?? []).some(
    (finding) =>
      finding.ruleKey === 'indexability.noindex' ||
      (finding.evidence?.details?.directive === 'noindex' && finding.ruleKey.startsWith('indexability.'))
  );
  const noindex = hasNoindexToken(metaRobots) || hasNoindexToken(xRobotsTag) || noindexFinding;

  // Canonical targets: HTML payload + header Link + conflict evidence.
  const canonicals: CanonicalTarget[] = [];
  const pushCanonical = (value: string | null, source: CanonicalTarget['source']) => {
    if (!value || value.length === 0) return;
    if (canonicals.some((entry) => entry.value === value)) return;
    canonicals.push({ value, source });
  };
  pushCanonical(typeof head.canonicalUrl === 'string' ? head.canonicalUrl : null, 'html');
  for (const entry of headerLink ? parseLinkHeader(headerLink) : []) {
    if (entry.rel === 'canonical') pushCanonical(entry.url, 'header');
  }
  for (const conflict of detail.headerConflicts ?? []) {
    pushCanonical(canonicalFromEvidence(conflict.headerValue), 'header');
    pushCanonical(canonicalFromEvidence(conflict.htmlValue), 'html');
  }

  // hreflang alternates: HTML payload + header Link entries.
  const hreflang: HreflangAlternate[] = [];
  const pushAlternate = (lang: string | null | undefined, href: string | null | undefined, source: HreflangAlternate['source']) => {
    if (!lang || !href) return;
    if (hreflang.some((entry) => entry.hreflang === lang && entry.href === href)) return;
    hreflang.push({ hreflang: lang, href, source });
  };
  for (const alternate of head.alternates ?? []) {
    pushAlternate(alternate.hreflang, alternate.href, 'html');
  }
  for (const entry of headerLink ? parseLinkHeader(headerLink) : []) {
    if (entry.rel === 'alternate' && entry.hreflang) pushAlternate(entry.hreflang, entry.url, 'header');
  }

  return { title, metaDescription, metaRobots, xRobotsTag, noindex, canonicals, hreflang, headerLink };
}

/** Normalizes one raw asset value (string URL or {url|src|href} object). */
function assetUrl(value: unknown): string | null {
  if (typeof value === 'string' && value.length > 0) return value;
  if (value && typeof value === 'object') {
    const record = value as Record<string, unknown>;
    for (const key of ['url', 'src', 'href']) {
      if (typeof record[key] === 'string' && (record[key] as string).length > 0) return record[key] as string;
    }
  }
  return null;
}

/**
 * Derives the asset list: the additive `assets` record (kind → entries)
 * plus url_elements image/resource rows. Deduplicated by URL + kind.
 */
export function deriveAssets(detail: UrlDetailWireView): readonly AssetEntry[] {
  const entries: AssetEntry[] = [];
  const seen = new Set<string>();
  const push = (url: string | null, kind: string, assetDetail?: string | null) => {
    if (!url) return;
    const key = `${kind}|${url}`;
    if (seen.has(key)) return;
    seen.add(key);
    entries.push({ url, kind, ...(assetDetail ? { detail: assetDetail } : {}) });
  };

  for (const [kind, value] of Object.entries(detail.assets ?? {})) {
    if (Array.isArray(value)) {
      for (const item of value) push(assetUrl(item), kind);
    } else {
      push(assetUrl(value), kind);
    }
  }

  const ELEMENT_KIND: Readonly<Record<string, string>> = {
    images: 'image',
    image: 'image',
    scripts: 'js',
    stylesheets: 'css',
    fonts: 'font',
    resources: 'resource',
    documents: 'document'
  };
  for (const row of detail.elements ?? []) {
    const kind = ELEMENT_KIND[row.elementGroup];
    if (!kind) continue;
    const payload = elementPayload(row);
    const alt = typeof payload.alt === 'string' ? payload.alt : null;
    push(assetUrl(payload), kind, alt);
  }

  return entries;
}
