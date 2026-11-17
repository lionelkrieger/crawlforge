/**
 * Ephemeral live-telemetry store (latest-wins, bounded). The locked
 * CrawlForgeState carries normalized entities and windows; this auxiliary
 * store holds the additive live counters the hub streams (throughput, ETA,
 * WAF aggregates, matrix/embedding/log-import progress) WITHOUT raw event
 * history (FS-CRAWL-002 §7.4: counters aggregated, never raw retention).
 *
 * Not part of the plugin relay surface (plugins read locked slices only).
 *
 * @module store/liveProgress
 */

import { createStore } from 'zustand/vanilla';
import { useStore } from 'zustand';
import type {
  CrawlProgressPayload,
  EmbeddingProgressPayload,
  LogImportProgressPayload,
  BackendLifecyclePayload
} from '../contracts/hub/events';
import type { EmbeddingFinalCounts, EmbeddingPassSummary } from '../contracts/hub/events';

/** Maximum audits tracked simultaneously (bounded map, LRU-ish cap). */
export const MAX_LIVE_AUDITS = 32;
/** Maximum concurrent log imports tracked. */
export const MAX_LIVE_IMPORTS = 16;

/** Aggregated URL status-class counts for one audit (no raw rows). */
export interface UrlClassCounts {
  readonly '1xx': number;
  readonly '2xx': number;
  readonly '3xx': number;
  readonly '4xx': number;
  readonly '5xx': number;
}

/** Aggregated finding deltas for one audit (severity/category totals). */
export interface FindingDeltas {
  readonly severity: Readonly<Record<string, number>>;
  readonly category: Readonly<Record<string, number>>;
}

/** Aggregated WAF block counts per provider for one audit. */
export type WafProviderCounts = Readonly<Record<string, number>>;

/** Matrix validation progress (aggregated row counts). */
export interface MatrixProgress {
  readonly validated: number;
  readonly total: number | null;
}

/** Embedding job live state (progress or final). */
export interface EmbeddingLiveState {
  readonly embeddedCount: number;
  readonly totalEligible: number;
  readonly provider: string;
  readonly phase: string;
  readonly isFinal: boolean;
  readonly skipped?: number;
  readonly passes?: EmbeddingPassSummary;
}

/** Log import live state (latest-wins). */
export interface LogImportLiveState {
  readonly phase: string;
  readonly linesParsed: number;
  readonly linesAccepted: number;
  readonly linesRejected: number;
  readonly isFinal: boolean;
}

/** The complete live-telemetry shape. */
export interface LiveProgressState {
  readonly crawlByAudit: Readonly<Record<string, CrawlProgressPayload>>;
  readonly urlClassByAudit: Readonly<Record<string, UrlClassCounts>>;
  readonly findingDeltasByAudit: Readonly<Record<string, FindingDeltas>>;
  readonly wafCountsByAudit: Readonly<Record<string, WafProviderCounts>>;
  readonly matrixByAudit: Readonly<Record<string, MatrixProgress>>;
  readonly embeddingByAudit: Readonly<Record<string, EmbeddingLiveState>>;
  readonly logImportById: Readonly<Record<string, LogImportLiveState>>;
  readonly backendLifecycle: BackendLifecyclePayload | null;
}

/** Creates the initial live-telemetry state. */
export function createInitialLiveState(): LiveProgressState {
  return {
    crawlByAudit: {},
    urlClassByAudit: {},
    findingDeltasByAudit: {},
    wafCountsByAudit: {},
    matrixByAudit: {},
    embeddingByAudit: {},
    logImportById: {},
    backendLifecycle: null
  };
}

/** Live-telemetry store type. */
export type LiveProgressStore = ReturnType<typeof createLiveProgressStore>;

/** Creates the vanilla live-telemetry store. */
export function createLiveProgressStore() {
  return createStore<LiveProgressState>()(() => createInitialLiveState());
}

/** React hook bound to a live store. */
export function useLiveProgress<T>(
  store: LiveProgressStore,
  selector: (state: LiveProgressState) => T
): T {
  return useStore(store, selector);
}

/** Caps a record to its most recent `max` keys (insertion order). */
function capRecord<T>(record: Readonly<Record<string, T>>, max: number): Record<string, T> {
  const keys = Object.keys(record);
  if (keys.length <= max) return { ...record };
  const capped: Record<string, T> = {};
  for (const key of keys.slice(keys.length - max)) capped[key] = record[key];
  return capped;
}

/** Inserts/replaces one audit-keyed entry under the bounded cap. */
function putBounded<T>(
  record: Readonly<Record<string, T>>,
  key: string,
  value: T,
  max: number
): Record<string, T> {
  const next: Record<string, T> = { ...record };
  delete next[key];
  next[key] = value;
  return capRecord(next, max);
}

/** Latest-wins crawl progress (counters replace; no history). */
export function ingestCrawlProgress(store: LiveProgressStore, payload: CrawlProgressPayload): void {
  store.setState((state) => ({
    crawlByAudit: putBounded(state.crawlByAudit, payload.auditId, payload, MAX_LIVE_AUDITS),
    matrixByAudit:
      payload.matrixValidated !== undefined
        ? putBounded(
            state.matrixByAudit,
            payload.auditId,
            { validated: payload.matrixValidated, total: payload.matrixTotal ?? null },
            MAX_LIVE_AUDITS
          )
        : state.matrixByAudit
  }));
}

