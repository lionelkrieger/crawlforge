/**
 * ExplorerHost — the generic explorer surface (FS-CRAWL-002 §8.3).
 *
 * Renders any ExplorerDefinition through the same server-windowed grid
 * engine: view tabs, summary strip, instant filter, query builder, plugin
 * contributions, drill-down drawer, and the export handoff inspector.
 * Offline-first: no API → honest disconnected state; no active audit →
 * selection prompt. Mount with key={explorerId} so engine state resets
 * when switching explorers.
 *
 * @module surfaces/explorers/ExplorerHost
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { RowData } from '@tanstack/react-table';
import { useShell, useStoreSelector } from '../../shell/ShellContext';
import { selectActiveAudit } from '../../store/selectors';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type { ApiRouteKey } from '../../contracts/api/routes';
import type { ApiRouteParams } from '../../contracts/api/contracts-map';
import type { LogCorrelationResponse, LogCorrelationSummary } from '../../contracts/api/logs';
import type { AnalyticsResponse, AnalyticsConsentSummary } from '../../contracts/api/analytics';
import type { SearchResultsResponse } from '../../contracts/api/content';
import {
  createExplorerDataSource,
  createOfflineDataSource,
  arrayAdapter,
  dynamicColumnsAdapter,
  type ResponseAdapter
} from '../../grid/dataSource';
import { useGridEngine } from '../../grid/useGridEngine';
import { DataGrid } from '../../grid/DataGrid';
import { QueryBuilderPanel } from '../../grid/QueryBuilderPanel';
import type { GridContributionRegistry } from '../../grid/contributions';
import type { GridExportHandoff } from '../../grid/types';
import type { ExplorerDefinition, SummaryChip, DrilldownSpec, DrilldownDetail } from './explorerTypes';
import { logSummaryChips, consentChips } from './analysisExplorers';
import './explorers.css';

/** Host props. */
export interface ExplorerHostProps {
  readonly definition: ExplorerDefinition;
  /** Override the active audit (tests / split views). */
  readonly auditId?: string | null;
  /** Plugin contribution registry (WP-06-06). */
  readonly registry?: GridContributionRegistry;
}

/** Adapters for the non-array locked response shapes. */
function adapterFor(kind: ExplorerDefinition['adapterKind']): ResponseAdapter<RowData, ApiRouteKey> {
  switch (kind) {
    case 'dynamic':
      return dynamicColumnsAdapter<RowData, ApiRouteKey>();
    case 'search':
      return (data) => {
        const payload = data as unknown as SearchResultsResponse;
        return { rows: (payload.matches ?? []) as readonly RowData[], meta: { overview: payload.overview ?? [] } };
      };
    case 'correlation':
      return (data) => {
        const payload = data as unknown as LogCorrelationResponse;
        return { rows: (payload.rows ?? []) as readonly RowData[], meta: { summary: payload.summary } };
      };
    case 'analytics':
      return (data, query) => {
        const payload = data as unknown as AnalyticsResponse;
        const rows = query.view === 'findings' ? payload.findings ?? [] : payload.tags ?? [];
        return { rows: rows as readonly RowData[], meta: { consent: payload.consent } };
      };
    default:
      return arrayAdapter<RowData, ApiRouteKey>();
  }
}

