/**
 * Raw-vs-rendered diff reader (WP-10-03, UI-13 rendered tab; FS-CRAWL-001
 * §7.18 / CR-009).
 *
 * Pure function of the stored findings: it reads the DUAL evidence the
 * WP-10-02 `ResponseRenderedComparer` persists in `finding.evidence.details`
 * and projects the title / canonical / meta-robots / noindex / word-count
 * differences (plus every other delta field) into a presentational diff.
 *
 * Wire shapes bound here (verbatim from the comparer, never invented):
 *  - field deltas (title/canonical/meta-robots/metadata/status/…):
 *      `{ field, responseValue, renderedValue }`
 *  - `render.text-delta`:
 *      `{ field:'text', responseValue, renderedValue, rawWordCount,
 *         renderedWordCount, totalWordCount, words, wordCountDelta,
 *         wordCountChangeFraction }`
 *  - `render.noindex-delta` (critical, either direction):
 *      `{ field:'noindex', critical:true, direction:'rendered-only'|'raw-only',
 *         rawNoindex, renderedNoindex, rawDirectives, renderedDirectives,
 *         responseValue, renderedValue, … }`
 *
 * Re-audit reproduces these findings offline from stored artifacts (§9.6), so
 * the diff is stable across re-runs. No network access happens here.
 *
 * @module surfaces/explorers/rendered/renderedDiff
 */

import type { Finding } from '../../../contracts/api/audits';
import type { JsonValue } from '../../../contracts/common/primitives';
import { FINDING_RULE_KEYS } from '../../../contracts/api/vocabulary';
import { isRenderDeltaRuleKey, renderFieldLabel, severityRank } from './renderedKeys';

/** One response-vs-rendered field difference with its dual evidence. */
export interface RenderedFieldDiff {
  readonly findingId: string;
  readonly ruleKey: string;
  /** The comparer field name (`evidence.details.field`). */
  readonly field: string;
  readonly label: string;
  /** Wire severity (`error`/`warning`/`notice`/`pass`). */
  readonly severity: string;
  /** True for the critical rendered-only/raw-only noindex delta. */
  readonly critical: boolean;
  readonly responseValue: string | null;
  readonly renderedValue: string | null;
  readonly message: string;
  /** Full evidence details for the generic drill-down. */
  readonly details: Readonly<Record<string, JsonValue>>;
}

/** Word-count difference carried by `render.text-delta`. */
export interface RenderedWordCountDiff {
  readonly findingId: string;
  readonly severity: string;
  readonly rawWordCount: number | null;
  readonly renderedWordCount: number | null;
  readonly wordCountDelta: number | null;
  readonly wordCountChangeFraction: number | null;
}

/** Noindex difference carried by `render.noindex-delta` (dual evidence). */
export interface RenderedNoindexDiff {
  readonly findingId: string;
  readonly severity: string;
  readonly critical: boolean;
  /** `rendered-only` | `raw-only` (the comparer direction token). */
  readonly direction: string;
  readonly rawNoindex: boolean;
  readonly renderedNoindex: boolean;
  readonly rawDirectives: string | null;
  readonly renderedDirectives: string | null;
}

/** The projected raw-vs-rendered diff for one URL. */
export interface RenderedDiff {
  /** Every delta field, in comparer order (title first when present). */
  readonly fields: readonly RenderedFieldDiff[];
  readonly title: RenderedFieldDiff | null;
  readonly canonical: RenderedFieldDiff | null;
  readonly metaRobots: RenderedFieldDiff | null;
  readonly noindex: RenderedNoindexDiff | null;
  readonly wordCount: RenderedWordCountDiff | null;
  /** True when at least one response-vs-rendered delta exists. */
  readonly hasDifferences: boolean;
  /** Number of critical deltas (rendered-only/raw-only noindex). */
  readonly criticalCount: number;
  /** Most severe wire severity among the deltas, or null when none. */
  readonly highestSeverity: string | null;
}

/** Evidence details record (empty when absent). */
function detailsOf(finding: Finding): Readonly<Record<string, JsonValue>> {
  return finding.evidence?.details ?? {};
}

/** Reads a JSON value as a display string (null when absent/null). */
export function evidenceString(value: JsonValue | undefined): string | null {
  if (value === undefined || value === null) return null;
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return JSON.stringify(value);
}

/** Reads a JSON value as a finite number (null when absent/non-numeric). */
export function evidenceNumber(value: JsonValue | undefined): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
  return null;
}

