/**
 * UrlExplorer — the URL inventory surface (WP-09-09, FS-CRAWL-002 §8.2,
 * UI-13; root testid `url-explorer` also satisfies UI-11).
 *
 * Server-windowed virtualized inventory over the locked `audits.listUrls`
 * route through the shared grid engine (≤50 DOM rows at any dataset size,
 * §7.1). Advanced filters (status class / CR-024 search-indexability /
 * depth / content type) travel as server query params — never client-side
 * filtering. Sorting uses the route whitelist (id, canonicalUrl, depth,
 * statusCode); pinning and custom column visibility compose the shared
 * DataGrid affordances.
 *
 * Row activation drills into UrlDetailPanel via `audits.getUrl` and fires
 * the onOpenUrl handoff callback. Mounting + report navigation ("URL
 * explorer") is the lead's wiring workpiece (WP-09-11).
 *
 * @module surfaces/explorers/urls/UrlExplorer
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useShell, useStoreSelector } from '../../../shell/ShellContext';
import { selectActiveAudit } from '../../../store/selectors';
import type { LoopbackApiClient } from '../../../contracts/api/client';
import type { UrlRecordSummary, HttpStatusClass } from '../../../contracts/api/audits';
import { createExplorerDataSource, createOfflineDataSource, arrayAdapter } from '../../../grid/dataSource';
import { useGridEngine } from '../../../grid/useGridEngine';
import { DataGrid } from '../../../grid/DataGrid';
import { urlExplorer } from '../coreExplorers';
import { UrlDetailPanel } from './UrlDetailPanel';
import type { UrlDetailWireView, UrlExplorerFilters, UrlOpenTarget } from './types';
import './urls.css';

/** Surface props (audit/api overrides serve tests and split views). */
export interface UrlExplorerProps {
  /** Override the active audit (null = no audit selected). */
  readonly auditId?: string | null;
  /** Override the loopback client (null = offline). */
  readonly api?: LoopbackApiClient | null;
  /** Drill-down handoff: fired on row activation and internal-link drills. */
  readonly onOpenUrl?: (target: UrlOpenTarget) => void;
  /** Initial advanced-filter state (deep links / saved views). */
  readonly initialFilters?: UrlExplorerFilters;
  /**
   * External drill-down target (report handoffs such as the rendered
   * comparison pane): opens the detail drawer for this URL id through the
   * same `openDetail` path row activation uses, once the client is available.
   * The handoff carries the id only, so the drawer label falls back to the id
   * until the detail wire loads its canonicalUrl.
   */
  readonly openUrlId?: string | null;
  /** Change token so a request for the SAME url id can re-open the drawer. */
  readonly openUrlKey?: number | null;
}

const STATUS_CLASSES: readonly HttpStatusClass[] = ['1xx', '2xx', '3xx', '4xx', '5xx'];

const CONTENT_TYPES: readonly string[] = [
  'text/html',
  'application/json',
  'text/css',
  'application/javascript',
  'image/png',
  'image/jpeg',
  'image/svg+xml',
  'text/plain',
  'application/xml',
  'application/pdf'
];

/** Columns shown by default; the rest are opt-in via the column picker. */
const DEFAULT_VISIBLE_COLUMNS: readonly string[] = [
  'canonicalUrl', 'statusCode', 'statusClass', 'contentType', 'depth', 'indexable', 'title'
];

/** True when any advanced filter is active. */
function hasActiveFilters(filters: UrlExplorerFilters): boolean {
  return (filters.statusClass ?? null) !== null
    || (filters.indexable ?? null) !== null
    || (filters.depth ?? null) !== null
    || (filters.contentType ?? null) !== null;
}

