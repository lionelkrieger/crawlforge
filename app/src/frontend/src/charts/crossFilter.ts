/**
 * Chart cross-filtering (FS-CRAWL-001 §8.3: interactive chart filters
 * propagate to the underlying lists). Pure selection → locked ListQuery
 * mappings plus a dispatcher that drives the ACCEPTED store actions —
 * the store itself is never edited. Wire values are lowercase-no-hyphen
 * (status classes keep their '2xx' form).
 *
 * @module charts/crossFilter
 */

import type {
  FindingListQuery,
  LinkListQuery,
  UrlListQuery
} from '../contracts/api/audits';
import type { NgramListQuery } from '../contracts/api/ngrams';
import type { WafBlockListQuery } from '../contracts/api/waf';
import type { CrawlForgeStoreActions } from '../store/crawlForgeStore';

/** A chart selection that can cross-filter an explorer. */
export type ChartSelection =
  | { readonly kind: 'status'; readonly statusClass: string }
  | { readonly kind: 'severity'; readonly severity: string }
  | { readonly kind: 'category'; readonly category: string }
  | { readonly kind: 'waf-provider'; readonly provider: string }
  | { readonly kind: 'ngram-zone'; readonly zone: string }
  | { readonly kind: 'host'; readonly host: string };

/** URL-explorer query for a selection, or null when not applicable. */
export function selectionToUrlQuery(selection: ChartSelection): UrlListQuery | null {
  switch (selection.kind) {
    case 'status':
      return { statusClass: selection.statusClass as UrlListQuery['statusClass'] };
    case 'waf-provider':
      return { wafProvider: selection.provider };
    default:
      return null;
  }
}

/** Issue-explorer query for a selection, or null when not applicable. */
export function selectionToFindingQuery(selection: ChartSelection): FindingListQuery | null {
  switch (selection.kind) {
    case 'severity':
      return { severity: selection.severity };
    case 'category':
      return { category: selection.category };
    default:
      return null;
  }
}

/** Link-explorer query for a selection (DOM-zone filter), or null. */
export function selectionToLinkQuery(selection: ChartSelection): LinkListQuery | null {
  return selection.kind === 'ngram-zone' ? { domZone: selection.zone } : null;
}

/** N-gram explorer query for a selection, or null. */
export function selectionToNgramQuery(selection: ChartSelection): NgramListQuery | null {
  return selection.kind === 'ngram-zone' ? { zone: selection.zone } : null;
}

/** WAF block-list query for a selection, or null. */
export function selectionToWafQuery(selection: ChartSelection): WafBlockListQuery | null {
  return selection.kind === 'waf-provider' ? { provider: selection.provider } : null;
}

/** Explorer targets a selection reaches (for UI affordances/tests). */
export function selectionTargets(selection: ChartSelection): readonly string[] {
  const targets: string[] = [];
  if (selectionToUrlQuery(selection)) targets.push('urls');
  if (selectionToFindingQuery(selection)) targets.push('findings');
  if (selectionToLinkQuery(selection)) targets.push('links');
  if (selectionToNgramQuery(selection)) targets.push('ngrams');
  if (selectionToWafQuery(selection)) targets.push('waf-blocks');
  return targets;
}

/**
 * Applies a chart selection through the accepted store actions: loads the
 * matching explorer window(s) with the mapped server-side filters. Loaders
 * degrade to error outcomes offline — this never throws.
 */
export async function applyChartCrossFilter(
  actions: CrawlForgeStoreActions,
  auditId: string,
  selection: ChartSelection
): Promise<void> {
  const urlQuery = selectionToUrlQuery(selection);
  const findingQuery = selectionToFindingQuery(selection);
  const linkQuery = selectionToLinkQuery(selection);
  const ngramQuery = selectionToNgramQuery(selection);

  const loads: Promise<unknown>[] = [];
  if (urlQuery) loads.push(actions.loadUrlPage(auditId, urlQuery));
  if (findingQuery) loads.push(actions.loadFindingPage(auditId, findingQuery));
  if (linkQuery) loads.push(actions.loadLinkPage(auditId, linkQuery));
  if (ngramQuery) loads.push(actions.loadNgramPage(auditId, ngramQuery));
  await Promise.all(loads);
}
