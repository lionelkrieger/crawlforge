/**
 * Dashboards surface barrel (WP-06-04). Charts and graphs areas are
 * imported directly by consumers; this barrel re-exports the composed
 * dashboard surfaces for the shell wiring workpiece.
 *
 * @module surfaces/dashboards
 */

export { AuditDashboard, type AuditDashboardProps } from './AuditDashboard';
export { CrawlHealthPanel, type CrawlHealthPanelProps } from './CrawlHealthPanel';
export { ReportDashboard, type ReportDashboardProps } from './ReportDashboard';
export { ScoreTrendSurface, type ScoreTrendSurfaceProps } from './ScoreTrendSurface';
export {
  NgramTopicalPanel,
  LogIntelligencePanel,
  GeoCoveragePanel,
  AnalyticsCoveragePanel,
  type NgramTopicalPanelProps,
  type LogIntelligencePanelProps,
  type GeoCoveragePanelProps,
  type AnalyticsCoveragePanelProps
} from './IntelligenceDashboards';
export { VisualizationSurface, type VisualizationSurfaceProps } from './VisualizationSurface';
export {
  loadDashboardAggregates,
  loadVisualizationAggregates,
  useDashboardAggregates,
  useVisualizationAggregates,
  EMPTY_AGGREGATES,
  type DashboardAggregates,
  type VisualizationAggregates,
  type AggregatesState
} from './dashboardData';
