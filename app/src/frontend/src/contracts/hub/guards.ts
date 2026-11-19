/**
 * Runtime type guards for hub events. These narrow `unknown` wire values
 * into the discriminated unions of contracts/hub/events without any
 * `any`-typed escape hatches. Runtime code in the contracts package is
 * limited to type guards such as these.
 *
 * @module contracts/hub/guards
 */

import {
  isArrayOf,
  isBoolean,
  isNumber,
  isNumberRecord,
  isRecord,
  isString
} from '../common/guards';
import type { AuditCounters, AuditState, HttpStatusClass } from '../api/audits';
import type { CompletionActionResult, ScheduleRunOutcome, ScheduleTriggerType } from '../api/schedules';
import type { ExportDeliveryState, ExportJobState } from '../api/exports';
import type { LogCorrelationSummary, LogImportCounters } from '../api/logs';
import type { EmbeddingProvider } from '../api/semantics';
import type { SystemHealthSnapshot } from '../api/system';
import type {
  AuditCompletedPayload,
  AuditFailedPayload,
  BackendLifecyclePayload,
  CrawlProgressPayload,
  EmbeddingCompletedPayload,
  EmbeddingProgressPayload,
  ExportDeliveryPayload,
  ExportProgressPayload,
  FindingsUpdatedPayload,
  HostThrottlePayload,
  HubClientMessage,
  HubEvent,
  HubServerEvent,
  HubServerEventName,
  LocalNotificationPayload,
  LogImportCompletedPayload,
  LogImportProgressPayload,
  MatrixRowValidatedEntry,
  MatrixRowValidatedPayload,
  ScheduleFiredPayload,
  ScheduleRunCompletedPayload,
  StageChangedPayload,
  AuditChannelPayload,
  UrlBatchEntry,
  UrlBatchPayload,
  WafBlockBatchEntry,
  WafBlocksPayload
} from './events';
import { HUB_SERVER_EVENT_NAMES } from './events';

/** Narrows a value to one of the listed string literals. */
export function isOneOf<T extends string>(value: unknown, options: readonly T[]): value is T {
  return isString(value) && (options as readonly string[]).includes(value);
}

const AUDIT_STATES: readonly AuditState[] = [
  'queued',
  'running',
  'paused',
  'completed',
  'failed',
  'cancelled'
];

const STATUS_CLASSES: readonly HttpStatusClass[] = ['1xx', '2xx', '3xx', '4xx', '5xx'];

const TRIGGER_TYPES: readonly ScheduleTriggerType[] = ['scheduled', 'catch-up', 'manual'];

const RUN_OUTCOMES: readonly ScheduleRunOutcome[] = ['completed', 'failed', 'cancelled'];

const DELIVERY_STATES: readonly ExportDeliveryState[] = ['delivered', 'retrying', 'failed'];

const EXPORT_JOB_STATES: readonly ExportJobState[] = ['queued', 'running', 'completed', 'failed'];

const EMBEDDING_PROVIDERS: readonly EmbeddingProvider[] = ['onnx', 'loopback-daemon'];

/** Narrows an unknown value to {@link AuditCounters} (additive wafBlocked optional). */
export function isAuditCounters(value: unknown): value is AuditCounters {
  return (
    isRecord(value) &&
    isNumber(value.queued) &&
    isNumber(value.fetched) &&
    isNumber(value.error) &&
    isNumber(value.blocked) &&
    (value.wafBlocked === undefined || isNumber(value.wafBlocked))
  );
}

/** Narrows an unknown value to {@link LogImportCounters}. */
export function isLogImportCounters(value: unknown): value is LogImportCounters {
  return (
    isRecord(value) &&
    isNumber(value.linesParsed) &&
    isNumber(value.linesAccepted) &&
    isNumber(value.linesRejected)
  );
}

