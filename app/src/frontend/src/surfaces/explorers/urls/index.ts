/**
 * URL explorer surface barrel (WP-09-09).
 *
 * Mounting handoff (WP-09-11): import { UrlExplorer } from
 * './surfaces/explorers/urls'; mount inside the report workspace (root
 * testid `url-explorer` satisfies walker UI-13 + UI-11) and add the report
 * nav entry labelled exactly "URL explorer". No store/apiClient additions
 * are required — the surface consumes the locked `audits.listUrls` and
 * `audits.getUrl` routes through the existing typed loopback client.
 *
 * @module surfaces/explorers/urls
 */

export { UrlExplorer, type UrlExplorerProps } from './UrlExplorer';
export { UrlDetailPanel, type UrlDetailPanelProps } from './UrlDetailPanel';
export { normalizeRedirectChain, type ChainRecordFacts } from './redirectChain';
export {
  deriveInventoryFacts,
  deriveAssets,
  elementPayload,
  parseLinkHeader,
  type LinkHeaderEntry
} from './inventory';
export type {
  RedirectChainHopWire,
  RedirectChainObjectWire,
  RedirectChainWire,
  UrlElementRowWire,
  UrlDetailWireView,
  NormalizedRedirectHop,
  NormalizedRedirectChain,
  UrlExplorerFilters,
  UrlOpenTarget,
  CanonicalTarget,
  HreflangAlternate,
  InventoryFacts,
  AssetEntry,
  DocumentHeadPayload
} from './types';