/** Reads a JSON value as a boolean (false when absent/non-boolean). */
export function evidenceBoolean(value: JsonValue | undefined): boolean {
  return value === true;
}

/** Projects one delta finding into a field diff (null for non-delta keys). */
export function fieldDiffFromFinding(finding: Finding): RenderedFieldDiff | null {
  if (!isRenderDeltaRuleKey(finding.ruleKey)) return null;
  const details = detailsOf(finding);
  const field = evidenceString(details.field) ?? finding.ruleKey.replace(/^render\./, '').replace(/-delta$/, '');
  return {
    findingId: finding.id,
    ruleKey: finding.ruleKey,
    field,
    label: renderFieldLabel(field),
    severity: finding.severity,
    critical: evidenceBoolean(details.critical),
    responseValue: evidenceString(details.responseValue),
    renderedValue: evidenceString(details.renderedValue),
    message: finding.message,
    details
  };
}

/** Reads the noindex dual-evidence diff from a `render.noindex-delta` finding. */
export function noindexDiffFromFinding(finding: Finding): RenderedNoindexDiff | null {
  if (finding.ruleKey !== FINDING_RULE_KEYS.RenderNoindexDelta) return null;
  const details = detailsOf(finding);
  return {
    findingId: finding.id,
    severity: finding.severity,
    critical: evidenceBoolean(details.critical),
    direction: evidenceString(details.direction) ?? 'rendered-only',
    rawNoindex: evidenceBoolean(details.rawNoindex),
    renderedNoindex: evidenceBoolean(details.renderedNoindex),
    rawDirectives: evidenceString(details.rawDirectives),
    renderedDirectives: evidenceString(details.renderedDirectives)
  };
}

/** Reads the word-count diff from a `render.text-delta` finding. */
export function wordCountDiffFromFinding(finding: Finding): RenderedWordCountDiff | null {
  if (finding.ruleKey !== FINDING_RULE_KEYS.RenderTextDelta) return null;
  const details = detailsOf(finding);
  const rawWordCount = evidenceNumber(details.rawWordCount);
  // The comparer carries the rendered count under renderedWordCount, with
  // totalWordCount/words as the canonical rendered aliases (§7.18 evidence).
  const renderedWordCount =
    evidenceNumber(details.renderedWordCount) ?? evidenceNumber(details.totalWordCount) ?? evidenceNumber(details.words);
  return {
    findingId: finding.id,
    severity: finding.severity,
    rawWordCount,
    renderedWordCount,
    wordCountDelta: evidenceNumber(details.wordCountDelta),
    wordCountChangeFraction: evidenceNumber(details.wordCountChangeFraction)
  };
}

/**
 * Extracts the raw-vs-rendered diff from a URL's findings. Only locked
 * `render.*-delta` keys contribute; the service-worker informational finding
 * is intentionally excluded (it is not a response-vs-rendered difference).
 */
export function extractRenderedDiff(findings: readonly Finding[]): RenderedDiff {
  const fields: RenderedFieldDiff[] = [];
  let title: RenderedFieldDiff | null = null;
  let canonical: RenderedFieldDiff | null = null;
  let metaRobots: RenderedFieldDiff | null = null;
  let noindex: RenderedNoindexDiff | null = null;
  let wordCount: RenderedWordCountDiff | null = null;
  let criticalCount = 0;
  let highestSeverity: string | null = null;

  for (const finding of findings) {
    const fieldDiff = fieldDiffFromFinding(finding);
    if (!fieldDiff) continue;
    fields.push(fieldDiff);
    if (fieldDiff.critical) criticalCount += 1;
    if (highestSeverity === null || severityRank(finding.severity) < severityRank(highestSeverity)) {
      highestSeverity = finding.severity;
    }
    switch (finding.ruleKey) {
      case FINDING_RULE_KEYS.RenderTitleDelta:
        title = fieldDiff;
        break;
      case FINDING_RULE_KEYS.RenderCanonicalDelta:
        canonical = fieldDiff;
        break;
      case FINDING_RULE_KEYS.RenderMetaRobotsDelta:
        metaRobots = fieldDiff;
        break;
      case FINDING_RULE_KEYS.RenderNoindexDelta:
        noindex = noindexDiffFromFinding(finding);
        break;
      case FINDING_RULE_KEYS.RenderTextDelta:
        wordCount = wordCountDiffFromFinding(finding);
        break;
      default:
        break;
    }
  }

  return {
    fields,
    title,
    canonical,
    metaRobots,
    noindex,
    wordCount,
    hasDifferences: fields.length > 0,
    criticalCount,
    highestSeverity
  };
}