/** The explorer surface. */
export function ExplorerHost({ definition, auditId: auditIdOverride, registry }: ExplorerHostProps) {
  const shell = useShell();
  const activeAudit = useStoreSelector(selectActiveAudit);
  const auditId = auditIdOverride !== undefined ? auditIdOverride : activeAudit?.id ?? null;
  const api: LoopbackApiClient | null = shell.services.api;

  const views = definition.views ?? [];
  const [viewId, setViewId] = useState<string | null>(views[0]?.id ?? null);
  const activeView = views.find((view) => view.id === viewId) ?? null;
  const [queryBuilderOpen, setQueryBuilderOpen] = useState(false);
  const [summaryChips, setSummaryChips] = useState<readonly SummaryChip[]>([]);
  const [drilldown, setDrilldown] = useState<DrilldownSpec | null>(null);
  const [drilldownDetail, setDrilldownDetail] = useState<DrilldownDetail | null>(null);
  const [drilldownLoading, setDrilldownLoading] = useState(false);
  const [handoff, setHandoff] = useState<GridExportHandoff | null>(null);
  const [copied, setCopied] = useState(false);

  // Dynamic columns flow engine → columns on the next render (ref carries
  // the latest value into the column factory without an effect loop).
  const dynamicColumnsRef = useRef<readonly string[]>([]);

  const source = useMemo(() => {
    if (!api || !auditId) return createOfflineDataSource<RowData>(`${definition.route}|offline`);
    return createExplorerDataSource<RowData, ApiRouteKey>({
      api,
      route: definition.route,
      params: { id: auditId } as ApiRouteParams<ApiRouteKey>,
      baseQuery: activeView?.query ?? {},
      adapter: adapterFor(definition.adapterKind)
    });
  }, [api, auditId, definition.route, definition.adapterKind, activeView?.query]);

  const dynamicKey = dynamicColumnsRef.current.join('|');
  const columnContext = useMemo(
    () => ({ dynamicColumns: dynamicColumnsRef.current, viewId }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [viewId, dynamicKey]
  );
  const columns = useMemo(() => definition.columns(columnContext), [definition, columnContext]);
  const queryFields = useMemo(() => definition.queryFields(columnContext), [definition, columnContext]);

  const handleActivate = useCallback((row: RowData) => {
    if (!definition.drilldown) return;
    const spec = definition.drilldown(row);
    setDrilldown(spec);
    setDrilldownDetail(null);
    setCopied(false);
    if (spec?.fetchDetail && api && auditId) {
      setDrilldownLoading(true);
      spec.fetchDetail(api, auditId)
        .then((detail) => setDrilldownDetail(detail))
        .catch(() => setDrilldownDetail(null))
        .finally(() => setDrilldownLoading(false));
    }
  }, [definition, api, auditId]);

  const engine = useGridEngine<RowData>({
    source,
    columns,
    rowKey: definition.rowKey,
    queryFields,
    explorerId: definition.id,
    initialSort: definition.defaultSort,
    onActivateRow: handleActivate
  });
  dynamicColumnsRef.current = engine.dynamicColumns;

  // Summary strip: definition-provided one-shot fetch.
  useEffect(() => {
    let cancelled = false;
    if (!api || !auditId || !definition.fetchSummary) {
      setSummaryChips([]);
      return;
    }
    definition.fetchSummary(api, auditId)
      .then((chips) => { if (!cancelled) setSummaryChips(chips); })
      .catch(() => { if (!cancelled) setSummaryChips([]); });
    return () => { cancelled = true; };
  }, [api, auditId, definition]);

  // Meta-derived chips (log correlation summary, analytics consent rollup).
  const metaChips = useMemo<readonly SummaryChip[]>(() => {
    const meta = engine.pageMeta;
    if (!meta) return [];
    if (definition.id === 'logs' && meta.summary) {
      return logSummaryChips(meta.summary as LogCorrelationSummary);
    }
    if (definition.id === 'analytics' && meta.consent) {
      return consentChips(meta.consent as AnalyticsConsentSummary);
    }
    return [];
  }, [engine.pageMeta, definition.id]);

  const handleExport = useCallback((payload: GridExportHandoff) => {
    setHandoff(payload);
    setCopied(false);
  }, []);

  const copyHandoff = useCallback(async () => {
    if (!handoff) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(handoff, null, 2));
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }, [handoff]);

  // ── Preconditions ───────────────────────────────────────────────────────
  if (!auditId) {
    return (
      <div className="cf-explorer cf-explorer-empty" data-testid="cf-explorer-no-audit">
        <span className="cf-explorer-empty-glyph">{definition.glyph}</span>
        <h3>{definition.title}</h3>
        <p>Select an audit to explore its {definition.title.toLowerCase()} data.</p>
      </div>
    );
  }

  const allChips = [...metaChips, ...summaryChips];

  return (
    <div className="cf-explorer" data-testid={`cf-explorer-${definition.id}`}>
      <header className="cf-explorer-header">
        <span className="cf-explorer-glyph" aria-hidden="true">{definition.glyph}</span>
        <div className="cf-explorer-heading">
          <h2 className="cf-explorer-title">{definition.title}</h2>
          <p className="cf-explorer-tagline">{definition.tagline}</p>
        </div>
        {views.length > 0 ? (
          <div className="cf-explorer-views" role="tablist" aria-label={`${definition.title} views`}>
            {views.map((view) => (
              <button
                key={view.id}
                type="button"
                role="tab"
                aria-selected={view.id === viewId}
                className={`cf-explorer-view${view.id === viewId ? ' cf-explorer-view-active' : ''}`}
                onClick={() => setViewId(view.id)}
                data-testid={`cf-explorer-view-${view.id}`}
              >
                {view.label}
              </button>
            ))}
          </div>
        ) : null}
      </header>

      {!api ? (
        <div className="cf-explorer-offline" role="status" data-testid="cf-explorer-offline">
          Backend disconnected — {definition.title} data is unavailable offline. Reconnect to load server windows.
        </div>
      ) : null}

      {allChips.length > 0 ? (
        <div className="cf-explorer-summary" data-testid="cf-explorer-summary">
          {allChips.map((chip, index) => (
            <span key={`${chip.label}-${index}`} className={`cf-summary-chip cf-summary-${chip.tone ?? 'neutral'}`}>
              <span className="cf-summary-label">{chip.label}</span>
              <span className="cf-summary-value">{chip.value}</span>
            </span>
          ))}
        </div>
      ) : null}

      {queryBuilderOpen ? (
        <QueryBuilderPanel
          fields={queryFields}
          explorerId={definition.id}
          initialTree={engine.state.ruleTree}
          api={api}
          registry={registry}
          onApply={engine.applyRuleTree}
          onClose={() => setQueryBuilderOpen(false)}
        />
      ) : null}

      <DataGrid
        engine={engine}
        label={definition.title}
        onActivateRow={handleActivate}
        formatterFor={registry ? (key) => registry.formatterFor(key) : undefined}
        onToggleQueryBuilder={() => setQueryBuilderOpen((open) => !open)}
        queryBuilderOpen={queryBuilderOpen}
        onExport={handleExport}
        height={460}
      />

      {engine.totalRows === 0 && !engine.loading && definition.emptyHint ? (
        <p className="cf-explorer-hint" data-testid="cf-explorer-hint">{definition.emptyHint}</p>
      ) : null}

      {/* Drill-down drawer */}
      {drilldown ? (
        <aside className="cf-drilldown" role="dialog" aria-label={drilldown.title} data-testid="cf-drilldown">
          <div className="cf-drilldown-header">
            <div>
              <h3 className="cf-drilldown-title">{drilldown.title}</h3>
              {drilldown.subtitle ? <p className="cf-drilldown-subtitle">{drilldown.subtitle}</p> : null}
            </div>
            <button type="button" className="cf-drilldown-close" onClick={() => setDrilldown(null)} aria-label="Close details" data-testid="cf-drilldown-close">✕</button>
          </div>
          <div className="cf-drilldown-fields">
            {drilldown.fields.map((field, index) => (
              <div key={index} className="cf-drilldown-field">
                <span className="cf-drilldown-label">{field.label}</span>
                <span className="cf-drilldown-value">{field.value}</span>
              </div>
            ))}
          </div>
          {drilldownLoading ? <p className="cf-drilldown-loading">Loading detail…</p> : null}
          {drilldownDetail?.sections.map((section, index) => (
            <section key={index} className="cf-drilldown-section">
              <h4 className="cf-drilldown-section-heading">{section.heading}</h4>
              {section.fields.length === 0 ? (
                <p className="cf-drilldown-none">None</p>
              ) : (
                section.fields.map((field, fieldIndex) => (
                  <div key={fieldIndex} className="cf-drilldown-field">
                    <span className="cf-drilldown-label">{field.label}</span>
                    <span className="cf-drilldown-value">{field.value}</span>
                  </div>
                ))
              )}
            </section>
          ))}
        </aside>
      ) : null}

      {/* Export handoff inspector */}
      {handoff ? (
        <div className="cf-handoff" role="dialog" aria-label="Export handoff" data-testid="cf-handoff">
          <div className="cf-handoff-header">
            <h3>Export handoff</h3>
            <button type="button" className="cf-drilldown-close" onClick={() => setHandoff(null)} aria-label="Close export handoff" data-testid="cf-handoff-close">✕</button>
          </div>
          <p className="cf-handoff-note">
            This payload carries the current selection, filters, sort chain, and locked query tree into the export flow.
          </p>
          <pre className="cf-handoff-json" data-testid="cf-handoff-json">{JSON.stringify(handoff, null, 2)}</pre>
          <button type="button" className="cf-handoff-copy" onClick={() => void copyHandoff()} data-testid="cf-handoff-copy">
            {copied ? 'Copied ✓' : 'Copy payload'}
          </button>
        </div>
      ) : null}
    </div>
  );
}
