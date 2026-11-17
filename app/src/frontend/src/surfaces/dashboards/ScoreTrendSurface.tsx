/**
 * Score trend surface (FS-CRAWL-001 §8.1/§8.2): cross-audit overall score
 * trend from pre-bucketed chart points plus per-category score bars with
 * the overall mark line. Aggregates only.
 *
 * @module surfaces/dashboards/ScoreTrendSurface
 */

import type { AuditScores, ChartPoint } from '../../contracts/api/audits';
import type { ChartRendererFactory } from '../../charts/chartPorts';
import { EChart } from '../../charts/EChart';
import { categoryScoresOption, scoreTrendOption } from '../../charts/chartTransforms';

/** Score trend surface props. */
export interface ScoreTrendSurfaceProps {
  readonly trend: readonly ChartPoint[] | null;
  readonly scores: AuditScores | null;
  readonly factory?: ChartRendererFactory;
}

/** Score trend + category score charts. */
export function ScoreTrendSurface({ trend, scores, factory }: ScoreTrendSurfaceProps) {
  return (
    <div data-testid="cf-score-surface" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      {trend && trend.length > 0 ? (
        <EChart
          factory={factory}
          option={scoreTrendOption(trend)}
          ariaLabel="Overall score trend across audits"
          exportName="score-trend"
          height={300}
        />
      ) : (
        <div data-testid="cf-trend-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
          No score history yet — complete at least one audit.
        </div>
      )}
      {scores ? (
        <EChart
          factory={factory}
          option={categoryScoresOption(scores)}
          ariaLabel="Category scores"
          exportName="category-scores"
          height={300}
        />
      ) : (
        <div data-testid="cf-scores-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
          Scores unavailable for this audit.
        </div>
      )}
    </div>
  );
}
