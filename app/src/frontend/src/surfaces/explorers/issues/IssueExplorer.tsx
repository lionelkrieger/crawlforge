/**
 * Issue Explorer (WP-09-08, FS-CRAWL-001 §8.2 / §7.5, FS-CRAWL-002 §8.3/§7.1).
 *
 * The report window's finding surface: a SERVER-windowed, virtualized grid
 * (≤ MAX_DOM_ROWS DOM rows at any dataset size — the shared grid engine's
 * double invariant) over the frozen `audits.listFindings` route, with
 * category/rule/status/severity filters, a finding detail drawer with
 * generic evidence drill-down, and reversible suppression wired to the
 * accepted WP-03-08 engine (`suppressions.create` / `suppressions.delete`
 * → synchronous score recompute → `audits.scores` delta).
 *
 * Reuse posture: the shared explorer modules (cells, columnUtils, the
 * `issueExplorer` definition, explorers.css) and the grid engine are
 * imported, never edited. This component adds the issue-specific filter
 * bar, suppression UX, and evidence drill-down around them.
 *
 * Suppressed view honesty: the findings route returns suppressed rows
 * unflagged and has no suppression filter param, so the suppressed view
 * binds the grid to the selected suppression's server-expressible dimensions
 * (ruleKey/urlId) — totals stay server-true. The all-findings view badges
 * every row with its live coverage status. A server-side active-only view
 * needs a findings `suppressed` wire flag/filter (mounting handoff item).
 *
 * @module surfaces/explorers/issues/IssueExplorer
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Finding, AuditScores } from '../../../contracts/api/audits';
import type { SuppressionState } from '../../../contracts/api/projects';
import type { LoopbackApiClient } from '../../../contracts/api/client';
import { createExplorerDataSource, createOfflineDataSource, arrayAdapter } from '../../../grid/dataSource';
import { useGridEngine } from '../../../grid/useGridEngine';
import { DataGrid } from '../../../grid/DataGrid';
import { issueExplorer } from '../coreExplorers';
import { makeColumn } from '../columnUtils';
import { Badge } from '../cells';
import { FindingDetail } from './FindingDetail';
import {
  buildSuppressionRequest,
  computeScoreDelta,
  findingSuppression,
  formatDelta,
  isFindingSuppressed,
  suppressionLabel,
  suppressionServerQuery,
  upsertSuppression,
  type IssueStatusFilter,
  type ScoreDelta
} from './suppression';
import '../explorers.css';
import './issues.css';

/** Severity wire keys (lowercase enum names, ApiEnumConverter). */
export const SEVERITY_WIRE_KEYS = ['error', 'warning', 'notice', 'pass'] as const;

/** Category wire keys (lowercase AuditCategory enum names). WP-10-04 (D-021):
 * the three additive §7.4 categories append to the locked baseline list. */
export const CATEGORY_WIRE_KEYS = [
  'indexability', 'content', 'linking', 'structureddata', 'accessibility',
  'performance', 'crawl', 'custom', 'logintelligence', 'aisearchgeo', 'analyticstagging',
  'images', 'securityandprotocol', 'internationalization'
] as const;

/** Issue explorer props. */
export interface IssueExplorerProps {
  readonly api: LoopbackApiClient | null;
  readonly auditId: string | null;
  /** Suppressions are project-scoped; null disables suppression actions. */
  readonly projectId: string | null;
  /** Drill-down handoff: the shell opens the URL explorer / URL detail. */
  readonly onOpenUrl?: (urlId: string) => void;
  /**
   * Seed suppressions (e.g. from a future `suppressions.list` endpoint).
   * The component also tracks every create/delete response itself.
   */
  readonly initialSuppressions?: readonly SuppressionState[];
  /** Notified after every suppression state change (store sync handoff). */
  readonly onSuppressionsChanged?: (suppressions: readonly SuppressionState[]) => void;
  readonly height?: number;
}

/** Degrades a scores load to null (offline-first). */
async function loadScores(api: LoopbackApiClient, auditId: string): Promise<AuditScores | null> {
  try {
    const envelope = await api.request('audits.scores', { params: { id: auditId } });
    return envelope.error ? null : envelope.data;
  } catch {
    return null;
  }
}

