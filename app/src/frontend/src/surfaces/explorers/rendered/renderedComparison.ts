/**
 * Rendered comparison aggregation (WP-10-03, UI-16 specialist surface;
 * FS-CRAWL-001 §7.18 / §8.2 "Rendered comparison").
 *
 * Lists every page with a response-vs-rendered difference, severity-sorted.
 * The data is queried through the LOCKED issue-explorer query surface
 * (`audits.listFindings` with the `ruleKey` filter) — one query per locked
 * `render.*` key — then merged and grouped per URL. URL identity / capture
 * source come from the locked `audits.listUrls` route. No contract is edited
 * and no new route is required.
 *
 * Severity ordering (§7.18 / CR-009): a critical rendered-only/raw-only noindex
 * sorts first, then `error` (title/canonical/meta-robots/hreflang), then
 * `warning` (metadata/status), then `notice` (text/links/structured-data/
 * resources). Ties break on canonical URL for a stable order.
 *
 * @module surfaces/explorers/rendered/renderedComparison
 */

import type { Finding, UrlRecordSummary } from '../../../contracts/api/audits';
import type { LoopbackApiClient } from '../../../contracts/api/client';
import type { ApiEnvelope } from '../../../contracts/common/envelope';
import type { CaptureSourceWire } from '../../../contracts/api/vocabulary';
import {
  RENDER_COMPARISON_RULE_KEYS,
  comparisonSortRank,
  isRenderDeltaRuleKey,
  renderFieldLabel,
  severityRank
} from './renderedKeys';
import { evidenceString, fieldDiffFromFinding } from './renderedDiff';

/** One page row of the rendered comparison surface. */
export interface RenderedComparisonRow {
  readonly urlId: string;
  readonly canonicalUrl: string;
  readonly title: string | null;
  /** Which capture produced the analysed document (null = baseline raw). */
  readonly captureSource: CaptureSourceWire | null;
  /** Most severe wire severity among the page's delta findings. */
  readonly severity: string;
  readonly severityRank: number;
  /** Critical-aware sort rank (lower sorts first). */
  readonly sortRank: number;
  /** True when the page carries a critical noindex delta. */
  readonly critical: boolean;
  /** Display labels of the changed fields (title/canonical/noindex/…). */
  readonly changedFields: readonly string[];
  readonly findingCount: number;
  /** Every render.* finding for the page (drill-down evidence). */
  readonly findings: readonly Finding[];
}

/** Result of loading the comparison surface. */
export interface RenderedComparisonResult {
  readonly rows: readonly RenderedComparisonRow[];
  /** Total render.* findings merged (before grouping). */
  readonly findingCount: number;
  /** Envelope/transport error message, or null on success. */
  readonly error: string | null;
}

/** Page size used to drain the locked list routes. */
const DRAIN_PAGE_SIZE = 200;
/** Safety bound on pagination loops (500k URLs / 200 = 2500 pages max). */
const MAX_PAGES = 5000;

/**
 * Groups render.* findings into severity-sorted per-URL comparison rows.
 * A row exists only when the page has at least one response-vs-rendered DELTA
 * (the service-worker informational finding alone does not create a row).
 */