/** The URL explorer surface. */
export function UrlExplorer({ auditId: auditIdOverride, api: apiOverride, onOpenUrl, initialFilters, openUrlId, openUrlKey }: UrlExplorerProps) {
  const shell = useShell();
  const activeAudit = useStoreSelector(selectActiveAudit);
  const auditId = auditIdOverride !== undefined ? auditIdOverride : activeAudit?.id ?? null;
  const api: LoopbackApiClient | null = apiOverride !== undefined ? apiOverride : shell.services.api;

  const [filters, setFilters] = useState<UrlExplorerFilters>(initialFilters ?? {});
  const [visibleColumns, setVisibleColumns] = useState<readonly string[]>(DEFAULT_VISIBLE_COLUMNS);
  const [columnsPanelOpen, setColumnsPanelOpen] = useState(false);

  // ── Drill-down state ────────────────────────────────────────────────────
  const [openTarget, setOpenTarget] = useState<UrlOpenTarget | null>(null);
  const [detail, setDetail] = useState<UrlDetailWireView | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const detailRequestRef = useRef(0);

  const openDetail = useCallback((target: UrlOpenTarget) => {
    setOpenTarget(target);
    setDetail(null);
    setDetailError(null);
    onOpenUrl?.(target);
    if (!api) {
      setDetailError('Backend disconnected — URL detail is unavailable offline.');
      return;
    }
    const requestId = ++detailRequestRef.current;
    setDetailLoading(true);
    api.request('audits.getUrl', { params: { id: target.auditId, urlId: target.urlId } })
      .then((envelope) => {
        if (requestId !== detailRequestRef.current) return;
        if (envelope.error || !envelope.data) {
          setDetailError(envelope.error?.message ?? 'URL detail response was empty.');
          return;
        }
        setDetail(envelope.data);
      })
      .catch((error: unknown) => {
        if (requestId !== detailRequestRef.current) return;
        setDetailError(error instanceof Error ? error.message : 'Backend unreachable');
      })
      .finally(() => {
        if (requestId === detailRequestRef.current) setDetailLoading(false);
      });
  }, [api, onOpenUrl]);

  const closeDetail = useCallback(() => {
    detailRequestRef.current += 1;
    setOpenTarget(null);
    setDetail(null);
    setDetailError(null);
    setDetailLoading(false);
  }, []);

  const handleActivate = useCallback((row: UrlRecordSummary) => {
    if (!auditId) return;
    openDetail({ auditId, urlId: row.id, canonicalUrl: row.canonicalUrl });
  }, [auditId, openDetail]);

  // ── External drill-down (openUrlId handoff, WP-10-04 mounting) ─────────
  // Opens the requested URL's detail through the SAME openDetail mechanism as
  // row activation. The token guard opens each request exactly once (the
  // effect also re-fires when openDetail's identity changes), and openUrlKey
  // lets the same url id be requested again — e.g. re-activating the same
  // comparison row after the drawer was closed. The handoff carries only the
  // id, so the target's canonicalUrl is left empty (the drawer label falls
  // back to the id) and the panel shows the real URL from the detail wire.
  const handledOpenRef = useRef<string | null>(null);
  useEffect(() => {
    if (!openUrlId || !auditId || !api) return;
    const token = openUrlKey === null || openUrlKey === undefined ? openUrlId : `${openUrlId}#${openUrlKey}`;
    if (handledOpenRef.current === token) return;
    handledOpenRef.current = token;
    openDetail({ auditId, urlId: openUrlId, canonicalUrl: '' });
  }, [openUrlId, openUrlKey, auditId, api, openDetail]);

  // ── Server-windowed source (advanced filters ride the locked query) ────
  const baseQuery = useMemo<Record<string, unknown>>(() => {
    const query: Record<string, unknown> = {};
    if (filters.statusClass) query.statusClass = filters.statusClass;
    if (filters.indexable !== null && filters.indexable !== undefined) query.indexable = filters.indexable;
    if (filters.depth !== null && filters.depth !== undefined) query.depth = filters.depth;
    if (filters.contentType) query.contentType = filters.contentType;
    return query;
  }, [filters]);

  const source = useMemo(() => {
    if (!api || !auditId) return createOfflineDataSource<UrlRecordSummary>('audits.listUrls|offline');
    return createExplorerDataSource<UrlRecordSummary, 'audits.listUrls'>({
      api,
      route: 'audits.listUrls',
      params: { id: auditId },
      baseQuery,
      adapter: arrayAdapter<UrlRecordSummary, 'audits.listUrls'>()
    });
  }, [api, auditId, baseQuery]);

  // ── Columns: locked definition + custom visibility ─────────────────────
  const columnContext = useMemo(() => ({ dynamicColumns: [] as readonly string[], viewId: null as string | null }), []);
  const allColumns = useMemo(() => urlExplorer.columns(columnContext), [columnContext]);
  const queryFields = useMemo(() => urlExplorer.queryFields(columnContext), [columnContext]);
  const columns = useMemo(
    () => allColumns.filter((column) => visibleColumns.includes(String(column.id))),
    [allColumns, visibleColumns]
  );

  const engine = useGridEngine<UrlRecordSummary>({
    source,
    columns,
    rowKey: urlExplorer.rowKey,
    queryFields,
    explorerId: 'urls',
    initialSort: urlExplorer.defaultSort,
    onActivateRow: handleActivate
  });

  const toggleColumn = useCallback((columnId: string) => {
    setVisibleColumns((current) =>
      current.includes(columnId)
        ? current.filter((id) => id !== columnId)
        : [...allColumns.map((column) => String(column.id)).filter((id) => current.includes(id) || id === columnId)]
    );
  }, [allColumns]);

  const filtersActive = hasActiveFilters(filters);

  // ── Preconditions ───────────────────────────────────────────────────────
  if (!auditId) {
    return (
      <div className="cf-explorer cf-url-explorer" data-testid="url-explorer">
        <div className="cf-explorer-empty" data-testid="url-explorer-state">
          <span className="cf-explorer-empty-glyph" aria-hidden="true">🌐</span>
          <h3>URL explorer</h3>
          <p>Select an audit to explore its URL inventory.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="cf-explorer cf-url-explorer" data-testid="url-explorer">
      <header className="cf-explorer-header">
        <span className="cf-explorer-glyph" aria-hidden="true">🌐</span>
        <div className="cf-explorer-heading">
          <h2 className="cf-explorer-title">URL explorer</h2>
          <p className="cf-explorer-tagline">
            Every crawled URL — status, search-indexability (CR-024), depth, and block verdicts.
          </p>
        </div>
        <button
          type="button"
          className="cf-grid-tool-button cf-url-columns-toggle"
          aria-expanded={columnsPanelOpen}
          onClick={() => setColumnsPanelOpen((open) => !open)}
          data-testid="url-explorer-columns"
        >
          Columns
        </button>
      </header>

      {!api ? (
        <div className="cf-explorer-offline" role="status" data-testid="url-explorer-offline">
          Backend disconnected — URL inventory is unavailable offline. Reconnect to load server windows.
        </div>
      ) : null}

      {/* Advanced filters — every control maps to a locked server query param. */}
      <div className="cf-url-filters" role="group" aria-label="URL filters" data-testid="url-explorer-filters">
        <label className="cf-url-filter">
          <span className="cf-url-filter-label">Status</span>
          <select
            value={filters.statusClass ?? ''}
            onChange={(event) => setFilters((current) => ({
              ...current,
              statusClass: event.target.value === '' ? null : event.target.value as HttpStatusClass
            }))}
            data-testid="url-explorer-filter-status"
          >
            <option value="">All statuses</option>
            {STATUS_CLASSES.map((statusClass) => <option key={statusClass} value={statusClass}>{statusClass}</option>)}
          </select>
        </label>
        <label className="cf-url-filter">
          <span className="cf-url-filter-label">Indexability</span>
          <select
            value={filters.indexable === null || filters.indexable === undefined ? '' : String(filters.indexable)}
            onChange={(event) => setFilters((current) => ({
              ...current,
              indexable: event.target.value === '' ? null : event.target.value === 'true'
            }))}
            data-testid="url-explorer-filter-indexable"
            title="CR-024: search-indexable — noindex pages are fetchable but not indexable"
          >
            <option value="">All</option>
            <option value="true">Search-indexable</option>
            <option value="false">Not search-indexable</option>
          </select>
        </label>
        <label className="cf-url-filter">
          <span className="cf-url-filter-label">Depth</span>
          <input
            type="number"
            min={0}
            value={filters.depth ?? ''}
            onChange={(event) => setFilters((current) => ({
              ...current,
              depth: event.target.value === '' ? null : Math.max(0, Number.parseInt(event.target.value, 10) || 0)
            }))}
            data-testid="url-explorer-filter-depth"
          />
        </label>
        <label className="cf-url-filter">
          <span className="cf-url-filter-label">Content type</span>
          <select
            value={filters.contentType ?? ''}
            onChange={(event) => setFilters((current) => ({
              ...current,
              contentType: event.target.value === '' ? null : event.target.value
            }))}
            data-testid="url-explorer-filter-content-type"
          >
            <option value="">All types</option>
            {CONTENT_TYPES.map((contentType) => <option key={contentType} value={contentType}>{contentType}</option>)}
          </select>
        </label>
        <button
          type="button"
          className="cf-grid-tool-button"
          disabled={!filtersActive}
          onClick={() => setFilters({})}
          data-testid="url-explorer-filters-clear"
        >
          Clear filters
        </button>
      </div>

      {columnsPanelOpen ? (
        <div className="cf-url-columns-panel" role="group" aria-label="Custom columns" data-testid="url-explorer-columns-panel">
          {allColumns.map((column) => {
            const columnId = String(column.id);
            const checked = visibleColumns.includes(columnId);
            return (
              <label key={columnId} className="cf-url-column-option">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleColumn(columnId)}
                  data-testid={`url-explorer-column-toggle-${columnId}`}
                />
                <span>{typeof column.header === 'string' ? column.header : columnId}</span>
              </label>
            );
          })}
        </div>
      ) : null}

      <DataGrid
        engine={engine}
        label="URL explorer"
        onActivateRow={handleActivate}
        height={460}
      />

      {engine.totalRows === 0 && !engine.loading && !engine.error ? (
        <p className="cf-explorer-hint" data-testid="url-explorer-empty">
          {filtersActive
            ? 'No URLs match the current filters. Adjust or clear the filters to widen the inventory.'
            : 'This audit has no URL records yet. Start or resume the crawl to populate the inventory.'}
        </p>
      ) : null}

      {/* Drill-down drawer */}
      {openTarget ? (
        <aside className="cf-url-detail-drawer" role="dialog" aria-label={`URL detail: ${openTarget.canonicalUrl || openTarget.urlId}`} data-testid="url-explorer-detail">
          {detailLoading ? (
            <p className="cf-url-detail-loading" role="status" data-testid="url-detail-loading">Loading URL detail…</p>
          ) : null}
          {detailError ? (
            <div className="cf-url-detail-error" role="alert" data-testid="url-detail-error">
              <p>{detailError}</p>
              <button type="button" className="cf-grid-tool-button" onClick={() => openDetail(openTarget)} data-testid="url-detail-retry">
                Retry
              </button>
            </div>
          ) : null}
          {detail ? (
            <UrlDetailPanel detail={detail} auditId={openTarget.auditId} onOpenUrl={openDetail} onClose={closeDetail} />
          ) : null}
          {!detailLoading && !detailError && !detail ? (
            <button type="button" className="cf-url-detail-close" onClick={closeDetail} aria-label="Close URL detail" data-testid="url-detail-close">✕</button>
          ) : null}
        </aside>
      ) : null}
    </div>
  );
}
