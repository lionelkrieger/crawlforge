/**
 * Rendered comparison specialist surface (WP-10-03, UI-16; FS-CRAWL-001 §8.2
 * "Rendered comparison" / §7.18, CAP-A-035 / CAP-A-X09).
 *
 * Lists EVERY page with a response-vs-rendered difference, severity-sorted
 * (critical rendered-only/raw-only noindex first, then title/canonical/
 * meta-robots/hreflang errors, then metadata/status warnings, then text/links/
 * structured-data/resources notices). Data is queried through the locked
 * issue-explorer query surface (`audits.listFindings` ruleKey filters) and the
 * locked URL list route, aggregated by `loadRenderedComparison`.
 *
 * Reuses the accepted grid engine (virtualized, ≤ MAX_DOM_ROWS DOM rows) over
 * an in-memory source of the pre-sorted aggregated rows. Row activation drills
 * into the URL detail rendered tab via `onOpenUrl` (coordinator wiring).
 *
 * @module surfaces/explorers/rendered/RenderedComparisonSurface
 */

import { useEffect, useMemo, useRef, useState } from 'react';
import type { LoopbackApiClient } from '../../../contracts/api/client';
import { GRID_PAGE_SIZE, type GridDataSource, type GridFetchResult } from '../../../grid/types';
import type { QueryFieldDef } from '../../../grid/queryBuilder';
import { useGridEngine } from '../../../grid/useGridEngine';
import { DataGrid } from '../../../grid/DataGrid';
import { makeColumn } from '../columnUtils';
import { Badge, NumberCell, UrlCell, severityTone } from '../cells';
import { loadRenderedComparison, type RenderedComparisonRow } from './renderedComparison';
import { CAPTURE_SOURCE_LABELS } from './renderedKeys';
import '../explorers.css';

/** Props for the rendered comparison surface. */
export interface RenderedComparisonSurfaceProps {
  readonly api: LoopbackApiClient | null;
  readonly auditId: string | null;
  /** Drill-down handoff: the shell opens the URL detail rendered tab. */
  readonly onOpenUrl?: (urlId: string) => void;
  readonly height?: number;
}

/**
 * Stable empty query-field list. MUST be a constant reference: the grid engine
 * re-signs its cache whenever `queryFields` changes identity, so an inline `[]`
 * would re-render the engine every frame (infinite render loop).
 */
const NO_QUERY_FIELDS: readonly QueryFieldDef[] = [];

/** Severity badge cell (critical noindex highlighted). */
function SeverityCell({ row }: { row: RenderedComparisonRow }) {
  return (
    <span className="cf-cell-badges">
      <Badge tone={severityTone(row.severity)}>{row.severity}</Badge>
      {row.critical ? <Badge tone="danger">critical</Badge> : null}
    </span>
  );
}

/** Columns over the aggregated comparison row. */
function comparisonColumns() {
  return [
    makeColumn<RenderedComparisonRow, string>('severity', 'Severity', (row) => row.severity, {
      size: 130,
      cell: (_value, row) => <SeverityCell row={row} />
    }),
    makeColumn<RenderedComparisonRow, string>('canonicalUrl', 'URL', (row) => row.canonicalUrl, {
      size: 320,
      cell: (value, row) => (
        <span data-testid={`rendered-comparison-url-${row.urlId}`} data-critical={row.critical ? 'true' : 'false'}>
          <UrlCell url={value} />
        </span>
      )
    }),
    makeColumn<RenderedComparisonRow, string>('captureSource', 'Capture', (row) => row.captureSource ?? 'raw', {
      size: 130,
      cell: (value) => <Badge tone="neutral">{CAPTURE_SOURCE_LABELS[value] ?? value}</Badge>
    }),
    makeColumn<RenderedComparisonRow, string>('changedFields', 'Changed fields', (row) => row.changedFields.join(', '), {
      size: 280,
      cell: (value) => <span className="cf-cell-text">{value}</span>
    }),
    makeColumn<RenderedComparisonRow, number>('findingCount', 'Findings', (row) => row.findingCount, {
      size: 90,
      meta: { numeric: true },
      cell: (value) => <NumberCell value={value} />
    })
  ];
}