export function buildRenderedComparisonRows(
  findings: readonly Finding[],
  urlRecords: readonly UrlRecordSummary[]
): RenderedComparisonRow[] {
  const recordById = new Map<string, UrlRecordSummary>();
  for (const record of urlRecords) recordById.set(record.id, record);

  const byUrl = new Map<string, Finding[]>();
  for (const finding of findings) {
    if (!finding.urlId) continue;
    const bucket = byUrl.get(finding.urlId);
    if (bucket) bucket.push(finding);
    else byUrl.set(finding.urlId, [finding]);
  }

  const rows: RenderedComparisonRow[] = [];
  for (const [urlId, pageFindings] of byUrl) {
    const deltas = pageFindings.filter((finding) => isRenderDeltaRuleKey(finding.ruleKey));
    if (deltas.length === 0) continue; // no response-vs-rendered difference

    const record = recordById.get(urlId);
    let severity = 'pass';
    let critical = false;
    const changedFields: string[] = [];
    const seenFields = new Set<string>();
    for (const finding of deltas) {
      if (severityRank(finding.severity) < severityRank(severity)) severity = finding.severity;
      const diff = fieldDiffFromFinding(finding);
      if (diff?.critical) critical = true;
      const field = diff?.field ?? evidenceString(finding.evidence?.details?.field) ?? finding.ruleKey;
      if (!seenFields.has(field)) {
        seenFields.add(field);
        changedFields.push(renderFieldLabel(field));
      }
    }

    rows.push({
      urlId,
      canonicalUrl: record?.canonicalUrl ?? urlId,
      title: record?.title ?? null,
      captureSource: record?.captureSource ?? null,
      severity,
      severityRank: severityRank(severity),
      sortRank: comparisonSortRank(severity, critical),
      critical,
      changedFields,
      findingCount: pageFindings.length,
      findings: pageFindings
    });
  }

  rows.sort((a, b) => a.sortRank - b.sortRank || a.canonicalUrl.localeCompare(b.canonicalUrl));
  return rows;
}

/** Drains a locked list route to completion (follows envelope pagination). */
async function drainList<TRow>(
  fetchPage: (offset: number, limit: number) => Promise<ApiEnvelope<readonly TRow[]>>,
  signal?: AbortSignal
): Promise<{ rows: TRow[]; error: string | null }> {
  const rows: TRow[] = [];
  let offset = 0;
  for (let page = 0; page < MAX_PAGES; page += 1) {
    if (signal?.aborted) return { rows, error: null };
    let envelope: ApiEnvelope<readonly TRow[]>;
    try {
      envelope = await fetchPage(offset, DRAIN_PAGE_SIZE);
    } catch (error) {
      return { rows, error: error instanceof Error ? error.message : 'Backend unreachable' };
    }
    if (envelope.error) return { rows, error: envelope.error.message };
    const batch = envelope.data ?? [];
    rows.push(...batch);
    const total = envelope.pagination?.total;
    offset += batch.length;
    if (batch.length === 0) break;
    if (typeof total === 'number' && offset >= total) break;
    if (batch.length < DRAIN_PAGE_SIZE && total === undefined) break;
  }
  return { rows, error: null };
}

/**
 * Loads the rendered comparison surface through the locked query surface:
 * every URL record plus every render.* finding (one findings query per locked
 * key), merged into severity-sorted per-URL rows. Offline-first — a null API
 * or any envelope error degrades to an honest empty result with a message.
 */
export async function loadRenderedComparison(
  api: LoopbackApiClient | null,
  auditId: string | null,
  signal?: AbortSignal
): Promise<RenderedComparisonResult> {
  if (!api || !auditId) {
    return { rows: [], findingCount: 0, error: api ? null : 'Backend disconnected — rendered comparison is unavailable offline.' };
  }

  const urls = await drainList<UrlRecordSummary>(
    (offset, limit) =>
      api.request('audits.listUrls', { params: { id: auditId }, query: { offset, limit }, ...(signal ? { signal } : {}) }),
    signal
  );
  if (urls.error) return { rows: [], findingCount: 0, error: urls.error };

  const findings: Finding[] = [];
  for (const ruleKey of RENDER_COMPARISON_RULE_KEYS) {
    const batch = await drainList<Finding>(
      (offset, limit) =>
        api.request('audits.listFindings', {
          params: { id: auditId },
          query: { ruleKey, offset, limit },
          ...(signal ? { signal } : {})
        }),
      signal
    );
    if (batch.error) return { rows: [], findingCount: 0, error: batch.error };
    findings.push(...batch.rows);
  }

  const rows = buildRenderedComparisonRows(findings, urls.rows);
  return { rows, findingCount: findings.length, error: null };
}