/** Narrows an unknown value to {@link LogCorrelationSummary}. */
export function isLogCorrelationSummary(value: unknown): value is LogCorrelationSummary {
  return (
    isRecord(value) &&
    isNumber(value.orphanCount) &&
    isNumber(value.budgetWasteCount) &&
    isNumber(value.uncrawledIndexableCount)
  );
}

/** Narrows an unknown value to {@link CompletionActionResult}. */
export function isCompletionActionResult(value: unknown): value is CompletionActionResult {
  return (
    isRecord(value) &&
    isString(value.kind) &&
    isBoolean(value.succeeded) &&
    (value.detail === null || isString(value.detail))
  );
}

/** Narrows an unknown value to {@link SystemHealthSnapshot} (heartbeat payload). */
export function isSystemHealthSnapshot(value: unknown): value is SystemHealthSnapshot {
  return (
    isRecord(value) &&
    isString(value.timestamp) &&
    isNumber(value.backendWorkingSetBytes) &&
    isNumber(value.managedHeapBytes) &&
    isNumber(value.gcGen0Count) &&
    isNumber(value.gcGen1Count) &&
    isNumber(value.gcGen2Count) &&
    (value.sqliteWalBytes === null || isNumber(value.sqliteWalBytes)) &&
    (value.auditId === undefined || value.auditId === null || isString(value.auditId))
  );
}

/** crawl-progress payload guard (baseline + additive fields). */
export function isCrawlProgressPayload(value: unknown): value is CrawlProgressPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isAuditCounters(value.counters) &&
    isNumber(value.throughputPerSecond) &&
    isNumber(value.elapsedSeconds) &&
    (value.estimatedRemainderSeconds === null || isNumber(value.estimatedRemainderSeconds)) &&
    isString(value.phase) &&
    (value.wafBlocked === undefined || isNumber(value.wafBlocked)) &&
    (value.throttledHostCount === undefined || isNumber(value.throttledHostCount)) &&
    (value.meanEffectiveRate === undefined || isNumber(value.meanEffectiveRate)) &&
    (value.matrixValidated === undefined || isNumber(value.matrixValidated)) &&
    (value.matrixTotal === undefined || isNumber(value.matrixTotal))
  );
}

/** url-batch payload guard (additive block reason/provider optional). */
export function isUrlBatchPayload(value: unknown): value is UrlBatchPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isArrayOf(value.urls, (entry): entry is UrlBatchEntry => {
      if (!isRecord(entry)) return false;
      return (
        isString(entry.urlId) &&
        isString(entry.canonicalUrl) &&
        isOneOf(entry.statusClass, STATUS_CLASSES) &&
        (entry.blockReason === undefined || entry.blockReason === null || isString(entry.blockReason)) &&
        (entry.wafProvider === undefined || entry.wafProvider === null || isString(entry.wafProvider))
      );
    })
  );
}

/** stage-changed payload guard. */
export function isStageChangedPayload(value: unknown): value is StageChangedPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isOneOf(value.previousState, AUDIT_STATES) &&
    isOneOf(value.newState, AUDIT_STATES)
  );
}

/** findings-updated payload guard. */
export function isFindingsUpdatedPayload(value: unknown): value is FindingsUpdatedPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isNumberRecord(value.severityDeltas) &&
    isNumberRecord(value.categoryDeltas)
  );
}

/** audit-completed payload guard. */
export function isAuditCompletedPayload(value: unknown): value is AuditCompletedPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isAuditCounters(value.finalCounters) &&
    isBoolean(value.reportReady)
  );
}

/** audit-failed payload guard. */
export function isAuditFailedPayload(value: unknown): value is AuditFailedPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isString(value.errorCode) &&
    isString(value.message)
  );
}

/** backend-lifecycle payload guard. */
export function isBackendLifecyclePayload(value: unknown): value is BackendLifecyclePayload {
  return (
    isRecord(value) &&
    isOneOf(value.kind, ['ready', 'shutdown'] as const) &&
    (value.message === undefined || value.message === null || isString(value.message))
  );
}

