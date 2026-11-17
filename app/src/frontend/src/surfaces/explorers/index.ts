/**
 * Explorer registry — all eleven surfaces (FS-CRAWL-002 §8.3), in shell
 * navigation order. Definitions are typed against their locked row DTOs;
 * the registry erases to the host's RowData surface with one documented
 * variance cast (columns are covariant producers; the host only reads).
 *
 * @module surfaces/explorers
 */

import type { RowData } from '@tanstack/react-table';
import type { ExplorerDefinition, ExplorerId } from './explorerTypes';
import { urlExplorer, issueExplorer, linkExplorer, extractionExplorer, ngramExplorer } from './coreExplorers';
import { logExplorer, semanticExplorer, geoExplorer, analyticsExplorer, redirectMatrixExplorer, wafExplorer } from './analysisExplorers';

export * from './explorerTypes';
export * from './cells';
export * from './columnUtils';
export * from './ExplorerHost';
export { urlExplorer, issueExplorer, linkExplorer, extractionExplorer, ngramExplorer } from './coreExplorers';
export {
  logExplorer, semanticExplorer, geoExplorer, analyticsExplorer,
  redirectMatrixExplorer, wafExplorer, logSummaryChips, consentChips
} from './analysisExplorers';

/**
 * Erases a concretely-typed definition to the host surface. This is the
 * single documented variance boundary: TanStack's AccessorFn makes
 * columns() invariant in T, while the host only consumes producers over
 * RowData. Row DTOs remain locked inside each definition.
 */
export function eraseExplorerDefinition<TRow extends RowData>(definition: ExplorerDefinition<TRow>): ExplorerDefinition {
  return definition as unknown as ExplorerDefinition;
}

/** All eleven explorers in navigation order. */
export const EXPLORERS: readonly ExplorerDefinition[] = [
  eraseExplorerDefinition(urlExplorer),
  eraseExplorerDefinition(issueExplorer),
  eraseExplorerDefinition(linkExplorer),
  eraseExplorerDefinition(extractionExplorer),
  eraseExplorerDefinition(ngramExplorer),
  eraseExplorerDefinition(logExplorer),
  eraseExplorerDefinition(semanticExplorer),
  eraseExplorerDefinition(geoExplorer),
  eraseExplorerDefinition(analyticsExplorer),
  eraseExplorerDefinition(redirectMatrixExplorer),
  eraseExplorerDefinition(wafExplorer)
];

/** Lookup by explorer id. */
export const EXPLORER_BY_ID: ReadonlyMap<ExplorerId, ExplorerDefinition> = new Map(
  EXPLORERS.map((definition) => [definition.id, definition])
);

/** Navigation metadata (rail labels + glyphs). */
export const EXPLORER_NAV: readonly { readonly id: ExplorerId; readonly label: string; readonly glyph: string }[] =
  EXPLORERS.map((definition) => ({ id: definition.id, label: definition.title, glyph: definition.glyph }));