/** The issue explorer surface (root testid `issues-grid`). */
export function IssueExplorer({
  api,
  auditId,
  projectId,
  onOpenUrl,
  initialSuppressions,
  onSuppressionsChanged,
  height = 480
}: IssueExplorerProps) {
  const [severity, setSeverity] = useState<string>('');
  const [category, setCategory] = useState<string>('');
  const [ruleInput, setRuleInput] = useState<string>('');
  const [ruleKey, setRuleKey] = useState<string>('');
  const [status, setStatus] = useState<IssueStatusFilter>('all');
  const [selectedSuppressionKey, setSelectedSuppressionKey] = useState<string | null>(null);
  const [suppressions, setSuppressions] = useState<readonly SuppressionState[]>(initialSuppressions ?? []);
  const [scores, setScores] = useState<AuditScores | null>(null);
  const [scoreDelta, setScoreDelta] = useState<ScoreDelta | null>(null);
  const [detail, setDetail] = useState<Finding | null>(null);
  const [busy, setBusy] = useState(false);
  const [suppressionError, setSuppressionError] = useState<string | null>(null);
  // Latest grid refresh handle: suppression callbacks always refresh the
  // CURRENT engine window (filters may have changed since they were created).
  const refreshRef = useRef<() => void>(() => {});

  // Seed suppressions from the prop (lead wiring / future list endpoint).
  useEffect(() => {
    setSuppressions(initialSuppressions ?? []);
  }, [initialSuppressions]);

  // Debounce the rule-key filter so typing does not refetch per keystroke.
  useEffect(() => {
    const timer = setTimeout(() => setRuleKey(ruleInput.trim()), 250);
    return () => clearTimeout(timer);
  }, [ruleInput]);

  // Current score snapshot (refreshed after every suppression change).
  useEffect(() => {
    if (!api || !auditId) {
      setScores(null);
      return;
    }
    let cancelled = false;
    void loadScores(api, auditId).then((next) => {
      if (!cancelled) setScores(next);
    });
    return () => {
      cancelled = true;
    };
  }, [api, auditId]);

  const activeSuppressions = useMemo(
    () => suppressions.filter((entry) => entry.suppressed),
    [suppressions]
  );
  const selectedSuppression = useMemo(
    () =>
      activeSuppressions.find((entry) => entry.key === selectedSuppressionKey) ??
      activeSuppressions[0] ??
      null,
    [activeSuppressions, selectedSuppressionKey]
  );

  // Server-expressible domain filters. The suppressed view binds the grid to
  // the selected suppression's dimensions so totals stay server-true.
  const baseQuery = useMemo(() => {
    const query: Record<string, unknown> = {};
    if (severity) query.severity = severity;
    if (category) query.category = category;
    if (ruleKey) query.ruleKey = ruleKey;
    if (status === 'suppressed' && selectedSuppression) {
      Object.assign(query, suppressionServerQuery(selectedSuppression));
    }
    return query;
  }, [severity, category, ruleKey, status, selectedSuppression]);

  const source = useMemo(() => {
    if (!api || !auditId || (status === 'suppressed' && !selectedSuppression)) {
      return createOfflineDataSource<Finding>('issues');
    }
    return createExplorerDataSource<Finding, 'audits.listFindings'>({
      api,
      route: 'audits.listFindings',
      params: { id: auditId },
      baseQuery,
      adapter: arrayAdapter<Finding, 'audits.listFindings'>()
    });
  }, [api, auditId, status, selectedSuppression, baseQuery]);

  const applySuppressions = useCallback(
    (next: readonly SuppressionState[]) => {
      setSuppressions(next);
      onSuppressionsChanged?.(next);
    },
    [onSuppressionsChanged]
  );

  const handleSuppress = useCallback(
    async (finding: Finding, scope: 'finding' | 'rule') => {
      if (!api || !projectId || busy) return;
      setBusy(true);
      setSuppressionError(null);
      try {
        const before = scores ?? (auditId ? await loadScores(api, auditId) : null);
        const envelope = await api.request('suppressions.create', {
          params: { id: projectId },
          body: buildSuppressionRequest(finding, scope)
        });
        if (envelope.error || !envelope.data) {
          setSuppressionError(envelope.error?.message ?? 'Suppression failed.');
          return;
        }
        applySuppressions(upsertSuppression(suppressions, envelope.data));
        const after = auditId ? await loadScores(api, auditId) : null;
        setScores(after);
        setScoreDelta(computeScoreDelta(before, after));
        refreshRef.current();
      } catch (error) {
        setSuppressionError(error instanceof Error ? error.message : 'Suppression failed.');
      } finally {
        setBusy(false);
      }
    },
    [api, projectId, busy, scores, auditId, suppressions, applySuppressions]
  );

  const handleUnsuppress = useCallback(
    async (finding: Finding) => {
      if (!api || !projectId || busy) return;
      const covering = findingSuppression(finding, suppressions);
      if (!covering) {
        setSuppressionError('No active suppression covers this finding.');
        return;
      }
      setBusy(true);
      setSuppressionError(null);
      try {
        const before = scores ?? (auditId ? await loadScores(api, auditId) : null);
        const envelope = await api.request('suppressions.delete', {
          params: { id: projectId, key: covering.key }
        });
        if (envelope.error || !envelope.data) {
          setSuppressionError(envelope.error?.message ?? 'Unsuppress failed.');
          return;
        }
        applySuppressions(upsertSuppression(suppressions, envelope.data));
        const after = auditId ? await loadScores(api, auditId) : null;
        setScores(after);
        setScoreDelta(computeScoreDelta(before, after));
        refreshRef.current();
      } catch (error) {
        setSuppressionError(error instanceof Error ? error.message : 'Unsuppress failed.');
      } finally {
        setBusy(false);
      }
    },
    [api, projectId, busy, scores, auditId, suppressions, applySuppressions]
  );

  // Columns: the shared issue definition plus live suppression status and
  // the reversible suppress/unsuppress action. Cells close over the current
  // suppression state so badges/actions recompute after every change.
  const columns = useMemo(() => {
    const base = issueExplorer.columns({ dynamicColumns: [], viewId: null });
    const statusColumn = {
      ...makeColumn<Finding, string>(
        'suppressionStatus',
        'Status',
        (row) => (isFindingSuppressed(row.ruleKey, row.urlId, suppressions) ? 'suppressed' : 'active'),
        {
          size: 110,
          cell: (value) => <Badge tone={value === 'suppressed' ? 'neutral' : 'success'}>{value}</Badge>
        }
      ),
      enableSorting: false
    };
    const actionColumn = {
      ...makeColumn<Finding, string>('suppressionAction', 'Suppression', (row) => row.id, {
        size: 140,
        cell: (_value, row) => {
          const covering = findingSuppression(row, suppressions);
          return (
            <button
              type="button"
              className="cf-suppression-button cf-suppression-row-button"
              data-testid={covering ? `issues-unsuppress-${row.id}` : `issues-suppress-${row.id}`}
              disabled={busy || projectId === null}
              onClick={(event) => {
                event.stopPropagation();
                if (covering) void handleUnsuppress(row);
                else void handleSuppress(row, 'finding');
              }}
            >
              {covering ? 'Unsuppress' : 'Suppress'}
            </button>
          );
        }
      }),
      enableSorting: false
    };
    return [...base, statusColumn, actionColumn];
  }, [suppressions, busy, projectId, handleSuppress, handleUnsuppress]);

  const queryFields = useMemo(
    () => issueExplorer.queryFields({ dynamicColumns: [], viewId: null }),
    []
  );

  const engine = useGridEngine<Finding>({
    source,
    columns,
    rowKey: issueExplorer.rowKey,
    queryFields,
    explorerId: 'issues',
    initialSort: [{ field: 'severity', order: 'asc' }],
    onActivateRow: (row) => setDetail(row)
  });

  // Keep the refresh handle current (event handlers run after effects flush).
  useEffect(() => {
    refreshRef.current = engine.refresh;
  }, [engine.refresh]);

  const suppressedEmpty = status === 'suppressed' && activeSuppressions.length === 0;

  return (
    <div className="cf-explorer cf-explorer-issues" data-testid="issues-grid">
      <div className="cf-explorer-header">
        <span className="cf-explorer-glyph" aria-hidden>{issueExplorer.glyph}</span>
        <div className="cf-explorer-titles">
          <h2 className="cf-explorer-title">{issueExplorer.title}</h2>
          <p className="cf-explorer-tagline">{issueExplorer.tagline}</p>
        </div>
        {scores ? (
          <span className="cf-explorer-meta" data-testid="issues-score-overall" title="Overall audit score (suppressed findings excluded)">
            Score {scores.overall}
          </span>
        ) : null}
      </div>

      {scoreDelta ? (
        <div className="cf-score-delta" data-testid="issues-score-delta" role="status">
          <span>
            Overall {scoreDelta.overallBefore} → {scoreDelta.overallAfter} ({formatDelta(scoreDelta.overallDelta)})
          </span>
          {scoreDelta.categories
            .filter((entry) => entry.delta !== 0)
            .map((entry) => (
              <span key={entry.category} className="cf-score-delta-category" data-testid={`issues-score-delta-${entry.category}`}>
                {entry.category} {formatDelta(entry.delta)}
              </span>
            ))}
        </div>
      ) : null}

      <div className="cf-issues-filters" role="group" aria-label="Issue filters">
        <label className="cf-issues-filter">
          <span>Severity</span>
          <select
            data-testid="issues-filter-severity"
            value={severity}
            onChange={(event) => setSeverity(event.target.value)}
          >
            <option value="">All severities</option>
            {SEVERITY_WIRE_KEYS.map((key) => (
              <option key={key} value={key}>{key}</option>
            ))}
          </select>
        </label>
        <label className="cf-issues-filter">
          <span>Category</span>
          <select
            data-testid="issues-filter-category"
            value={category}
            onChange={(event) => setCategory(event.target.value)}
          >
            <option value="">All categories</option>
            {CATEGORY_WIRE_KEYS.map((key) => (
              <option key={key} value={key}>{key}</option>
            ))}
          </select>
        </label>
        <label className="cf-issues-filter">
          <span>Rule</span>
          <input
            type="text"
            data-testid="issues-filter-rule"
            value={ruleInput}
            placeholder="Filter by rule key…"
            onChange={(event) => setRuleInput(event.target.value)}
          />
        </label>
        <label className="cf-issues-filter">
          <span>Status</span>
          <select
            data-testid="issues-filter-status"
            value={status}
            onChange={(event) => {
              setStatus(event.target.value as IssueStatusFilter);
              setSelectedSuppressionKey(null);
            }}
          >
            <option value="all">All findings</option>
            <option value="suppressed">Suppressed</option>
          </select>
        </label>
        {status === 'suppressed' && activeSuppressions.length > 0 ? (
          <label className="cf-issues-filter">
            <span>Suppression</span>
            <select
              data-testid="issues-suppression-picker"
              value={selectedSuppression?.key ?? ''}
              onChange={(event) => setSelectedSuppressionKey(event.target.value)}
            >
              {activeSuppressions.map((entry) => (
                <option key={entry.key} value={entry.key}>{suppressionLabel(entry)}</option>
              ))}
            </select>
          </label>
        ) : null}
      </div>

      {suppressionError ? (
        <div className="cf-explorer-error" role="alert" data-testid="issues-suppression-error">
          {suppressionError}
        </div>
      ) : null}
      {busy ? (
        <div className="cf-explorer-loading" data-testid="issues-suppression-busy">
          Updating suppression and recomputing scores…
        </div>
      ) : null}
      {status === 'suppressed' ? (
        <div className="cf-explorer-note" data-testid="issues-status-note">
          Suppressed view — the grid is scoped to the selected suppression's rule/URL dimensions
          (server-windowed totals). Unsuppress any row to reverse it; scores recompute immediately.
        </div>
      ) : null}
      {projectId === null ? (
        <div className="cf-explorer-note" data-testid="issues-no-project-note">
          Suppression actions are disabled — suppressions are project-scoped and no project is selected.
        </div>
      ) : null}

      {!auditId ? (
        <div className="cf-explorer-empty" data-testid="issues-no-audit">
          Select an audit to explore its findings.
        </div>
      ) : !api ? (
        <div className="cf-explorer-empty" data-testid="issues-offline">
          Backend disconnected — the issue explorer is unavailable offline. No cached findings are shown.
        </div>
      ) : suppressedEmpty ? (
        <div className="cf-explorer-empty" data-testid="issues-suppressed-empty">
          No suppressions yet — suppress a finding (row action or detail panel) to see it here.
        </div>
      ) : (
        <>
          {!engine.loading && !engine.error && engine.totalRows === 0 ? (
            <div className="cf-explorer-note" data-testid="issues-empty">
              No findings match the current filters — a clean result for this scope.
            </div>
          ) : null}
          <DataGrid
            engine={engine}
            label="Issue explorer"
            onActivateRow={(row) => setDetail(row)}
            height={height}
          />
        </>
      )}

      {detail ? (
        <FindingDetail
          finding={detail}
          suppressions={suppressions}
          projectId={projectId}
          busy={busy}
          onOpenUrl={onOpenUrl}
          onSuppress={(finding, scope) => void handleSuppress(finding, scope)}
          onUnsuppress={(finding) => void handleUnsuppress(finding)}
          onClose={() => setDetail(null)}
        />
      ) : null}
    </div>
  );
}
