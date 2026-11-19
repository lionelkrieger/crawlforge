/**
 * Report Dashboard (WP-09-08, FS-CRAWL-001 §8.2: "Summary cards,
 * severity/category charts, crawl-health panel, top-issue lists"; UI-11).
 *
 * Root testid `dashboard-charts` (the acceptance walker's UI-11 marker).
 * Composes the accepted WP-06-04 aggregate surfaces (AuditDashboard charts,
 * CrawlHealthPanel) with the report-window essentials they do not carry:
 *
 *  - REAL crawl counters on the WIRE keys queued/fetched/error/blocked/
 *    wafBlocked (AuditCounters; `audits.get`, with an optional live override
 *    from the accepted liveProgress store — WP-09-06 fixes the progress
 *    mapper in parallel, this component codes against the wire shape).
 *  - Category + overall scores (`audits.scores`, suppressed findings excluded
 *    by the backend scoring engine).
 *  - A bounded top-issues window (`audits.listFindings`, severity-sorted)
 *    with drill-down handoff through `onOpenUrl`.
 *
 * Offline-first: every loader degrades to null and renders an honest
 * unavailable state — never placeholder frames, never fabricated numbers.
 * Charts render ONLY from precomputed aggregates (FS-CRAWL-002 §8.4).
 *
 * @module surfaces/dashboards/ReportDashboard
 */

import { useEffect, useState } from 'react';
import type {
  AuditCounters,
  AuditDashboardSummary,
  AuditDetail,
  AuditScores,
  Finding
} from '../../contracts/api/audits';
import type { LoopbackApiClient } from '../../contracts/api/client';
import type { ChartRendererFactory } from '../../charts/chartPorts';
import type { ChartSelection } from '../../charts/crossFilter';
import { Badge, severityTone } from '../explorers/cells';
import { AuditDashboard } from './AuditDashboard';
import { CrawlHealthPanel } from './CrawlHealthPanel';
import { loadDashboardAggregates, EMPTY_AGGREGATES, type DashboardAggregates } from './dashboardData';
import './reportDashboard.css';

/** Report dashboard props. */
export interface ReportDashboardProps {
  readonly api: LoopbackApiClient | null;
  readonly auditId: string | null;
  /** Live counter override (accepted liveProgress store); wire keys. */
  readonly liveCounters?: AuditCounters | null;
  /** Chart renderer port (tests inject a mock; production lazy singleton). */
  readonly factory?: ChartRendererFactory;
  /** Drill-down handoff: the shell opens the URL explorer / URL detail. */
  readonly onOpenUrl?: (urlId: string) => void;
  /** Chart click → explorer cross-filter bridge (accepted store actions). */
  readonly onCrossFilter?: (selection: ChartSelection) => void;
  /** Bump to reload aggregates (e.g. after a suppression change). */
  readonly refreshKey?: number;
}

/** The five crawl counter wire keys (AuditCounters). */
const COUNTER_KEYS = ['queued', 'fetched', 'error', 'blocked', 'wafBlocked'] as const;
type CounterKey = (typeof COUNTER_KEYS)[number];

const COUNTER_LABELS: Record<CounterKey, string> = {
  queued: 'Queued',
  fetched: 'Fetched',
  error: 'Errors',
  blocked: 'Blocked',
  wafBlocked: 'WAF blocked'
};

/** Bounded top-issues window size (aggregate posture: bounded explorer window). */
const TOP_ISSUES_LIMIT = 10;

/** Degrades a loader failure to null (offline-first, never throws). */
async function safe<T>(load: () => Promise<T>): Promise<T | null> {
  try {
    return await load();
  } catch {
    return null;
  }
}