/** Aggregates URL status-class counts (no raw row retention). */
export function ingestUrlBatchClasses(
  store: LiveProgressStore,
  auditId: string,
  classes: readonly string[]
): void {
  if (classes.length === 0) return;
  store.setState((state) => {
    const current = state.urlClassByAudit[auditId] ?? { '1xx': 0, '2xx': 0, '3xx': 0, '4xx': 0, '5xx': 0 };
    const next = { ...current };
    for (const cls of classes) {
      if (cls === '1xx' || cls === '2xx' || cls === '3xx' || cls === '4xx' || cls === '5xx') {
        next[cls] += 1;
      }
    }
    return { urlClassByAudit: putBounded(state.urlClassByAudit, auditId, next, MAX_LIVE_AUDITS) };
  });
}

/** Aggregates finding severity/category deltas into running totals. */
export function ingestFindingDeltas(
  store: LiveProgressStore,
  auditId: string,
  severityDeltas: Readonly<Record<string, number>>,
  categoryDeltas: Readonly<Record<string, number>>
): void {
  store.setState((state) => {
    const current = state.findingDeltasByAudit[auditId] ?? { severity: {}, category: {} };
    const severity = { ...current.severity };
    const category = { ...current.category };
    for (const [key, delta] of Object.entries(severityDeltas)) {
      severity[key] = (severity[key] ?? 0) + delta;
    }
    for (const [key, delta] of Object.entries(categoryDeltas)) {
      category[key] = (category[key] ?? 0) + delta;
    }
    return {
      findingDeltasByAudit: putBounded(state.findingDeltasByAudit, auditId, { severity, category }, MAX_LIVE_AUDITS)
    };
  });
}

/** Aggregates WAF block counts per provider. */
export function ingestWafBlocks(
  store: LiveProgressStore,
  auditId: string,
  providers: readonly string[]
): void {
  if (providers.length === 0) return;
  store.setState((state) => {
    const current = { ...(state.wafCountsByAudit[auditId] ?? {}) };
    for (const provider of providers) {
      current[provider] = (current[provider] ?? 0) + 1;
    }
    return { wafCountsByAudit: putBounded(state.wafCountsByAudit, auditId, current, MAX_LIVE_AUDITS) };
  });
}

/** Adds validated matrix rows (aggregate count). */
export function ingestMatrixRows(store: LiveProgressStore, auditId: string, rowCount: number): void {
  store.setState((state) => {
    const current = state.matrixByAudit[auditId] ?? { validated: 0, total: null };
    return {
      matrixByAudit: putBounded(
        state.matrixByAudit,
        auditId,
        { validated: current.validated + rowCount, total: current.total },
        MAX_LIVE_AUDITS
      )
    };
  });
}

/** Latest-wins embedding progress. */
export function ingestEmbeddingProgress(store: LiveProgressStore, payload: EmbeddingProgressPayload): void {
  store.setState((state) => ({
    embeddingByAudit: putBounded(
      state.embeddingByAudit,
      payload.auditId,
      {
        embeddedCount: payload.embeddedCount,
        totalEligible: payload.totalEligible,
        provider: payload.provider,
        phase: payload.phase,
        isFinal: false
      },
      MAX_LIVE_AUDITS
    )
  }));
}

/** Final embedding counts (terminal state). */
export function ingestEmbeddingCompleted(
  store: LiveProgressStore,
  auditId: string,
  finalCounts: EmbeddingFinalCounts,
  passes: EmbeddingPassSummary
): void {
  store.setState((state) => {
    const current = state.embeddingByAudit[auditId];
    return {
      embeddingByAudit: putBounded(
        state.embeddingByAudit,
        auditId,
        {
          embeddedCount: finalCounts.embedded,
          totalEligible: finalCounts.totalEligible,
          provider: current?.provider ?? 'unknown',
          phase: 'completed',
          isFinal: true,
          skipped: finalCounts.skipped,
          passes
        },
        MAX_LIVE_AUDITS
      )
    };
  });
}

/** Latest-wins log-import progress (accepts flat or nested counters). */
export function ingestLogImportProgress(
  store: LiveProgressStore,
  payload: LogImportProgressPayload & {
    linesParsed?: number;
    linesAccepted?: number;
    linesRejected?: number;
  }
): void {
  const counters = payload.counters as
    | { linesParsed?: number; linesAccepted?: number; linesRejected?: number }
    | undefined;
  store.setState((state) => ({
    logImportById: putBounded(
      state.logImportById,
      payload.importId,
      {
        phase: payload.phase,
        linesParsed: counters?.linesParsed ?? payload.linesParsed ?? 0,
        linesAccepted: counters?.linesAccepted ?? payload.linesAccepted ?? 0,
        linesRejected: counters?.linesRejected ?? payload.linesRejected ?? 0,
        isFinal: false
      },
      MAX_LIVE_IMPORTS
    )
  }));
}

/** Final log-import state. */
export function ingestLogImportCompleted(
  store: LiveProgressStore,
  importId: string,
  finalCounters: { linesParsed?: number; linesAccepted?: number; linesRejected?: number }
): void {
  store.setState((state) => {
    const current = state.logImportById[importId];
    return {
      logImportById: putBounded(
        state.logImportById,
        importId,
        {
          phase: 'completed',
          linesParsed: finalCounters.linesParsed ?? current?.linesParsed ?? 0,
          linesAccepted: finalCounters.linesAccepted ?? current?.linesAccepted ?? 0,
          linesRejected: finalCounters.linesRejected ?? current?.linesRejected ?? 0,
          isFinal: true
        },
        MAX_LIVE_IMPORTS
      )
    };
  });
}

/** Backend lifecycle notice (latest-wins). */
export function ingestBackendLifecycle(store: LiveProgressStore, payload: BackendLifecyclePayload): void {
  store.setState({ backendLifecycle: payload });
}