/** schedule-fired payload guard. */
export function isScheduleFiredPayload(value: unknown): value is ScheduleFiredPayload {
  return (
    isRecord(value) &&
    isString(value.scheduleId) &&
    isString(value.auditId) &&
    isOneOf(value.triggerType, TRIGGER_TYPES)
  );
}

/** schedule-run-completed payload guard. */
export function isScheduleRunCompletedPayload(value: unknown): value is ScheduleRunCompletedPayload {
  return (
    isRecord(value) &&
    isString(value.scheduleId) &&
    isString(value.auditId) &&
    isOneOf(value.outcome, RUN_OUTCOMES) &&
    isArrayOf(value.completionActionResults, isCompletionActionResult)
  );
}

/** export-delivery payload guard. */
export function isExportDeliveryPayload(value: unknown): value is ExportDeliveryPayload {
  return (
    isRecord(value) &&
    isString(value.exportId) &&
    isString(value.destinationId) &&
    isOneOf(value.state, DELIVERY_STATES)
  );
}

/** waf-blocks payload guard (additive event). */
export function isWafBlocksPayload(value: unknown): value is WafBlocksPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isArrayOf(value.blocks, (entry): entry is WafBlockBatchEntry => {
      if (!isRecord(entry)) return false;
      return (
        isString(entry.urlId) &&
        isString(entry.canonicalUrl) &&
        isString(entry.provider) &&
        isString(entry.challengeClass) &&
        isNumber(entry.confidence)
      );
    })
  );
}

/** host-throttle payload guard (additive event). */
export function isHostThrottlePayload(value: unknown): value is HostThrottlePayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isString(value.host) &&
    isString(value.trigger) &&
    isNumber(value.rateBefore) &&
    isNumber(value.rateAfter) &&
    isNumber(value.concurrencyBefore) &&
    isNumber(value.concurrencyAfter) &&
    (value.ttfbSampleMs === null || isNumber(value.ttfbSampleMs)) &&
    isNumber(value.rollingBaselineRate)
  );
}

/** matrix-row-validated payload guard (additive event). */
export function isMatrixRowValidatedPayload(value: unknown): value is MatrixRowValidatedPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isArrayOf(value.rows, (entry): entry is MatrixRowValidatedEntry => {
      if (!isRecord(entry)) return false;
      return isString(entry.urlId) && isString(entry.sourceUrl) && isString(entry.verdict);
    })
  );
}

/** log-import-progress payload guard (additive event). */
export function isLogImportProgressPayload(value: unknown): value is LogImportProgressPayload {
  return (
    isRecord(value) &&
    isString(value.importId) &&
    isLogImportCounters(value.counters) &&
    isString(value.phase)
  );
}

/** log-import-completed payload guard (additive event). */
export function isLogImportCompletedPayload(value: unknown): value is LogImportCompletedPayload {
  return (
    isRecord(value) &&
    isString(value.importId) &&
    isLogImportCounters(value.finalCounters) &&
    isLogCorrelationSummary(value.correlation)
  );
}

/** embedding-progress payload guard (additive event). */
export function isEmbeddingProgressPayload(value: unknown): value is EmbeddingProgressPayload {
  return (
    isRecord(value) &&
    isString(value.auditId) &&
    isNumber(value.embeddedCount) &&
    isNumber(value.totalEligible) &&
    isOneOf(value.provider, EMBEDDING_PROVIDERS) &&
    isString(value.phase)
  );
}

/** embedding-completed payload guard (additive event). */
export function isEmbeddingCompletedPayload(value: unknown): value is EmbeddingCompletedPayload {
  if (!isRecord(value) || !isString(value.auditId)) return false;
  const counts = value.finalCounts;
  const passes = value.passes;
  return (
    isRecord(counts) &&
    isNumber(counts.embedded) &&
    isNumber(counts.totalEligible) &&
    isNumber(counts.skipped) &&
    isRecord(passes) &&
    isNumber(passes.clusters) &&
    isNumber(passes.linkOpportunities) &&
    isNumber(passes.lowRelevanceLinks)
  );
}