/** One crawl counter tile (real wire value, honest unavailable state). */
function CounterTile({ counterKey, value }: { counterKey: CounterKey; value: number | null }) {
  return (
    <div className="cf-dashboard-counter" data-testid={`dashboard-counter-tile-${counterKey}`}>
      <div className="cf-dashboard-counter-label">{COUNTER_LABELS[counterKey]}</div>
      <div className="cf-dashboard-counter-value" data-testid={`dashboard-counter-${counterKey}`}>
        {value === null ? '—' : value.toLocaleString()}
      </div>
    </div>
  );
}

/** The report dashboard surface (root testid `dashboard-charts`). */
export function ReportDashboard({
  api,
  auditId,
  liveCounters,
  factory,
  onOpenUrl,
  onCrossFilter,
  refreshKey = 0
}: ReportDashboardProps) {
  const [aggregates, setAggregates] = useState<DashboardAggregates>(EMPTY_AGGREGATES);
  const [detail, setDetail] = useState<AuditDetail | null>(null);
  const [topIssues, setTopIssues] = useState<readonly Finding[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  useEffect(() => {
    if (!api || !auditId) {
      setAggregates(EMPTY_AGGREGATES);
      setDetail(null);
      setTopIssues(null);
      setLoadError(null);
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    setLoading(true);
    setLoadError(null);
    void (async () => {
      const [loadedAggregates, detailEnvelope, findingsEnvelope] = await Promise.all([
        loadDashboardAggregates(api, auditId, controller.signal),
        safe(() => api.request('audits.get', { params: { id: auditId }, signal: controller.signal })),
        safe(() =>
          api.request('audits.listFindings', {
            params: { id: auditId },
            query: { offset: 0, limit: TOP_ISSUES_LIMIT, sort: 'severity', order: 'asc' },
            signal: controller.signal
          })
        )
      ]);
      if (controller.signal.aborted) return;
      setAggregates(loadedAggregates);
      setDetail(detailEnvelope && !detailEnvelope.error ? detailEnvelope.data ?? null : null);
      setTopIssues(findingsEnvelope && !findingsEnvelope.error ? findingsEnvelope.data ?? null : null);
      if (detailEnvelope?.error) {
        setLoadError(detailEnvelope.error.message);
      } else if (findingsEnvelope?.error) {
        setLoadError(findingsEnvelope.error.message);
      }
      setLoading(false);
    })();
    return () => controller.abort();
  }, [api, auditId, refreshKey]);

  const counters: AuditCounters | null = liveCounters ?? detail?.counters ?? null;
  const summary: AuditDashboardSummary | null = aggregates.summary;
  const scores: AuditScores | null = aggregates.scores;
  const busy = loading;

  return (
    <div className="cf-report-dashboard" data-testid="dashboard-charts">
      {!auditId ? (
        <div className="cf-explorer-empty" data-testid="dashboard-no-audit">
          Select an audit to open its report dashboard.
        </div>
      ) : !api ? (
        <div className="cf-explorer-empty" data-testid="dashboard-offline">
          Backend disconnected — dashboard aggregates are unavailable offline.
        </div>
      ) : (
        <>
          {busy ? (
            <div className="cf-explorer-loading" data-testid="dashboard-loading">
              Loading dashboard aggregates…
            </div>
          ) : null}
          {loadError ? (
            <div className="cf-explorer-error" role="alert" data-testid="dashboard-error">
              {loadError}
            </div>
          ) : null}

          {/* Real crawl counters (wire keys). */}
          <div className="cf-dashboard-counters" data-testid="dashboard-counters" role="group" aria-label="Crawl counters">
            {COUNTER_KEYS.map((key) => (
              <CounterTile
                key={key}
                counterKey={key}
                value={counters ? (key === 'wafBlocked' ? counters.wafBlocked ?? 0 : counters[key]) : null}
              />
            ))}
            {counters === null && !busy ? (
              <div className="cf-explorer-note" data-testid="dashboard-counters-unavailable">
                Counters unavailable — the audit record could not be read.
              </div>
            ) : null}
          </div>

          {/* Category + overall scores (suppressed findings excluded server-side). */}
          <div className="cf-dashboard-scores" data-testid="dashboard-scores">
            <div className="cf-dashboard-section-title">Scores</div>
            {scores ? (
              <>
                <div className="cf-dashboard-score-overall" data-testid="dashboard-score-overall">
                  Overall {scores.overall} / 100
                </div>
                {scores.categories.map((entry) => (
                  <div className="cf-dashboard-score-row" key={entry.category} data-testid={`dashboard-score-row-${entry.category}`}>
                    <span className="cf-dashboard-score-category">{entry.category}</span>
                    <span className="cf-dashboard-score-track" aria-hidden>
                      <span
                        className="cf-dashboard-score-fill"
                        style={{ width: `${Math.max(0, Math.min(100, entry.score))}%` }}
                      />
                    </span>
                    <span className="cf-dashboard-score-value" data-testid={`dashboard-score-${entry.category}`}>
                      {entry.score}
                    </span>
                    <span className="cf-dashboard-score-count">{entry.findingCount} findings</span>
                  </div>
                ))}
              </>
            ) : (
              <div className="cf-explorer-note" data-testid="dashboard-scores-unavailable">
                {busy ? 'Scores loading…' : 'Scores unavailable — the audit has not been scored yet.'}
              </div>
            )}
          </div>

          {/* Severity/category/status distributions + summary cards (WP-06-04). */}
          <AuditDashboard summary={summary} factory={factory} onCrossFilter={onCrossFilter} />

          {/* Crawl health: numeric rollup + host throttle / WAF panels. */}
          <div className="cf-dashboard-section-title">Crawl health</div>
          {summary ? (
            <div className="cf-dashboard-crawl-health" data-testid="dashboard-crawl-health">
              <span>Total URLs {summary.crawlHealth.totalUrls.toLocaleString()}</span>
              <span>Fetched {summary.crawlHealth.fetched.toLocaleString()}</span>
              <span>Errors {summary.crawlHealth.errorCount.toLocaleString()}</span>
              <span>Blocked {summary.crawlHealth.blockedCount.toLocaleString()}</span>
              <span>
                Avg TTFB{' '}
                {summary.crawlHealth.averageTtfbMs === null
                  ? '—'
                  : `${Math.round(summary.crawlHealth.averageTtfbMs)} ms`}
              </span>
            </div>
          ) : null}
          <CrawlHealthPanel hosts={aggregates.hosts} waf={aggregates.waf} factory={factory} onCrossFilter={onCrossFilter} />

          {/* Top issues: bounded severity-sorted findings window. */}
          <div className="cf-dashboard-top-issues" data-testid="dashboard-top-issues">
            <div className="cf-dashboard-section-title">Top issues</div>
            {topIssues === null ? (
              <div className="cf-explorer-note" data-testid="dashboard-top-issues-unavailable">
                {busy ? 'Top issues loading…' : 'Top issues unavailable — the findings window could not be read.'}
              </div>
            ) : topIssues.length === 0 ? (
              <div className="cf-explorer-note" data-testid="dashboard-top-issues-empty">
                No findings — clean audit.
              </div>
            ) : (
              <ul className="cf-dashboard-issue-list">
                {topIssues.map((finding) => (
                  <li key={finding.id} className="cf-dashboard-issue" data-testid={`dashboard-top-issue-${finding.id}`}>
                    <Badge tone={severityTone(finding.severity)}>{finding.severity}</Badge>
                    <span className="cf-dashboard-issue-rule">{finding.ruleKey}</span>
                    <span className="cf-dashboard-issue-message" title={finding.message}>{finding.message}</span>
                    {finding.urlId !== null && onOpenUrl ? (
                      <button
                        type="button"
                        className="cf-evidence-open-url"
                        data-testid={`dashboard-top-issue-open-${finding.id}`}
                        onClick={() => onOpenUrl(finding.urlId as string)}
                      >
                        Open URL
                      </button>
                    ) : null}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}
    </div>
  );
}
