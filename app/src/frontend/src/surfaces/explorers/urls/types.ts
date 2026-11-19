/**
 * URL explorer wire-consumption types (WP-09-09, FS-CRAWL-002 §8.2 + §15.6).
 *
 * The inventory binds the locked `audits.listUrls` route (UrlRecordSummary
 * rows, UrlListQuery filters). The detail panel consumes `audits.getUrl`
 * (UrlDetailWire) plus the additive redirect-chain payload WP-09-06 maps
 * from url_elements storage: {hops:[{ordinal,url,toUrl,statusCode}],
 * finalUrl, finalStatusCode, loopDetected, chainCapExceeded}. Both the rich
 * object form and the legacy flat RedirectHopWire[] form normalize to one
 * rendering model — the panel never reads crawl storage directly.
 *
 * @module surfaces/explorers/urls/types
 */

import type {
  HttpStatusClass,
  UrlDetail,
  RedirectHop
} from '../../../contracts/api/audits';

/**
 * One hop of the rich redirect-chain wire. `toUrl` is the storage-mapped
 * target name; `location` is the legacy RedirectHopWire(Url, StatusCode,
 * Location) field — both are accepted.
 */
export interface RedirectChainHopWire {
  readonly ordinal?: number;
  readonly url: string;
  readonly toUrl?: string | null;
  readonly statusCode: number;
  readonly location?: string | null;
}

/** Rich redirect-chain payload (url_elements redirect-chain group → wire). */
export interface RedirectChainObjectWire {
  readonly hops?: readonly RedirectChainHopWire[];
  readonly finalUrl?: string | null;
  readonly finalStatusCode?: number | null;
  readonly loopDetected?: boolean;
  readonly chainCapExceeded?: boolean;
}

/**
 * Every redirect-chain wire form the detail panel consumes:
 *  - the rich object payload (WP-09-06 storage → wire mapping);
 *  - the current flat RedirectHopWire[] (Url, StatusCode, Location);
 *  - a flat rich-hop array;
 *  - absent (legacy single-hop redirectTargetUrl fallback applies).
 */
export type RedirectChainWire =
  | RedirectChainObjectWire
  | readonly RedirectHop[]
  | readonly RedirectChainHopWire[]
  | null
  | undefined;

/**
 * Additive url_elements row as the detail wire may carry it: either the
 * parsed payload object or the raw payloadJson string (storage form).
 */
export interface UrlElementRowWire {
  readonly elementGroup: string;
  readonly payloadJson?: string;
  readonly payload?: Readonly<Record<string, unknown>>;
}

/**
 * UrlDetail plus the additive fields the detail panel consumes. The locked
 * contract's flat `redirectChain` widens to every accepted wire form; the
 * element rows / legacy target / assets record are additive optional fields
 * (never required — the panel degrades honestly without them).
 */
export type UrlDetailWireView = Omit<UrlDetail, 'redirectChain'> & {
  readonly redirectChain?: RedirectChainWire;
  /** Legacy single-hop fallback (UrlRecordDto.RedirectTargetUrl). */
  readonly redirectTargetUrl?: string | null;
  /** Additive: url_elements rows (document_head, header_directives, …). */
  readonly elements?: readonly UrlElementRowWire[];
  /** Additive: meta description when not carried by document_head. */
  readonly metaDescription?: string | null;
  /** Additive: asset lists keyed by kind (image/css/js/font/document/…). */
  readonly assets?: Readonly<Record<string, unknown>>;
};

/** One normalized hop for hop-by-hop rendering. */
export interface NormalizedRedirectHop {
  readonly ordinal: number;
  readonly url: string;
  readonly toUrl: string | null;
  readonly statusCode: number;
}

/** Normalized chain: one rendering model for every accepted wire form. */
export interface NormalizedRedirectChain {
  readonly hops: readonly NormalizedRedirectHop[];
  readonly finalUrl: string | null;
  readonly finalStatusCode: number | null;
  readonly loopDetected: boolean;
  readonly chainCapExceeded: boolean;
}

/**
 * Advanced filter state. Every field maps 1:1 to the locked UrlListQuery
 * server filters (statusClass / indexable / depth / contentType) — the grid
 * never filters client-side.
 */
export interface UrlExplorerFilters {
  readonly statusClass?: HttpStatusClass | null;
  /** CR-024: SEARCH-indexable (noindex pages are fetchable, not indexable). */
  readonly indexable?: boolean | null;
  readonly depth?: number | null;
  readonly contentType?: string | null;
}

/** Drill-down handoff target — the onOpenUrl callback payload. */
export interface UrlOpenTarget {
  readonly auditId: string;
  readonly urlId: string;
  readonly canonicalUrl: string;
}

/** One canonical target with its provenance (HTML vs HTTP header). */
export interface CanonicalTarget {
  readonly value: string;
  readonly source: 'html' | 'header' | 'finding';
}

/** One hreflang alternate with its provenance. */
export interface HreflangAlternate {
  readonly hreflang: string;
  readonly href: string;
  readonly source: 'html' | 'header';
}

/**
 * Derived element-inventory facts. CR-024 semantics: `noindex` marks a page
 * that is fetchable but NOT search-indexable; the inventory column displays
 * the engine's `indexable` verdict, this panel shows the directive evidence.
 */
export interface InventoryFacts {
  readonly title: string | null;
  readonly metaDescription: string | null;
  readonly metaRobots: string | null;
  readonly xRobotsTag: string | null;
  readonly noindex: boolean;
  readonly canonicals: readonly CanonicalTarget[];
  readonly hreflang: readonly HreflangAlternate[];
  readonly headerLink: string | null;
}

/** One asset entry in the detail asset list. */
export interface AssetEntry {
  readonly url: string;
  readonly kind: string;
  readonly detail?: string | null;
}

/** document_head payload shape (UrlElementExtractor wire, camelCase). */
export interface DocumentHeadPayload {
  readonly title?: string | null;
  readonly metaDescription?: string | null;
  readonly metaRobots?: string | null;
  readonly viewport?: string | null;
  readonly charset?: string | null;
  readonly canonicalUrl?: string | null;
  readonly alternates?: readonly { readonly hreflang?: string | null; readonly href?: string | null }[];
  readonly openGraph?: Readonly<Record<string, unknown>> | null;
  readonly twitterCard?: Readonly<Record<string, unknown>> | null;
  readonly favicons?: readonly string[];
}
