/**
 * Issue explorer surface barrel (WP-09-08). The shell mounts `IssueExplorer`
 * (root testid `issues-grid`) into the report window; the pure suppression
 * helpers are exported for the lead's SuppressionScore conformance suite.
 *
 * @module surfaces/explorers/issues
 */

export { IssueExplorer, SEVERITY_WIRE_KEYS, CATEGORY_WIRE_KEYS, type IssueExplorerProps } from './IssueExplorer';
export { FindingDetail, type FindingDetailProps } from './FindingDetail';
export {
  coversFinding,
  isFindingSuppressed,
  findCoveringSuppression,
  findingSuppression,
  buildSuppressionRequest,
  suppressionServerQuery,
  suppressionLabel,
  computeScoreDelta,
  formatDelta,
  upsertSuppression,
  type IssueStatusFilter,
  type ScoreDelta,
  type CategoryScoreDelta
} from './suppression';
