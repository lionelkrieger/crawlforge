/**
 * The grid engine hook (FS-CRAWL-002 §7.1 + §8.3, criterion #40).
 *
 * Combines the headless pair — TanStack Table (column model, multi-sort,
 * pinning, selection) + TanStack Virtual (row windowing) — over the bounded
 * server page cache. Guarantees:
 *
 *  - DOM rows ≤ MAX_DOM_ROWS: overscan is clamped to the cap AND the mounted
 *    virtual-item list is hard-sliced to the cap (double invariant);
 *  - rows recycle by global-index slot keys as the window slides;
 *  - sort/filter/query-tree changes re-sign the cache and refetch windows
 *    server-side; instant filter is debounced (FILTER_DEBOUNCE_MS);
 *  - keyboard grid navigation with focus retention across page loads;
 *  - ARIA row semantics (aria-rowcount/aria-rowindex) live in DataGrid;
 *  - export handoff carries selection + filter + locked rule tree.
 *
 * @module grid/useGridEngine
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  useLegacyTable,
  type LegacyReactTable
} from '@tanstack/react-table/legacy';
import type {
  ColumnPinningState,
  ColumnVisibilityState,
  RowData,
  RowSelectionState,
  SortingState
} from '@tanstack/react-table';
import { useVirtualizer, type VirtualItem } from '@tanstack/react-virtual';
import type { QueryRuleNode } from '../contracts/api/queries';
import { GridPageCache, querySignature } from './pageCache';
import {
  DEFAULT_OVERSCAN,
  DENSITY_ROW_HEIGHT,
  FILTER_DEBOUNCE_MS,
  GRID_PAGE_SIZE,
  MAX_DOM_ROWS,
  type GridColumnDef,
  type GridDensity,
  type GridExportHandoff,
  type GridRowKey,
  type GridSortEntry,
  type GridState,
  type GridDataSource,
  type ServerListQuery
} from './types';
import {
  clampOverscan,
  clampPinning,
  pagesForRange
} from './windowMath';
import { foldTreeIntoFilters, type QueryFieldDef } from './queryBuilder';

/** Options binding the engine to one explorer. */
export interface UseGridEngineOptions<TRow extends RowData> {
  readonly source: GridDataSource<TRow>;
  readonly columns: readonly GridColumnDef<TRow>[];
  readonly rowKey: GridRowKey<TRow>;
  readonly queryFields: readonly QueryFieldDef[];
  readonly explorerId: string;
  readonly initialSort?: readonly GridSortEntry[];
  readonly initialDensity?: GridDensity;
  readonly pageSize?: number;
  readonly filterDebounceMs?: number;
  /** Row activation (drill-down) callback. */
  readonly onActivateRow?: (row: TRow, globalIndex: number) => void;
}

/** A focused grid cell in global coordinates. */
export interface GridFocus {
  readonly rowIndex: number;
  readonly colIndex: number;
}

/** The engine surface consumed by DataGrid and explorer surfaces. */
export interface GridEngine<TRow extends RowData> {
  readonly table: LegacyReactTable<TRow>;
  readonly virtualRows: readonly VirtualItem[];
  readonly totalRows: number;
  readonly rowHeight: number;
  readonly density: GridDensity;
  readonly state: GridState;
  readonly loading: boolean;
  readonly error: string | null;
  readonly dynamicColumns: readonly string[];
  /** Response-level metadata from the latest fetched page. */
  readonly pageMeta: Readonly<Record<string, unknown>> | null;
  readonly focusedCell: GridFocus | null;
  readonly scrollRef: React.RefObject<HTMLDivElement | null>;
  rowAt(globalIndex: number): TRow | undefined;
  tableRowFor(globalIndex: number): ReturnType<LegacyReactTable<TRow>['getRowModel']>['rows'][number] | undefined;
  setDensity(density: GridDensity): void;
  toggleSort(field: string, additive: boolean): void;
  setFilterText(text: string): void;
  applyRuleTree(tree: QueryRuleNode | null): void;
  togglePin(columnId: string, side: 'left' | 'right'): void;
  setColumnWidth(columnId: string, width: number): void;
  moveColumn(columnId: string, targetIndex: number): void;
  toggleRowSelected(key: string): void;
  clearSelection(): void;
  setFocusedCell(focus: GridFocus | null): void;
  handleKeyDown(event: React.KeyboardEvent): void;
  scrollToRow(globalIndex: number): void;
  exportHandoff(): GridExportHandoff;
  refresh(): void;
}

