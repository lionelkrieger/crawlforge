/**
 * Rendered-comparison vocabulary helpers (WP-10-03, FS-CRAWL-001 §7.18 /
 * §5.6.1; UI-13 rendered tab + UI-16 rendered comparison).
 *
 * Every rule key is read from the LOCKED frontend contract mirror
 * (`contracts/api/vocabulary` FINDING_RULE_KEYS) — this module never invents
 * keys. It only groups the locked `render.*` family for the comparison
 * surfaces and provides display labels + the severity ordering used to sort
 * the specialist surface.
 *
 * Severity wire values mirror the Api lowercase enum convention frozen in the
 * issue explorer (`error` / `warning` / `notice` / `pass`). The §7.18
 * "critical" rendered-only noindex is carried as `error` severity PLUS
 * `evidence.details.critical === true` (WP-10-02 comparer); the critical flag
 * — not a separate severity token — drives the top-of-list ordering.
 *
 * @module surfaces/explorers/rendered/renderedKeys
 */

import { FINDING_RULE_KEYS } from '../../../contracts/api/vocabulary';

/**
 * The locked `render.*-delta` comparison family (WP-10-02 comparer output).
 * These are the response-vs-rendered difference findings the UI-16 surface
 * aggregates and the UI-13 diff panel reads. Order is display order.
 */
export const RENDER_DELTA_RULE_KEYS = [
  FINDING_RULE_KEYS.RenderTitleDelta,
  FINDING_RULE_KEYS.RenderCanonicalDelta,
  FINDING_RULE_KEYS.RenderMetaRobotsDelta,
  FINDING_RULE_KEYS.RenderNoindexDelta,
  FINDING_RULE_KEYS.RenderMetadataDelta,
  FINDING_RULE_KEYS.RenderStatusDelta,
  FINDING_RULE_KEYS.RenderTextDelta,
  FINDING_RULE_KEYS.RenderLinksDelta,
  FINDING_RULE_KEYS.RenderStructuredDataDelta,
  FINDING_RULE_KEYS.RenderResourcesDelta
] as const;

/**
 * Every locked `render.*` key the comparison surface queries (the delta family
 * plus the §7.19 service-worker informational finding). The surface issues one
 * findings query per key through the locked issue-explorer query surface
 * (`audits.listFindings` ruleKey filter) and merges the results.
 */
export const RENDER_COMPARISON_RULE_KEYS = [
  ...RENDER_DELTA_RULE_KEYS,
  FINDING_RULE_KEYS.RenderServiceWorkerRegistered
] as const;

/** True when a rule key belongs to the locked rendered-comparison family. */
export function isRenderComparisonRuleKey(ruleKey: string): boolean {
  return (RENDER_COMPARISON_RULE_KEYS as readonly string[]).includes(ruleKey);
}

/** True when a rule key is a response-vs-rendered *delta* (a real difference). */
export function isRenderDeltaRuleKey(ruleKey: string): boolean {
  return (RENDER_DELTA_RULE_KEYS as readonly string[]).includes(ruleKey);
}

/** Human label for a rendered delta field (the comparer `evidence.details.field`). */
export const RENDER_FIELD_LABELS: Readonly<Record<string, string>> = {
  title: 'Title',
  canonical: 'Canonical',
  'meta-robots': 'Meta robots',
  noindex: 'Noindex',
  hreflang: 'Hreflang',
  metadata: 'Metadata',
  directives: 'Directives',
  status: 'Status',
  text: 'Visible text',
  links: 'Links',
  'structured-data': 'Structured data',
  resources: 'Resources'
};

/** Display label for a delta field (falls back to the raw field name). */
export function renderFieldLabel(field: string): string {
  return RENDER_FIELD_LABELS[field] ?? field;
}

/** Locked finding severity wire values, most severe first. */
export const RENDER_SEVERITY_ORDER = ['error', 'warning', 'notice', 'pass'] as const;

/**
 * Numeric severity rank (lower = more severe). Unknown tokens sort last so a
 * future severity never silently outranks a known one. `critical`/`high` are
 * accepted as aliases of `error` for forward compatibility with the catalogue.
 */
export function severityRank(severity: string): number {
  const key = severity.toLowerCase();
  if (key === 'error' || key === 'critical' || key === 'high') return 0;
  if (key === 'warning' || key === 'medium') return 1;
  if (key === 'notice' || key === 'info' || key === 'low') return 2;
  if (key === 'pass') return 3;
  return 4;
}

/**
 * Sort rank for a comparison row: a critical rendered-only noindex sorts above
 * every non-critical finding (including other errors), then by severity.
 */
export function comparisonSortRank(severity: string, critical: boolean): number {
  return critical ? -1 : severityRank(severity);
}

/** Capture-mode display labels (locked wire tokens → human text). */
export const CAPTURE_MODE_LABELS: Readonly<Record<string, string>> = {
  'non-js': 'Non-JS (raw HTTP)',
  js: 'JavaScript rendered',
  dual: 'Dual (raw + rendered)',
  adaptive: 'Adaptive sampling'
};

/** Capture-source display labels (locked wire tokens → human text). */
export const CAPTURE_SOURCE_LABELS: Readonly<Record<string, string>> = {
  raw: 'Raw response',
  rendered: 'Rendered DOM',
  both: 'Raw + rendered'
};
