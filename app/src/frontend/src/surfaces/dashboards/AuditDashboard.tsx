/**
 * Audit dashboard (FS-CRAWL-001 §8.2): summary cards, severity/category/
 * status distributions, crawl-health panel, top-issue list. Charts render
 * from precomputed aggregates only; clicking a chart cross-filters the
 * explorers through the accepted store actions (via the onCrossFilter
 * bridge — this surface never edits the store itself).
 *
 * @module surfaces/dashboards/AuditDashboard
 */

import type { AuditDashboardSummary } from '../../contracts/api/audits';
import type { ChartRendererFactory } from '../../charts/chartPorts';
import type { ChartSelection } from '../../charts/crossFilter';
import { EChart } from '../../charts/EChart';
import {
  categoryDistributionOption,
  crawlHealthOption,
  severityDistributionOption,
  sortedCountEntries,
  statusDistributionOption,
  summaryCardModels,
  type SummaryCardModel
} from '../../charts/chartTransforms';

/** Audit dashboard props. */
export interface AuditDashboardProps {
  readonly summary: AuditDashboardSummary | null;
  readonly factory?: ChartRendererFactory;
  readonly onCrossFilter?: (selection: ChartSelection) => void;
}

const toneColors: Record<SummaryCardModel['tone'], string> = {
  neutral: '#c9d1e0',
  success: '#37b24d',
  warning: '#f59f00',
  danger: '#f03e3e',
  info: '#22b8cf'
};

/** Summary cards + distribution charts + top issues for one audit. */
export function AuditDashboard({ summary, factory, onCrossFilter }: AuditDashboardProps) {
  if (!summary) {
    return (
      <div data-testid="cf-dashboard-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        Dashboard aggregates unavailable — the backend is offline or the audit has not completed.
      </div>
    );
  }

  const cards = summaryCardModels(summary);
  const categoryEntries = sortedCountEntries(summary.countsByCategory).sort(
    ([a, va], [b, vb]) => vb - va || (a < b ? -1 : 1)
  );
  const crossFilter = (selection: ChartSelection) => () => onCrossFilter?.(selection);

  return (
    <div data-testid="cf-dashboard" style={{ display: 'grid', gap: 12 }}>
      <div data-testid="cf-dashboard-cards" style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
        {cards.map((card) => (
          <div
            key={card.key}
            data-testid={`cf-card-${card.key}`}
            style={{
              background: '#1d2436',
              border: '1px solid #2b3348',
              borderRadius: 6,
              padding: '8px 14px',
              minWidth: 110
            }}
          >
            <div style={{ color: '#8892a8', fontSize: 11 }}>{card.label}</div>
            <div style={{ color: toneColors[card.tone], fontSize: 20, fontWeight: 650 }}>{card.value}</div>
          </div>
        ))}
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
        <EChart
          factory={factory}
          option={statusDistributionOption(summary.countsByStatus)}
          ariaLabel="URL status distribution"
          exportName="status-distribution"
          onChartClick={(params) => onCrossFilter?.({ kind: 'status', statusClass: params.name })}
        />
        <EChart
          factory={factory}
          option={severityDistributionOption(summary.countsBySeverity)}
          ariaLabel="Findings by severity"
          exportName="severity-distribution"
          onChartClick={(params) => onCrossFilter?.({ kind: 'severity', severity: params.name })}
        />
        <EChart
          factory={factory}
          option={categoryDistributionOption(summary.countsByCategory)}
          ariaLabel="Findings by category"
          exportName="category-distribution"
          onChartClick={(params) => onCrossFilter?.({ kind: 'category', category: params.name })}
        />
        <EChart
          factory={factory}
          option={crawlHealthOption(summary.crawlHealth)}
          ariaLabel="Crawl health"
          exportName="crawl-health"
        />
      </div>

      <div data-testid="cf-category-breakdown" style={{ background: '#1d2436', border: '1px solid #2b3348', borderRadius: 6, padding: 12 }}>
        <div style={{ color: '#c9d1e0', fontSize: 13, fontWeight: 600, marginBottom: 8 }}>Category breakdown</div>
        {categoryEntries.length === 0 ? (
          <div style={{ color: '#8892a8', fontSize: 12 }}>No findings — clean audit.</div>
        ) : (
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {categoryEntries.map(([category, count]) => (
              <li key={category} style={{ color: '#aab4c8', fontSize: 12, marginBottom: 4 }}>
                <button
                  type="button"
                  data-testid={`cf-category-${category}`}
                  onClick={crossFilter({ kind: 'category', category })}
                  style={{ background: 'none', border: 'none', color: '#66d9e8', cursor: 'pointer', padding: 0, fontSize: 12 }}
                >
                  {category}
                </button>
                <span style={{ color: '#8892a8' }}> · {count} findings — click to filter the issue explorer</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