/** Builds the server query from grid state (pure; unit-tested). */
export function buildServerQuery(
  sortChain: readonly GridSortEntry[],
  debouncedFilter: string,
  ruleTree: QueryRuleNode | null,
  queryFields: readonly QueryFieldDef[]
): ServerListQuery {
  const { filters } = foldTreeIntoFilters(ruleTree, queryFields);
  const query: Record<string, unknown> = { ...filters };
  if (debouncedFilter.trim().length > 0) query.search = debouncedFilter.trim();
  if (sortChain.length === 1) {
    query.sort = sortChain[0].field;
    query.order = sortChain[0].order;
  } else if (sortChain.length > 1) {
    // Repeated sort/order fields — the sanctioned multi-column wire form.
    query.sort = sortChain.map((entry) => entry.field);
    query.order = sortChain.map((entry) => entry.order);
  }
  return query as ServerListQuery;
}

/** Hook: one server-windowed virtualized grid engine instance. */
export function useGridEngine<TRow extends RowData>(options: UseGridEngineOptions<TRow>): GridEngine<TRow> {
  const {
    source,
    columns,
    rowKey,
    queryFields,
    explorerId,
    initialSort,
    initialDensity = 'normal',
    pageSize = GRID_PAGE_SIZE,
    filterDebounceMs = FILTER_DEBOUNCE_MS,
    onActivateRow
  } = options;

  // ── Grid state ──────────────────────────────────────────────────────────
  const [sortChain, setSortChain] = useState<readonly GridSortEntry[]>(initialSort ?? []);
  const [filterText, setFilterTextState] = useState('');
  const [debouncedFilter, setDebouncedFilter] = useState('');
  const [ruleTree, setRuleTree] = useState<QueryRuleNode | null>(null);
  const [density, setDensity] = useState<GridDensity>(initialDensity);
  const [pinned, setPinned] = useState<Readonly<Record<string, 'left' | 'right'>>>({});
  const [selectedRowKeys, setSelectedRowKeys] = useState<readonly string[]>([]);
  const [columnOrder, setColumnOrder] = useState<readonly string[]>([]);
  const [columnWidths, setColumnWidths] = useState<Readonly<Record<string, number>>>({});
  const [focusedCell, setFocusedCell] = useState<GridFocus | null>(null);
  const [totalRows, setTotalRows] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dynamicColumns, setDynamicColumns] = useState<readonly string[]>([]);
  const [pageMeta, setPageMeta] = useState<Readonly<Record<string, unknown>> | null>(null);
  const [renderTick, setRenderTick] = useState(0);
  const [refreshCounter, setRefreshCounter] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(600);

  const rowHeight = DENSITY_ROW_HEIGHT[density];
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const cacheRef = useRef<GridPageCache<TRow> | null>(null);
  const cacheSourceRef = useRef<GridDataSource<TRow> | null>(null);
  if (!cacheRef.current || cacheSourceRef.current !== source) {
    cacheRef.current = new GridPageCache<TRow>(source, pageSize);
    cacheSourceRef.current = source;
  }
  const cache = cacheRef.current;

  // ── Debounced instant filter (§7.1: 150–300 ms) ─────────────────────────
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedFilter(filterText), filterDebounceMs);
    return () => clearTimeout(timer);
  }, [filterText, filterDebounceMs]);

  // ── Server query + cache signature ──────────────────────────────────────
  const serverQuery = useMemo(
    () => buildServerQuery(sortChain, debouncedFilter, ruleTree, queryFields),
    [sortChain, debouncedFilter, ruleTree, queryFields]
  );
  const signature = useMemo(() => querySignature(source.sourceKey, serverQuery), [source.sourceKey, serverQuery]);

  useEffect(() => {
    cache.setSignature(serverQuery);
    setTotalRows(cache.totalRows());
    setRenderTick((tick) => tick + 1);
  }, [signature, cache, serverQuery]);

  // ── Virtualizer over the GLOBAL row space ───────────────────────────────
  const overscan = clampOverscan(viewportHeight, rowHeight, DEFAULT_OVERSCAN);
  const virtualizer = useVirtualizer({
    count: totalRows,
    getScrollElement: () => scrollRef.current,
    estimateSize: useCallback(() => rowHeight, [rowHeight]),
    overscan,
    initialRect: { width: 1200, height: 600 },
    getItemKey: useCallback((index: number) => index, [])
  });

  // Viewport measurement (ResizeObserver where available; fallback keeps
  // jsdom and hidden-pane mounts functional).
  useEffect(() => {
    const element = scrollRef.current;
    if (!element) return;
    const update = () => {
      const height = element.clientHeight;
      if (height > 0) setViewportHeight(height);
    };
    update();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [renderTick]);

  const rawVirtualRows = virtualizer.getVirtualItems();
  // Hard row-cap invariant: even if the virtualizer ever yields more, the
  // mounted list is sliced to MAX_DOM_ROWS (criterion #40 double guard).
  const virtualRows = useMemo(
    () => (rawVirtualRows.length > MAX_DOM_ROWS ? rawVirtualRows.slice(0, MAX_DOM_ROWS) : rawVirtualRows),
    [rawVirtualRows]
  );

  const startIndex = virtualRows.length > 0 ? virtualRows[0].index : 0;
  const endIndex = virtualRows.length > 0 ? virtualRows[virtualRows.length - 1].index + 1 : 0;

  // ── Server window fetches (missing pages only, deduped) ─────────────────
  useEffect(() => {
    if (totalRows === 0 && refreshCounter === 0 && signature === '') return;
    const needed = pagesForRange(startIndex, Math.max(endIndex, startIndex + 1), pageSize);
    const missing = needed.filter((page) => !cache.hasPage(page));
    if (missing.length === 0) return;
    let cancelled = false;
    setLoading(true);
    Promise.all(missing.map((page) => cache.ensurePage(page, serverQuery))).then((results) => {
      if (cancelled) return;
      setLoading(false);
      let sawError: string | null = null;
      let latestColumns: readonly string[] | null = null;
      for (const result of results) {
        if (result.ok) {
          setTotalRows((current) => Math.max(current, result.page.total));
          if (result.page.columns) latestColumns = result.page.columns;
          if (result.page.meta) setPageMeta(result.page.meta);
        } else if (result.errorCode !== 'offline') {
          sawError = result.errorMessage;
        } else {
          sawError = result.errorMessage;
        }
      }
      setError(sawError);
      if (latestColumns) setDynamicColumns(latestColumns);
      setRenderTick((tick) => tick + 1);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [startIndex, endIndex, signature, refreshCounter, pageSize, totalRows === 0]);

  // Initial window: fetch page 0 once the source/signature is known.
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    cache.ensurePage(0, serverQuery).then((result) => {
      if (cancelled) return;
      setLoading(false);
      if (result.ok) {
        setTotalRows(result.page.total);
        if (result.page.columns) setDynamicColumns(result.page.columns);
        if (result.page.meta) setPageMeta(result.page.meta);
        setError(null);
      } else {
        setError(result.errorMessage);
      }
      setRenderTick((tick) => tick + 1);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signature, refreshCounter]);

  // Cleanup on unmount: drop resident pages (§7.4 no cross-surface retention).
  useEffect(() => () => cache.clear(), [cache]);

  // ── Mounted row data (≤50 rows) → TanStack table ────────────────────────
  const mountedEntries = useMemo(() => {
    const entries: { readonly index: number; readonly row: TRow }[] = [];
    for (const item of virtualRows) {
      const row = cache.getRow(item.index);
      if (row !== undefined) entries.push({ index: item.index, row });
    }
    return entries;
    // renderTick drives cache reads after fetches land.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [virtualRows, renderTick, cache]);

  const indexByRow = useMemo(() => {
    const map = new Map<TRow, number>();
    for (const entry of mountedEntries) map.set(entry.row, entry.index);
    return map;
  }, [mountedEntries]);

  const data = useMemo(() => mountedEntries.map((entry) => entry.row), [mountedEntries]);

  const sortingState = useMemo<SortingState>(
    () => sortChain.map((entry) => ({ id: entry.field, desc: entry.order === 'desc' })),
    [sortChain]
  );
  const pinningState = useMemo<ColumnPinningState>(() => {
    // v9 names the pin tracks start/end; the grid UI keeps left/right.
    const start: string[] = [];
    const end: string[] = [];
    for (const [id, side] of Object.entries(pinned)) (side === 'left' ? start : end).push(id);
    return { start, end };
  }, [pinned]);
  const selectionState = useMemo<RowSelectionState>(
    () => Object.fromEntries(selectedRowKeys.map((key) => [key, true])),
    [selectedRowKeys]
  );

  const table = useLegacyTable<TRow>({
    data,
    columns: columns as GridColumnDef<TRow>[],
    getRowId: (row) => {
      const index = indexByRow.get(row);
      return index === undefined ? String(rowKey(row, -1)) : rowKey(row, index);
    },
    state: {
      sorting: sortingState,
      columnPinning: pinningState,
      rowSelection: selectionState,
      columnOrder: columnOrder as string[],
      columnSizing: columnWidths as Record<string, number>,
      columnVisibility: {} as ColumnVisibilityState
    },
    manualSorting: true,
    manualFiltering: true,
    manualPagination: true,
    rowCount: totalRows,
    enableColumnResizing: true,
    columnResizeMode: 'onChange',
    onSortingChange: () => undefined,
    onColumnPinningChange: () => undefined,
    onRowSelectionChange: () => undefined,
    onColumnOrderChange: () => undefined,
    onColumnSizingChange: () => undefined
  });

  const tableRowByIndex = useMemo(() => {
    const rows = table.getRowModel().rows;
    const map = new Map<number, (typeof rows)[number]>();
    for (const row of rows) {
      const index = indexByRow.get(row.original);
      if (index !== undefined) map.set(index, row);
    }
    return map;
  }, [table, indexByRow, renderTick]);

  // ── Handlers ────────────────────────────────────────────────────────────
  const toggleSort = useCallback((field: string, additive: boolean) => {
    setSortChain((chain) => {
      const existing = chain.find((entry) => entry.field === field);
      const nextOrder: 'asc' | 'desc' = existing ? (existing.order === 'asc' ? 'desc' : 'asc') : 'asc';
      if (!additive) {
        return existing && existing.order === 'desc' ? [] : [{ field, order: nextOrder }];
      }
      if (!existing) return [...chain, { field, order: 'asc' }];
      if (existing.order === 'desc') return chain.filter((entry) => entry.field !== field);
      return chain.map((entry) => (entry.field === field ? { field, order: 'desc' as const } : entry));
    });
  }, []);

  const togglePin = useCallback((columnId: string, side: 'left' | 'right') => {
    setPinned((current) => {
      const next = { ...current };
      if (next[columnId]) delete next[columnId];
      else next[columnId] = side;
      return clampPinning(next);
    });
  }, []);

  const setColumnWidth = useCallback((columnId: string, width: number) => {
    setColumnWidths((current) => ({ ...current, [columnId]: Math.max(48, Math.round(width)) }));
  }, []);

  const moveColumn = useCallback((columnId: string, targetIndex: number) => {
    setColumnOrder((current) => {
      const base = current.length > 0 ? [...current] : columns.map((column) => String(column.id ?? ''));
      const from = base.indexOf(columnId);
      if (from < 0) return current;
      base.splice(from, 1);
      base.splice(Math.max(0, Math.min(targetIndex, base.length)), 0, columnId);
      return base;
    });
  }, [columns]);

  const toggleRowSelected = useCallback((key: string) => {
    setSelectedRowKeys((current) =>
      current.includes(key) ? current.filter((candidate) => candidate !== key) : [...current, key]
    );
  }, []);

  const clearSelection = useCallback(() => setSelectedRowKeys([]), []);

  const scrollToRow = useCallback((globalIndex: number) => {
    virtualizer.scrollToIndex(Math.max(0, Math.min(globalIndex, Math.max(0, totalRows - 1))), { align: 'auto' });
  }, [virtualizer, totalRows]);

  const visibleColumnIds = useMemo(() => table.getVisibleLeafColumns().map((column) => column.id), [table, renderTick]);

  const handleKeyDown = useCallback((event: React.KeyboardEvent) => {
    const focus = focusedCell ?? { rowIndex: startIndex, colIndex: 0 };
    const colCount = Math.max(1, visibleColumnIds.length);
    const viewportRows = Math.max(1, Math.floor(viewportHeight / rowHeight));
    let next: GridFocus | null = null;
    switch (event.key) {
      case 'ArrowDown': next = { rowIndex: Math.min(totalRows - 1, focus.rowIndex + 1), colIndex: focus.colIndex }; break;
      case 'ArrowUp': next = { rowIndex: Math.max(0, focus.rowIndex - 1), colIndex: focus.colIndex }; break;
      case 'ArrowRight': next = { rowIndex: focus.rowIndex, colIndex: Math.min(colCount - 1, focus.colIndex + 1) }; break;
      case 'ArrowLeft': next = { rowIndex: focus.rowIndex, colIndex: Math.max(0, focus.colIndex - 1) }; break;
      case 'PageDown': next = { rowIndex: Math.min(totalRows - 1, focus.rowIndex + viewportRows), colIndex: focus.colIndex }; break;
      case 'PageUp': next = { rowIndex: Math.max(0, focus.rowIndex - viewportRows), colIndex: focus.colIndex }; break;
      case 'Home': next = event.ctrlKey ? { rowIndex: 0, colIndex: 0 } : { rowIndex: focus.rowIndex, colIndex: 0 }; break;
      case 'End': next = event.ctrlKey ? { rowIndex: Math.max(0, totalRows - 1), colIndex: colCount - 1 } : { rowIndex: focus.rowIndex, colIndex: colCount - 1 }; break;
      case 'Enter':
      case ' ': {
        const row = cache.getRow(focus.rowIndex);
        if (row !== undefined && onActivateRow) {
          event.preventDefault();
          onActivateRow(row, focus.rowIndex);
        }
        return;
      }
      default: return;
    }
    if (next) {
      event.preventDefault();
      setFocusedCell(next);
      scrollToRow(next.rowIndex);
    }
  }, [focusedCell, startIndex, visibleColumnIds, viewportHeight, rowHeight, totalRows, cache, onActivateRow, scrollToRow]);

  const exportHandoff = useCallback((): GridExportHandoff => ({
    explorerId,
    sourceKey: source.sourceKey,
    totalRows,
    selectedRowKeys,
    sortChain,
    filterText: debouncedFilter,
    ruleTree,
    serverQuery
  }), [explorerId, source.sourceKey, totalRows, selectedRowKeys, sortChain, debouncedFilter, ruleTree, serverQuery]);

  const refresh = useCallback(() => {
    cache.clear();
    setTotalRows(0);
    setRefreshCounter((counter) => counter + 1);
  }, [cache]);

  const state = useMemo<GridState>(() => ({
    sortChain,
    filterText,
    ruleTree,
    density,
    pinned,
    selectedRowKeys,
    columnOrder,
    columnWidths
  }), [sortChain, filterText, ruleTree, density, pinned, selectedRowKeys, columnOrder, columnWidths]);

  return {
    table,
    virtualRows,
    totalRows,
    rowHeight,
    density,
    state,
    loading,
    error,
    dynamicColumns,
    pageMeta,
    focusedCell,
    scrollRef,
    rowAt: (globalIndex) => cache.getRow(globalIndex),
    tableRowFor: (globalIndex) => tableRowByIndex.get(globalIndex),
    setDensity,
    toggleSort,
    setFilterText: setFilterTextState,
    applyRuleTree: setRuleTree,
    togglePin,
    setColumnWidth,
    moveColumn,
    toggleRowSelected,
    clearSelection,
    setFocusedCell,
    handleKeyDown,
    scrollToRow,
    exportHandoff,
    refresh
  };
}