/** The UI-16 rendered comparison surface. */
export function RenderedComparisonSurface({ api, auditId, onOpenUrl, height = 480 }: RenderedComparisonSurfaceProps) {
  const [rows, setRows] = useState<readonly RenderedComparisonRow[]>([]);
  const [findingCount, setFindingCount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    (async () => {
      const result = await loadRenderedComparison(api, auditId, controller.signal);
      if (controller.signal.aborted) return;
      setRows(result.rows);
      setFindingCount(result.findingCount);
      setError(result.error);
      setLoading(false);
    })();
    return () => controller.abort();
  }, [api, auditId]);

  const columns = useMemo(() => comparisonColumns(), []);

  // Stable ref-backed source. The grid page cache is constructed ONCE against
  // this object; recreating the source when rows load would leave the cache
  // bound to the stale (empty) source and spin the window-fetch effect. Reading
  // rowsRef at fetch time keeps every window current without rebuilding it.
  const rowsRef = useRef<readonly RenderedComparisonRow[]>([]);
  rowsRef.current = rows;
  const source = useMemo<GridDataSource<RenderedComparisonRow>>(
    () => ({
      sourceKey: 'rendered-comparison',
      async fetchPage(query): Promise<GridFetchResult<RenderedComparisonRow>> {
        const all = rowsRef.current;
        const offset = query.offset ?? 0;
        const limit = query.limit ?? GRID_PAGE_SIZE;
        return {
          ok: true,
          page: { offset, limit, total: all.length, rows: all.slice(offset, offset + limit), fetchedAt: Date.now() }
        };
      }
    }),
    []
  );
  const engine = useGridEngine<RenderedComparisonRow>({
    source,
    columns,
    rowKey: (row) => row.urlId,
    queryFields: NO_QUERY_FIELDS,
    explorerId: 'rendered-comparison',
    initialSort: [{ field: 'severity', order: 'asc' }],
    onActivateRow: (row) => onOpenUrl?.(row.urlId)
  });

  // Rows arrive asynchronously after mount; invalidate the cache so the loaded
  // comparison rows are fetched through the stable source (reads rowsRef).
  const refresh = engine.refresh;
  useEffect(() => {
    refresh();
  }, [rows, refresh]);

  const criticalCount = useMemo(() => rows.filter((row) => row.critical).length, [rows]);

  if (!api) {
    return (
      <div className="cf-explorer-empty" data-testid="rendered-comparison-offline">
        Backend disconnected — rendered comparison is unavailable offline.
      </div>
    );
  }

  return (
    <div className="cf-explorer" data-testid="rendered-comparison">
      <div className="cf-explorer-filters">
        <span className="cf-explorer-count" data-testid="rendered-comparison-count">
          {rows.length} page{rows.length === 1 ? '' : 's'} with response-vs-rendered differences
        </span>
        <span className="cf-explorer-count" data-testid="rendered-comparison-finding-count">
          {findingCount} render finding{findingCount === 1 ? '' : 's'}
        </span>
        {criticalCount > 0 ? <Badge tone="danger">{criticalCount} critical</Badge> : null}
        <span className="cf-explorer-count">sorted by severity</span>
        {loading ? (
          <span className="cf-explorer-count" data-testid="rendered-comparison-loading">
            Loading…
          </span>
        ) : null}
        {error ? (
          <span className="cf-explorer-error" data-testid="rendered-comparison-error">
            {error}
          </span>
        ) : null}
      </div>

      {!loading && !error && rows.length === 0 ? (
        <div className="cf-explorer-empty" data-testid="rendered-comparison-empty">
          No response-vs-rendered differences in this audit — every captured page agrees between its raw response and rendered DOM.
        </div>
      ) : null}

      <DataGrid
        engine={engine}
        label="Rendered comparison"
        onActivateRow={(row) => onOpenUrl?.(row.urlId)}
        height={height}
      />
    </div>
  );
}
