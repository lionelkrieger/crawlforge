/**
 * Rendered artifact access surface (WP-10-03, UI-13 rendered tab).
 *
 * The crawl persists per-URL rendered artifacts under the locked
 * `ContentArtifactKind` values `RenderedDom`, `Screenshot` and
 * `NetworkWaterfall` (WP-10-01 / WP-10-02). The frozen REST route catalog
 * (`contracts/api/routes`) exposes NO per-URL content-artifact read route at
 * this amendment, so the rendered tab binds artifact loading through this
 * injected loader interface. The coordinator wires the loader to the artifact
 * endpoint when it lands (wiring handoff); until then the tab renders honest
 * "not captured" states for the artifact viewers while the raw-vs-rendered
 * DIFF still works from stored finding evidence (which IS reachable today).
 *
 * The waterfall reader is tolerant of the persisted JSON shapes (a bare entry
 * array, or a `{ networkWaterfall | entries: [...] }` wrapper, or a JSON
 * string), mirroring the WP-10-05 `RenderedImageSizeReader` tolerance.
 *
 * @module surfaces/explorers/rendered/renderedArtifacts
 */

import type { ConsoleMessage, RenderedNetworkEntry } from '../../../contracts/api/tools';

/** Per-URL rendered artifacts the tab can display (all optional/null). */
export interface RenderedUrlArtifacts {
  /** Post-render serialized DOM (ContentArtifactKind.RenderedDom). */
  readonly renderedDom?: string | null;
  /** Base64 PNG screenshot (ContentArtifactKind.Screenshot). */
  readonly screenshotBase64?: string | null;
  /** Console messages captured during rendering. */
  readonly consoleMessages?: readonly ConsoleMessage[] | null;
  /** Network waterfall (ContentArtifactKind.NetworkWaterfall). */
  readonly networkWaterfall?: readonly RenderedNetworkEntry[] | null;
}

/**
 * Loads the rendered artifacts for one URL. Returns null when nothing was
 * captured (baseline non-JS capture) or the artifact surface is unavailable.
 * The coordinator supplies the implementation bound to the artifact endpoint.
 */
export type RenderedArtifactLoader = (
  auditId: string,
  urlId: string,
  signal?: AbortSignal
) => Promise<RenderedUrlArtifacts | null>;

/** Honest empty artifact set (baseline render / nothing captured). */
export const EMPTY_RENDERED_ARTIFACTS: RenderedUrlArtifacts = {
  renderedDom: null,
  screenshotBase64: null,
  consoleMessages: null,
  networkWaterfall: null
};

/** Normalizes one unknown entry into a RenderedNetworkEntry (or null). */
function toEntry(value: unknown): RenderedNetworkEntry | null {
  if (typeof value !== 'object' || value === null) return null;
  const record = value as Record<string, unknown>;
  if (typeof record.url !== 'string') return null;
  const num = (key: string): number | undefined =>
    typeof record[key] === 'number' && Number.isFinite(record[key] as number) ? (record[key] as number) : undefined;
  const str = (key: string): string | null => (typeof record[key] === 'string' ? (record[key] as string) : null);
  const bool = (key: string): boolean => record[key] === true;
  // Locked shape: url/resourceType/method/statusCode are required (statusCode
  // 0 = no HTTP response, e.g. cache/data URI); the rest are optional.
  return {
    url: record.url,
    resourceType: str('resourceType') ?? '',
    method: str('method') ?? 'GET',
    statusCode: num('statusCode') ?? 0,
    mimeType: str('mimeType'),
    transferredBytes: num('transferredBytes'),
    encodedDataLength: num('encodedDataLength'),
    timingJson: str('timingJson'),
    fromCache: bool('fromCache'),
    fromServiceWorker: bool('fromServiceWorker')
  };
}

/**
 * Parses a persisted network-waterfall artifact into locked entries. Accepts a
 * bare array, a `{ networkWaterfall | entries }` wrapper, or a JSON string of
 * either. Returns null when no usable entries exist (never fabricates rows).
 */
export function parseNetworkWaterfall(value: unknown): RenderedNetworkEntry[] | null {
  let candidate: unknown = value;
  if (typeof candidate === 'string') {
    const trimmed = candidate.trim();
    if (trimmed === '') return null;
    try {
      candidate = JSON.parse(trimmed);
    } catch {
      return null;
    }
  }
  if (candidate === null || candidate === undefined) return null;
  if (!Array.isArray(candidate) && typeof candidate === 'object') {
    const wrapper = candidate as Record<string, unknown>;
    candidate = wrapper.networkWaterfall ?? wrapper.entries ?? null;
  }
  if (!Array.isArray(candidate)) return null;
  const entries: RenderedNetworkEntry[] = [];
  for (const item of candidate) {
    const entry = toEntry(item);
    if (entry) entries.push(entry);
  }
  return entries.length > 0 ? entries : null;
}

/** Total transferred bytes across a waterfall (0 for null/empty). */
export function totalTransferredBytes(entries: readonly RenderedNetworkEntry[] | null | undefined): number {
  if (!entries) return 0;
  let total = 0;
  for (const entry of entries) {
    if (typeof entry.transferredBytes === 'number' && Number.isFinite(entry.transferredBytes)) {
      total += entry.transferredBytes;
    }
  }
  return total;
}
