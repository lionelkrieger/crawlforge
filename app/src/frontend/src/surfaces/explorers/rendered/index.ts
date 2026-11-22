/**
 * Rendered surfaces barrel (WP-10-03, phase 10 wave 10B).
 *
 * UI-13 rendered tab (`RenderedTab` + viewers + dual-evidence diff), UI-16
 * rendered comparison specialist surface (`RenderedComparisonSurface`), the
 * real rendered output panel shared by UI-18/UI-19 (`RenderedOutputPanel`),
 * and the UI-10 capture-mode indicator (`CaptureModeIndicator`).
 *
 * Mounting / routes / nav entries / indicator placement are coordinator
 * wiring — see the WP-10-03 wiring handoff for exact call sites, props and
 * testids. Everything here codes against the locked frontend contracts.
 *
 * @module surfaces/explorers/rendered
 */

// Pure logic (wire-bound readers / aggregation).
export {
  RENDER_DELTA_RULE_KEYS,
  RENDER_COMPARISON_RULE_KEYS,
  RENDER_FIELD_LABELS,
  RENDER_SEVERITY_ORDER,
  CAPTURE_MODE_LABELS,
  CAPTURE_SOURCE_LABELS,
  isRenderComparisonRuleKey,
  isRenderDeltaRuleKey,
  renderFieldLabel,
  severityRank,
  comparisonSortRank
} from './renderedKeys';
export {
  extractRenderedDiff,
  fieldDiffFromFinding,
  noindexDiffFromFinding,
  wordCountDiffFromFinding,
  evidenceString,
  evidenceNumber,
  evidenceBoolean,
  type RenderedDiff,
  type RenderedFieldDiff,
  type RenderedNoindexDiff,
  type RenderedWordCountDiff
} from './renderedDiff';
export {
  buildRenderedComparisonRows,
  loadRenderedComparison,
  type RenderedComparisonRow,
  type RenderedComparisonResult
} from './renderedComparison';
export {
  EMPTY_RENDERED_ARTIFACTS,
  parseNetworkWaterfall,
  totalTransferredBytes,
  type RenderedUrlArtifacts,
  type RenderedArtifactLoader
} from './renderedArtifacts';

// Components.
export { RenderedTab, type RenderedTabProps } from './RenderedTab';
export { RenderedDiffPanel, type RenderedDiffPanelProps } from './RenderedDiffPanel';
export { RenderedComparisonSurface, type RenderedComparisonSurfaceProps } from './RenderedComparisonSurface';
export { RenderedOutputPanel, type RenderedOutputPanelProps } from './RenderedOutputPanel';
export { CaptureModeIndicator, type CaptureModeIndicatorProps } from './CaptureModeIndicator';
export { NetworkWaterfallViewer, formatBytes, type NetworkWaterfallViewerProps } from './NetworkWaterfallViewer';
export { ConsoleLogViewer, type ConsoleLogViewerProps } from './ConsoleLogViewer';
export { RenderedDomViewer, type RenderedDomViewerProps } from './RenderedDomViewer';
export { ScreenshotViewer, type ScreenshotViewerProps } from './ScreenshotViewer';
