/**
 * DataGrid — the virtualized grid surface (FS-CRAWL-002 §7.1 + §8.3).
 *
 * Renders the engine's window: ≤50 DOM rows keyed by global-index slot
 * (rows recycle as the window slides), ARIA grid semantics with true
 * aria-rowcount/aria-rowindex, server multi-column sort (shift-click adds),
 * column pinning (≤4) + resize + reorder, density modes, instant filter,
 * selection, keyboard navigation with focus retention, and the export
 * handoff. Wide dynamic-column datasets horizontally virtualize: only
 * visible columns + overscan mount; pinned columns stay mounted.
 *
 * @module grid/DataGrid
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { flexRender, type RowData } from '@tanstack/react-table';
import type { LegacyHeader } from '@tanstack/react-table/legacy';
import type { GridEngine } from './useGridEngine';
import { MAX_DOM_ROWS, type GridDensity } from './types';
import { computeColumnWindow, type ColumnGeometry } from './windowMath';
import type { GridColumnFormatter } from './contributions';
import './grid.css';

/** DataGrid props. */
export interface DataGridProps<TRow extends RowData> {
  readonly engine: GridEngine<TRow>;
  /** Accessible grid label. */
  readonly label: string;
  /** Toolbar extras (explorer-specific controls). */
  readonly toolbarExtras?: ReactNode;
  /** Row activation (drill-down) — Enter/click. */
  readonly onActivateRow?: (row: TRow, globalIndex: number) => void;
  /** Plugin formatter resolution (WP-06-06 registry). */
  readonly formatterFor?: (columnKey: string) => GridColumnFormatter | null;
  /** Query-builder panel toggle callback. */
  readonly onToggleQueryBuilder?: () => void;
  readonly queryBuilderOpen?: boolean;
  /** Export handoff consumer (shell export flow). */
  readonly onExport?: (handoff: ReturnType<GridEngine<TRow>['exportHandoff']>) => void;
  /** Height of the scroll viewport (CSS length). */
  readonly height?: number | string;
}

const DENSITY_LABELS: Record<GridDensity, string> = {
  compact: 'Compact',
  normal: 'Normal',
  comfortable: 'Comfortable'
};

/** Sort glyph + chain position for a header. */
function sortIndicator(chain: readonly { readonly field: string; readonly order: 'asc' | 'desc' }[], field: string): { glyph: string; position: number } | null {
  const index = chain.findIndex((entry) => entry.field === field);
  if (index < 0) return null;
  return { glyph: chain[index].order === 'asc' ? '▲' : '▼', position: chain.length > 1 ? index + 1 : 0 };
}