/** subscribe/unsubscribe payload guard. */
export function isAuditChannelPayload(value: unknown): value is AuditChannelPayload {
  return isRecord(value) && isArrayOf(value.auditIds, isString);
}

/** local-notification payload guard (CR-027 additive event). */
export function isLocalNotificationPayload(value: unknown): value is LocalNotificationPayload {
  return isRecord(value) && isString(value.title) && isString(value.message);
}

/** export-progress payload guard (G-I additive event; nulls tolerated per wire). */
export function isExportProgressPayload(value: unknown): value is ExportProgressPayload {
  return (
    isRecord(value) &&
    isString(value.exportId) &&
    isString(value.auditId) &&
    isString(value.format) &&
    isOneOf(value.state, EXPORT_JOB_STATES) &&
    (value.filePath === null || value.filePath === undefined || isString(value.filePath)) &&
    (value.message === null || value.message === undefined || isString(value.message))
  );
}

/** Payload validator dispatch keyed by server event name. */
const SERVER_PAYLOAD_GUARDS: {
  readonly [E in HubServerEventName]: (value: unknown) => boolean;
} = {
  'crawl-progress': isCrawlProgressPayload,
  'url-batch': isUrlBatchPayload,
  'stage-changed': isStageChangedPayload,
  'findings-updated': isFindingsUpdatedPayload,
  'audit-completed': isAuditCompletedPayload,
  'audit-failed': isAuditFailedPayload,
  'backend-lifecycle': isBackendLifecyclePayload,
  'schedule-fired': isScheduleFiredPayload,
  'schedule-run-completed': isScheduleRunCompletedPayload,
  'export-delivery': isExportDeliveryPayload,
  'waf-blocks': isWafBlocksPayload,
  'host-throttle': isHostThrottlePayload,
  'system-health': isSystemHealthSnapshot,
  'matrix-row-validated': isMatrixRowValidatedPayload,
  'log-import-progress': isLogImportProgressPayload,
  'log-import-completed': isLogImportCompletedPayload,
  'embedding-progress': isEmbeddingProgressPayload,
  'embedding-completed': isEmbeddingCompletedPayload,
  'local-notification': isLocalNotificationPayload,
  'export-progress': isExportProgressPayload
};

/** True when the value is a known server event wire name. */
export function isHubServerEventName(value: unknown): value is HubServerEventName {
  return isOneOf(value, HUB_SERVER_EVENT_NAMES);
}

/**
 * Narrows an unknown wire value to a server hub event: known event name and
 * a payload satisfying that event's contract.
 */
export function isHubServerEvent(value: unknown): value is HubServerEvent {
  if (!isRecord(value) || !isHubServerEventName(value.event) || !isRecord(value.payload)) {
    return false;
  }
  return SERVER_PAYLOAD_GUARDS[value.event](value.payload);
}

/** Narrows an unknown wire value to a subscribe/unsubscribe client message. */
export function isHubClientMessage(value: unknown): value is HubClientMessage {
  return (
    isRecord(value) &&
    isOneOf(value.event, ['subscribe', 'unsubscribe'] as const) &&
    isAuditChannelPayload(value.payload)
  );
}

/** Narrows an unknown wire value to any hub message (either direction). */
export function isHubEvent(value: unknown): value is HubEvent {
  return isHubServerEvent(value) || isHubClientMessage(value);
}

/**
 * Exhaustiveness helper for switch statements over hub events. Calling this
 * with a value that was not narrowed away is a compile-time error.
 */
export function assertNeverHubEvent(value: never): never {
  throw new Error(`Unhandled hub event: ${JSON.stringify(value)}`);
}
