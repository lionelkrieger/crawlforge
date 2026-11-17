/**
 * Intelligence dashboards (FS-CRAWL-002 §5.7, §11.9, §13.6, §14.6):
 * n-gram topical views (top terms, zone treemap, gaps), log intelligence
 * (correlation, bot families, hit timeline), GEO coverage (llms.txt +
 * advisory governance), analytics coverage (container treemap + consent).
 * All charts render from precomputed aggregates; zone clicks cross-filter
 * the n-gram/link explorers.
 *
 * @module surfaces/dashboards/IntelligenceDashboards
 */

import type { ChartPoint } from '../../contracts/api/audits';
import type { NgramRow, TopicalGap } from '../../contracts/api/ngrams';
import type { LogCorrelationSummary } from '../../contracts/api/logs';
import type { LlmsTxtValidation, BotGovernanceRow } from '../../contracts/api/geo';
import type { AnalyticsConsentSummary, ContainerCoverage } from '../../contracts/api/analytics';
import type { ChartRendererFactory } from '../../charts/chartPorts';
import type { ChartSelection } from '../../charts/crossFilter';
import { EChart } from '../../charts/EChart';
import {
  analyticsCoverageOption,
  botFamilyOption,
  consentCoverageOption,
  geoLlmsCoverageOption,
  governanceCoverageOption,
  logCorrelationOption,
  logHitTimelineOption,
  ngramTopOption,
  ngramZoneTreemapOption,
  topicalGapOption
} from '../../charts/chartTransforms';

/** N-gram topical panel props. */
export interface NgramTopicalPanelProps {
  readonly rows: readonly NgramRow[] | null;
  readonly gaps: readonly TopicalGap[] | null;
  readonly factory?: ChartRendererFactory;
  readonly onCrossFilter?: (selection: ChartSelection) => void;
}

/** Top n-grams + zone treemap + topical gaps. */
export function NgramTopicalPanel({ rows, gaps, factory, onCrossFilter }: NgramTopicalPanelProps) {
  if (!rows || rows.length === 0) {
    return (
      <div data-testid="cf-ngrams-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No n-gram aggregates — the analyzer produces them during the audit pipeline.
      </div>
    );
  }
  const zones = [...new Set(rows.map((row) => row.zone))].sort();
  return (
    <div data-testid="cf-ngram-panel" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      <EChart factory={factory} option={ngramTopOption(rows)} ariaLabel="Top n-grams" exportName="top-ngrams" height={320} />
      <EChart
        factory={factory}
        option={ngramZoneTreemapOption(rows)}
        ariaLabel="N-gram mass by DOM zone"
        exportName="ngram-zones"
        height={320}
        onChartClick={(params) => {
          if (zones.includes(params.name)) onCrossFilter?.({ kind: 'ngram-zone', zone: params.name });
        }}
      />
      {gaps && gaps.length > 0 ? (
        <EChart factory={factory} option={topicalGapOption(gaps)} ariaLabel="Topical gaps" exportName="topical-gaps" height={320} />
      ) : null}
    </div>
  );
}

/** Log intelligence panel props. */
export interface LogIntelligencePanelProps {
  readonly correlation: LogCorrelationSummary | null;
  readonly botCounts: Readonly<Record<string, number>> | null;
  readonly timeline: readonly ChartPoint[] | null;
  readonly factory?: ChartRendererFactory;
}

/** Correlation + bot families + hit timeline (aggregated counters only). */
export function LogIntelligencePanel({ correlation, botCounts, timeline, factory }: LogIntelligencePanelProps) {
  if (!correlation && !botCounts && (!timeline || timeline.length === 0)) {
    return (
      <div data-testid="cf-logs-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No log intelligence yet — import a server log to correlate bot hits with crawl coverage.
      </div>
    );
  }
  return (
    <div data-testid="cf-log-panel" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      {correlation ? (
        <EChart factory={factory} option={logCorrelationOption(correlation)} ariaLabel="Log correlation summary" exportName="log-correlation" height={300} />
      ) : null}
      {botCounts && Object.keys(botCounts).length > 0 ? (
        <EChart factory={factory} option={botFamilyOption(botCounts)} ariaLabel="Bot family breakdown" exportName="bot-families" height={300} />
      ) : null}
      {timeline && timeline.length > 0 ? (
        <EChart factory={factory} option={logHitTimelineOption(timeline)} ariaLabel="Bot hit timeline" exportName="log-hit-timeline" height={300} />
      ) : null}
    </div>
  );
}

/** GEO coverage panel props. */
export interface GeoCoveragePanelProps {
  readonly llms: readonly LlmsTxtValidation[] | null;
  readonly governance: readonly BotGovernanceRow[] | null;
  readonly factory?: ChartRendererFactory;
}

/** llms.txt structural coverage + advisory bot-governance verdicts. */
export function GeoCoveragePanel({ llms, governance, factory }: GeoCoveragePanelProps) {
  if ((!llms || llms.length === 0) && (!governance || governance.length === 0)) {
    return (
      <div data-testid="cf-geo-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No GEO aggregates — llms.txt discovery and the governance matrix run during the audit.
      </div>
    );
  }
  return (
    <div data-testid="cf-geo-panel" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      {llms && llms.length > 0 ? (
        <EChart factory={factory} option={geoLlmsCoverageOption(llms)} ariaLabel="llms.txt coverage per host" exportName="llms-coverage" height={300} />
      ) : null}
      {governance && governance.length > 0 ? (
        <EChart factory={factory} option={governanceCoverageOption(governance)} ariaLabel="AI-bot governance verdicts" exportName="governance-coverage" height={300} />
      ) : null}
    </div>
  );
}

/** Analytics coverage panel props. */
export interface AnalyticsCoveragePanelProps {
  readonly coverage: readonly ContainerCoverage[] | null;
  readonly consent: AnalyticsConsentSummary | null;
  readonly factory?: ChartRendererFactory;
}

/** Container coverage treemap + consent coverage donut. */
export function AnalyticsCoveragePanel({ coverage, consent, factory }: AnalyticsCoveragePanelProps) {
  if ((!coverage || coverage.length === 0) && !consent) {
    return (
      <div data-testid="cf-analytics-empty" style={{ padding: 16, color: '#8892a8', fontSize: 13 }}>
        No analytics aggregates — tag detection runs during the audit pipeline.
      </div>
    );
  }
  return (
    <div data-testid="cf-analytics-panel" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 12 }}>
      {coverage && coverage.length > 0 ? (
        <EChart factory={factory} option={analyticsCoverageOption(coverage)} ariaLabel="Analytics container coverage" exportName="analytics-coverage" height={320} />
      ) : null}
      {consent ? (
        <EChart factory={factory} option={consentCoverageOption(consent)} ariaLabel="Consent coverage" exportName="consent-coverage" height={320} />
      ) : null}
    </div>
  );
}