/** The grid component (generic over the explorer row type). */
export function DataGrid<TRow extends RowData>(props: DataGridProps<TRow>) {
  const { engine, label, toolbarExtras, onActivateRow, formatterFor, onToggleQueryBuilder, queryBuilderOpen, onExport, height = 480 } = props;
  const { table, virtualRows, totalRows, rowHeight, state } = engine;
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [scrollLeft, setScrollLeft] = useState(0);
  const [viewportWidth, setViewportWidth] = useState(1200);
  const resizingRef = useRef<{ columnId: string; startX: number; startWidth: number } | null>(null);

  const headers = table.getHeaderGroups()[0]?.headers ?? [];
  const pinnedLeftHeaders = headers.filter((header) => engine.state.pinned[header.column.id] === 'left');
  const pinnedRightHeaders = headers.filter((header) => engine.state.pinned[header.column.id] === 'right');
  const centerHeaders = headers.filter((header) => !engine.state.pinned[header.column.id]);

  // ── Horizontal virtualization geometry ──────────────────────────────────
  const geometries = useMemo<ColumnGeometry[]>(
    () => centerHeaders.map((header) => ({
      id: header.column.id,
      width: engine.state.columnWidths[header.column.id] ?? header.column.getSize(),
      pinned: null
    })),
    [centerHeaders, engine.state.columnWidths]
  );
  const totalCenterWidth = geometries.reduce((sum, geometry) => sum + geometry.width, 0);
  const horizontallyVirtualized = totalCenterWidth > viewportWidth;
  const columnWindow = useMemo(
    () => computeColumnWindow(geometries, scrollLeft, viewportWidth, 2),
    [geometries, scrollLeft, viewportWidth]
  );
  const mountedCenterIds = horizontallyVirtualized ? columnWindow.visibleIds : geometries.map((geometry) => geometry.id);
  const centerOffsets = useMemo(() => {
    const offsets = new Map<string, number>();
    let cursor = 0;
    for (const geometry of geometries) {
      offsets.set(geometry.id, cursor);
      cursor += geometry.width;
    }
    return offsets;
  }, [geometries]);

  const orderedColumnIds = useMemo(
    () => [
      ...pinnedLeftHeaders.map((header) => header.column.id),
      ...mountedCenterIds,
      ...pinnedRightHeaders.map((header) => header.column.id)
    ],
    [pinnedLeftHeaders, mountedCenterIds, pinnedRightHeaders]
  );

  // ── Scroll + viewport tracking ──────────────────────────────────────────
  const handleScroll = useCallback(() => {
    const element = engine.scrollRef.current;
    if (!element) return;
    setScrollLeft(element.scrollLeft);
    if (element.clientWidth > 0) setViewportWidth(element.clientWidth);
  }, [engine.scrollRef]);

  useEffect(() => {
    const element = engine.scrollRef.current;
    if (element && element.clientWidth > 0) setViewportWidth(element.clientWidth);
  }, [engine.scrollRef, totalRows]);

  // ── Focus retention: keep the focused cell focused across page loads ────
  useEffect(() => {
    const focus = engine.focusedCell;
    if (!focus || !containerRef.current) return;
    const cell = containerRef.current.querySelector<HTMLElement>(
      `[data-cf-row="${focus.rowIndex}"][data-cf-col="${focus.colIndex}"]`
    );
    cell?.focus({ preventScroll: true });
  }, [engine.focusedCell, engine.virtualRows, engine.loading]);

  // ── Column resize (pointer) ─────────────────────────────────────────────
  const startResize = useCallback((event: React.PointerEvent, columnId: string, currentWidth: number) => {
    event.preventDefault();
    event.stopPropagation();
    resizingRef.current = { columnId, startX: event.clientX, startWidth: currentWidth };
    (event.target as HTMLElement).setPointerCapture?.(event.pointerId);
  }, []);
  const moveResize = useCallback((event: React.PointerEvent) => {
    const resize = resizingRef.current;
    if (!resize) return;
    engine.setColumnWidth(resize.columnId, resize.startWidth + (event.clientX - resize.startX));
  }, [engine]);
  const endResize = useCallback(() => {
    resizingRef.current = null;
  }, []);

  // ── Cell rendering ──────────────────────────────────────────────────────
  const renderHeaderCell = (header: LegacyHeader<TRow>, pinned: 'left' | 'right' | null) => {
    const column = header.column;
    const field = (column.columnDef.meta as { serverField?: string } | undefined)?.serverField ?? column.id;
    const indicator = sortIndicator(engine.state.sortChain, field);
    const width = engine.state.columnWidths[column.id] ?? column.getSize();
    // Sort affordance audit (WP-06-03 reopen): a sort button is offered only
    // when the column is sortable AND its server field is accepted by the
    // route's whitelist. Columns whose backend route rejects/ignores the
    // sort set enableSorting: false in their explorer definition — the UI
    // must never offer an operation the server answers with 400.
    const sortable = column.columnDef.enableSorting !== false;
    return (
      <div
        key={column.id}
        role="columnheader"
        aria-colindex={orderedColumnIds.indexOf(column.id) + 1}
        aria-sort={sortable && indicator ? (indicator.glyph === '▲' ? 'ascending' : 'descending') : 'none'}
        className={`cf-grid-header-cell${pinned ? ` cf-grid-pinned-${pinned}` : ''}`}
        style={{ width, minWidth: width, ...(pinned ? {} : { position: 'absolute', left: centerOffsets.get(column.id) ?? 0 }) }}
        data-testid={`cf-grid-header-${column.id}`}
      >
        {sortable ? (
          <button
            type="button"
            className="cf-grid-sort-button"
            onClick={(event) => engine.toggleSort(field, event.shiftKey)}
            title={`Sort by ${field} (shift-click for multi-column)`}
            data-testid={`cf-grid-sort-${column.id}`}
          >
            <span className="cf-grid-header-label">
              {header.isPlaceholder ? null : flexRender(column.columnDef.header, header.getContext())}
            </span>
            {indicator ? (
              <span className="cf-grid-sort-indicator" data-testid={`cf-grid-sort-indicator-${column.id}`}>
                {indicator.glyph}
                {indicator.position > 0 ? <sub>{indicator.position}</sub> : null}
              </span>
            ) : null}
          </button>
        ) : (
          <span className="cf-grid-header-label cf-grid-header-label-static">
            {header.isPlaceholder ? null : flexRender(column.columnDef.header, header.getContext())}
          </span>
        )}
        <span className="cf-grid-header-actions">
          <button
            type="button"
            className="cf-grid-pin-button"
            aria-label={`Pin ${column.id} ${pinned === 'left' ? 'right' : 'left'}`}
            data-testid={`cf-grid-pin-${column.id}`}
            onClick={() => engine.togglePin(column.id, pinned === 'left' ? 'right' : 'left')}
          >
            {pinned ? '◈' : '◇'}
          </button>
        </span>
        <span
          role="separator"
          aria-orientation="vertical"
          className="cf-grid-resize-handle"
          data-testid={`cf-grid-resize-${column.id}`}
          onPointerDown={(event) => startResize(event, column.id, width)}
          onPointerMove={moveResize}
          onPointerUp={endResize}
        />
      </div>
    );
  };

  const renderBodyCell = (globalIndex: number, columnId: string, colIndex: number, row: TRow | undefined) => {
    const header = headers.find((candidate) => candidate.column.id === columnId);
    if (!header) return null;
    const column = header.column;
    const width = engine.state.columnWidths[columnId] ?? column.getSize();
    const pinned = engine.state.pinned[columnId] ?? null;
    const focused = engine.focusedCell?.rowIndex === globalIndex && engine.focusedCell?.colIndex === colIndex;
    const tableRow = engine.tableRowFor(globalIndex);
    const cell = tableRow?.getVisibleCells().find((candidate) => candidate.column.id === columnId);
    const meta = column.columnDef.meta as { numeric?: boolean; formatterKey?: string } | undefined;
    const formatter = formatterFor?.(meta?.formatterKey ?? columnId) ?? null;

    let content: ReactNode;
    if (row === undefined) {
      content = <span className="cf-grid-skeleton" aria-hidden="true" />;
    } else if (formatter) {
      const raw = (row as Record<string, unknown>)[columnId];
      content = <span className="cf-grid-cell-text">{formatter.format(raw, row)}</span>;
    } else if (cell) {
      content = flexRender(cell.column.columnDef.cell, cell.getContext());
    } else {
      content = <span className="cf-grid-skeleton" aria-hidden="true" />;
    }

    return (
      <div
        key={columnId}
        role="gridcell"
        aria-colindex={colIndex + 1}
        tabIndex={focused ? 0 : -1}
        data-cf-row={globalIndex}
        data-cf-col={colIndex}
        className={`cf-grid-cell${pinned ? ` cf-grid-pinned-${pinned}` : ''}${meta?.numeric ? ' cf-grid-numeric' : ''}${focused ? ' cf-grid-cell-focused' : ''}`}
        style={{ width, minWidth: width, ...(pinned ? {} : { position: 'absolute', left: centerOffsets.get(columnId) ?? 0 }) }}
        onClick={() => {
          engine.setFocusedCell({ rowIndex: globalIndex, colIndex });
          if (row !== undefined && onActivateRow) onActivateRow(row, globalIndex);
        }}
      >
        {content}
      </div>
    );
  };

  const rangeStart = virtualRows.length > 0 ? virtualRows[0].index + 1 : 0;
  const rangeEnd = virtualRows.length > 0 ? virtualRows[virtualRows.length - 1].index + 1 : 0;
  // Slot-recycling anchor (criterion #40, WP-06-03 reopen): rows are keyed by
  // VIEWPORT SLOT, not absolute index. Absolute keys remount every subtree on
  // each scroll step (the WP-07-03 bench measured alternating 16.7/33.4ms
  // frames at 43 rows/frame churn); stable slot keys let React reuse the ≤50
  // row DOM subtrees and update cell contents in place. Absolute identity is
  // preserved where it matters: data-row-index/aria-rowindex below, selection
  // by row key, and keyboard focus by absolute cell coordinates.
  const windowStart = virtualRows.length > 0 ? virtualRows[0].index : 0;
  const totalSize = engine.totalRows > 0 ? Math.max(engine.totalRows * rowHeight, rowHeight) : rowHeight;

  return (
    <div className="cf-grid" ref={containerRef} data-testid="cf-grid" data-density={state.density}>
      {/* Toolbar */}
      <div className="cf-grid-toolbar" role="toolbar" aria-label={`${label} controls`}>
        <input
          type="search"
          className="cf-grid-filter"
          placeholder={`Filter ${label.toLowerCase()}…`}
          aria-label={`Instant filter for ${label}`}
          value={state.filterText}
          onChange={(event) => engine.setFilterText(event.currentTarget.value)}
          data-testid="cf-grid-filter"
        />
        {toolbarExtras}
        <label className="cf-grid-density">
          <span className="cf-grid-density-label">Density</span>
          <select
            value={state.density}
            aria-label="Row density"
            onChange={(event) => engine.setDensity(event.currentTarget.value as GridDensity)}
            data-testid="cf-grid-density"
          >
            {(Object.keys(DENSITY_LABELS) as GridDensity[]).map((mode) => (
              <option key={mode} value={mode}>{DENSITY_LABELS[mode]}</option>
            ))}
          </select>
        </label>
        {onToggleQueryBuilder ? (
          <button
            type="button"
            className={`cf-grid-tool-button${queryBuilderOpen ? ' cf-grid-tool-active' : ''}`}
            onClick={onToggleQueryBuilder}
            aria-pressed={queryBuilderOpen}
            data-testid="cf-grid-query-toggle"
          >
            ⌗ Query builder
          </button>
        ) : null}
        <button type="button" className="cf-grid-tool-button" onClick={engine.refresh} data-testid="cf-grid-refresh">
          ⟳ Refresh
        </button>
        {state.selectedRowKeys.length > 0 ? (
          <span className="cf-grid-selection" data-testid="cf-grid-selection">
            {state.selectedRowKeys.length} selected
            <button type="button" className="cf-grid-link-button" onClick={engine.clearSelection}>clear</button>
          </span>
        ) : null}
        {onExport ? (
          <button
            type="button"
            className="cf-grid-tool-button"
            onClick={() => onExport(engine.exportHandoff())}
            data-testid="cf-grid-export"
          >
            ⇩ Export view
          </button>
        ) : null}
        <span className="cf-grid-total" data-testid="cf-grid-total">
          {engine.loading ? 'Loading…' : `${totalRows.toLocaleString()} rows`}
        </span>
      </div>

      {engine.error ? (
        <div className="cf-grid-error" role="alert" data-testid="cf-grid-error">{engine.error}</div>
      ) : null}

      {/* Grid body */}
      <div
        role="grid"
        aria-label={label}
        aria-rowcount={totalRows + 1}
        aria-colcount={orderedColumnIds.length + 1}
        className="cf-grid-body"
        style={{ height }}
        ref={engine.scrollRef}
        onScroll={handleScroll}
        onKeyDown={engine.handleKeyDown}
        data-testid="cf-grid-body"
      >
        {/* Header row */}
        <div role="row" aria-rowindex={1} className="cf-grid-header-row" data-testid="cf-grid-header-row">
          <div role="columnheader" aria-colindex={1} className="cf-grid-header-cell cf-grid-select-cell cf-grid-pinned-left">
            <span className="cf-grid-header-label">✓</span>
          </div>
          {pinnedLeftHeaders.map((header) => renderHeaderCell(header, 'left'))}
          <div className="cf-grid-header-track" style={{ width: totalCenterWidth, position: 'relative' }}>
            {centerHeaders
              .filter((header) => mountedCenterIds.includes(header.column.id))
              .map((header) => renderHeaderCell(header, null))}
          </div>
          {pinnedRightHeaders.map((header) => renderHeaderCell(header, 'right'))}
        </div>

        {/* Virtualized rows — hard cap MAX_DOM_ROWS (criterion #40) */}
        <div className="cf-grid-canvas" style={{ height: totalSize, position: 'relative' }}>
          {virtualRows.slice(0, MAX_DOM_ROWS).map((item) => {
            const row = engine.rowAt(item.index);
            const rowKeyValue = row !== undefined ? String((row as Record<string, unknown>).id ?? item.index) : `slot-${item.index}`;
            const selected = row !== undefined && engine.state.selectedRowKeys.includes(rowKeyValue);
            return (
              <div
                key={item.index - windowStart}
                role="row"
                aria-rowindex={item.index + 2}
                aria-selected={selected}
                className={`cf-grid-row${selected ? ' cf-grid-row-selected' : ''}${row === undefined ? ' cf-grid-row-loading' : ''}`}
                style={{ position: 'absolute', top: item.index * rowHeight, height: rowHeight, width: '100%' }}
                data-testid={`cf-grid-row-${item.index}`}
                data-row-index={item.index}
              >
                <div role="gridcell" aria-colindex={1} className="cf-grid-cell cf-grid-select-cell cf-grid-pinned-left">
                  <input
                    type="checkbox"
                    checked={selected}
                    disabled={row === undefined}
                    aria-label={`Select row ${item.index + 1}`}
                    onChange={() => engine.toggleRowSelected(rowKeyValue)}
                    data-testid={`cf-grid-select-${item.index}`}
                  />
                </div>
                {pinnedLeftHeaders.map((header) =>
                  renderBodyCell(item.index, header.column.id, orderedColumnIds.indexOf(header.column.id), row)
                )}
                <div className="cf-grid-row-track" style={{ width: totalCenterWidth, position: 'relative' }}>
                  {mountedCenterIds.map((columnId) =>
                    renderBodyCell(item.index, columnId, orderedColumnIds.indexOf(columnId), row)
                  )}
                </div>
                {pinnedRightHeaders.map((header) =>
                  renderBodyCell(item.index, header.column.id, orderedColumnIds.indexOf(header.column.id), row)
                )}
              </div>
            );
          })}
        </div>
      </div>

      {/* Footer */}
      <div className="cf-grid-footer" data-testid="cf-grid-footer">
        <span>
          {totalRows === 0 ? 'No rows' : `Rows ${rangeStart.toLocaleString()}–${rangeEnd.toLocaleString()} of ${totalRows.toLocaleString()}`}
        </span>
        <span className="cf-grid-footer-note">
          Server-windowed · {virtualRows.length}/{MAX_DOM_ROWS} DOM rows mounted
        </span>
      </div>
    </div>
  );
}
