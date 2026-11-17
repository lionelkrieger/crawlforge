/**
 * Grid engine contract types (FS-CRAWL-002 §7.1, §8.3).
 *
 * The grid is a SERVER-WINDOWED row model: the renderer never holds the full
 * dataset. Rows arrive as offset/limit pages (default 200) through the typed
 * loopback client; a bounded LRU page cache (≤20 pages ≈ 4,000 rows) is the
 * only resident row memory. The virtualizer maps scroll position to global
 * row indices; the DOM mounts at most MAX_DOM_ROWS rows (target band 30–50).
 *
 * @module grid/types
 */

import type { LegacyColumnDef } from '@tanstack/react-table/legacy';
import type { RowData } from '@tanstack/react-table';
import type { ListQuery, SortOrder } from '../contracts/common/pagination';
import type { QueryRuleNode } from '../contracts/api/queries';

/** Hard DOM row cap including overscan (FS-CRAWL-002 §7.1, criterion #40). */
export const MAX_DOM_ROWS = 50;

/** Target mounted-row band (viewport rows + small overscan). */
export const TARGET_BAND_MIN = 30;

/** Default server page size (§7.1: default page size 200). */
export const GRID_PAGE_SIZE = 200;

/** Resident page-cache bound (§7.1: ≤20 pages ≈ 4,000 rows, LRU eviction). */
export const GRID_MAX_PAGES = 20;

/** Instant-filter debounce in ms (§7.1: 150–300 ms band). */
export const FILTER_DEBOUNCE_MS = 200;

/** Bounded column pin count (§7.1: default ≤4). */
export const MAX_PINNED_COLUMNS = 4;

/** Server query-tree whitelist depth (§8.7 query builder). */
export const MAX_QUERY_DEPTH = 16;

/** Default overscan rows above/below the viewport. */
export const DEFAULT_OVERSCAN = 6;

/** Bounded dynamic row-measurement cache (§7.1 row geometry). */
export const MAX_MEASURED_ROWS = 512;

/** Density modes (§8.3 interaction checklist). */
export type GridDensity = 'compact' | 'normal' | 'comfortable';

/** Row heights per density (estimated; dynamic measurement refines). */
export const DENSITY_ROW_HEIGHT: Record<GridDensity, number> = {
  compact: 28,
  normal: 36,
  comfortable: 44
};

/** One entry of the multi-column sort chain (executed server-side). */
export interface GridSortEntry {
  readonly field: string;
  readonly order: SortOrder;
}

/** Column pinning side. */
export type GridPinSide = 'left' | 'right';

/**
 * Grid column definition: TanStack column def plus grid metadata (server
 * sort field, pinning, dynamic-column origin, plugin formatter key).
 */
export interface GridColumnMeta {
  /** Server-side sort/filter field name (defaults to the column id). */
  readonly serverField?: string;
  /** Column is eligible for the query builder field list. */
  readonly queryable?: boolean;
  /** Value kind for query-builder operator/value editors. */
  readonly valueKind?: 'text' | 'number' | 'boolean' | 'enum';
  /** Enum choices when valueKind is 'enum'. */
  readonly enumValues?: readonly string[];
  /** Dynamic extraction/snippet column origin (rule or snippet key). */
  readonly dynamicOrigin?: string;
  /** Plugin formatter contribution key (WP-06-06 consumes). */
  readonly formatterKey?: string;
  /** Numeric columns render tabular and right-aligned. */
  readonly numeric?: boolean;
}

/** A grid column: TanStack def with CrawlForge metadata. */
export type GridColumnDef<TData extends RowData> = LegacyColumnDef<TData> & {
  readonly meta?: GridColumnMeta;
};

/**
 * Server list query as the grid composes it. Identical to the locked
 * ListQuery except sort/order may repeat for multi-column sorting — the
 * sanctioned wire expression per contracts/common/pagination ("Multi-column
 * sorting is expressed by repeating the sort field server-side").
 */
export interface ServerListQuery extends Omit<ListQuery, 'sort' | 'order'> {
  readonly sort?: string | readonly string[];
  readonly order?: SortOrder | readonly SortOrder[];
  readonly [filter: string]: unknown;
}

/** One fetched page of rows plus the server total. */
export interface GridPage<TRow> {
  readonly offset: number;
  readonly limit: number;
  readonly total: number;
  readonly rows: readonly TRow[];
  /** Dynamic column order when the dataset carries one (extraction/snippet). */
  readonly columns?: readonly string[];
  /** Response-level metadata (log correlation summary, consent rollup, …). */
  readonly meta?: Readonly<Record<string, unknown>>;
  fetchedAt: number;
}

/** Page fetch outcome — errors are values, never throws (offline-first). */
export type GridFetchResult<TRow> =
  | { readonly ok: true; readonly page: GridPage<TRow> }
  | { readonly ok: false; readonly errorCode: string; readonly errorMessage: string };

/**
 * Explorer data source: fetches one server window. Implementations wrap the
 * typed loopback client; the grid engine never fetches outside this surface.
 */
export interface GridDataSource<TRow> {
  /** Stable identity for cache partitioning (route + fixed params). */
  readonly sourceKey: string;
  fetchPage(query: ServerListQuery, signal?: AbortSignal): Promise<GridFetchResult<TRow>>;
}

/** Complete grid state the engine manages (serializable for export handoff). */
export interface GridState {
  readonly sortChain: readonly GridSortEntry[];
  readonly filterText: string;
  readonly ruleTree: QueryRuleNode | null;
  readonly density: GridDensity;
  readonly pinned: Readonly<Record<string, GridPinSide>>;
  readonly selectedRowKeys: readonly string[];
  readonly columnOrder: readonly string[];
  readonly columnWidths: Readonly<Record<string, number>>;
}

/** Export handoff payload (§8.3: selection/filter state → export flow). */
export interface GridExportHandoff {
  /** Explorer surface the view belongs to (export preset scoping). */
  readonly explorerId: string;
  readonly sourceKey: string;
  readonly totalRows: number;
  readonly selectedRowKeys: readonly string[];
  readonly sortChain: readonly GridSortEntry[];
  readonly filterText: string;
  readonly ruleTree: QueryRuleNode | null;
  readonly serverQuery: ServerListQuery;
}

/** Row identity accessor — stable keys for selection and focus. */
export type GridRowKey<TRow> = (row: TRow, globalIndex: number) => string;
