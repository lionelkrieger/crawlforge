/**
 * Explorer surface contract types (FS-CRAWL-002 §8.3 — all 11 explorers).
 *
 * Every explorer is a declarative definition bound to ONE frozen route:
 * columns, query-builder fields, row identity, optional view tabs, summary
 * strip, and drill-down. The generic ExplorerHost renders any definition
 * through the same server-windowed grid engine — no explorer materializes
 * its dataset.
 *
 * @module surfaces/explorers/explorerTypes
 */

import type { RowData } from '@tanstack/react-table';
import type { ReactNode } from 'react';
import type { ApiRouteKey } from '../../contracts/api/routes';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type { GridColumnDef, GridRowKey, GridSortEntry } from '../../grid/types';
import type { QueryFieldDef } from '../../grid/queryBuilder';

/** The eleven explorer identifiers (FS-CRAWL-002 §8.3). */
export type ExplorerId =
  | 'urls'
  | 'issues'
  | 'links'
  | 'extraction'
  | 'ngrams'
  | 'logs'
  | 'semantics'
  | 'geo'
  | 'analytics'
  | 'redirect-matrix'
  | 'waf';

/** Context passed to column/field builders. */
export interface ExplorerColumnContext {
  /** Dynamic column names from the latest fetched page (extraction/snippet). */
  readonly dynamicColumns: readonly string[];
  /** Active view tab id, when the explorer has views. */
  readonly viewId: string | null;
}

/** One view tab: a fixed domain-filter preset over the same route. */
export interface ExplorerView {
  readonly id: string;
  readonly label: string;
  /** Domain filters merged under every window request in this view. */
  readonly query: Readonly<Record<string, unknown>>;
}

/** One summary chip rendered above the grid. */
export interface SummaryChip {
  readonly label: string;
  readonly value: string;
  readonly tone?: 'neutral' | 'success' | 'warning' | 'danger' | 'info';
}

/** Drill-down specification for a row activation. */
export interface DrilldownSpec {
  readonly title: string;
  readonly subtitle?: string;
  readonly fields: readonly { readonly label: string; readonly value: ReactNode }[];
  /** Optional detail fetch appended to the drawer (URL detail, evidence…). */
  readonly fetchDetail?: (api: LoopbackApiClient, auditId: string) => Promise<DrilldownDetail | null>;
}

/** Fetched drill-down detail sections. */
export interface DrilldownDetail {
  readonly sections: readonly {
    readonly heading: string;
    readonly fields: readonly { readonly label: string; readonly value: ReactNode }[];
  }[];
}

/** How the route payload maps onto grid rows. */
export type ExplorerAdapterKind = 'array' | 'dynamic' | 'search' | 'correlation' | 'analytics';

/** Declarative explorer definition (generic over its locked row DTO). */
export interface ExplorerDefinition<TRow extends RowData = RowData> {
  readonly id: ExplorerId;
  readonly title: string;
  readonly glyph: string;
  readonly tagline: string;
  readonly route: ApiRouteKey;
  readonly adapterKind: ExplorerAdapterKind;
  readonly columns: (context: ExplorerColumnContext) => readonly GridColumnDef<TRow>[];
  readonly queryFields: (context: ExplorerColumnContext) => readonly QueryFieldDef[];
  readonly rowKey: GridRowKey<TRow>;
  readonly views?: readonly ExplorerView[];
  readonly defaultSort?: readonly GridSortEntry[];
  /** Optional one-shot summary strip (fetched on mount/view change). */
  readonly fetchSummary?: (api: LoopbackApiClient, auditId: string) => Promise<readonly SummaryChip[]>;
  /** Row activation → drill-down drawer. */
  readonly drilldown?: (row: TRow) => DrilldownSpec | null;
  /** Extra note shown when the explorer needs a precondition (e.g. matrix upload). */
  readonly emptyHint?: string;
}
