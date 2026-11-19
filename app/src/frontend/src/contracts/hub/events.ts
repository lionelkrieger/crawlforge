/**
 * Real-time hub event contracts — discriminated unions over the `event`
 * discriminant.
 *
 * Spec surface: FS-CRAWL-001 §10.3 (11 baseline rows: 10 server events plus
 * the subscribe/unsubscribe channel) + FS-CRAWL-002 §7.2 and Appendix C
 * (8 additive server events) + CR-027 local-notification (05/02 §76) +
 * G-I export-progress (WP-09-10 producer, §7.2 additive export telemetry).
 * 21 event contracts total; 22 distinct wire names (subscribe and unsubscribe
 * are two messages of one channel).
 *
 * Additive payload fields (Appendix C): crawl-progress gains wafBlocked,
 * throttled-host count, mean effective rate, and optional matrix counters;
 * url-batch entries gain optional block reason/provider; audit-completed
 * final counters gain the WAF sub-count. Parent emission rules and the
 * subscribe/unsubscribe contract are unchanged.
 *
 * Clean-room: no event carries identity/machine/account dimensions
 * (precedence rule 8); system-health carries technical counters only.
 *
 * @module contracts/hub/events
 */

import type { EntityId } from '../common/primitives';
import type { AuditCounters, AuditState, HttpStatusClass } from '../api/audits';
import type { CompletionActionResult, ScheduleRunOutcome, ScheduleTriggerType } from '../api/schedules';
import type { ExportDeliveryState, ExportJobState } from '../api/exports';
import type { LogCorrelationSummary, LogImportCounters } from '../api/logs';
import type { EmbeddingProvider } from '../api/semantics';
import type { SystemHealthSnapshot } from '../api/system';

/** Uniform hub message shape: event name discriminant plus typed payload. */
export interface HubEventEnvelope<TName extends string, TPayload> {
  readonly event: TName;
  readonly payload: TPayload;
}

// ── Baseline server events (FS-CRAWL-001 §10.3) ────────────────────────────

/**
 * crawl-progress — at most once per second while running.
 * Additive fields (FS-CRAWL-002 Appendix C): wafBlocked, throttledHostCount,
 * meanEffectiveRate, and optional matrix counters while a migration matrix
 * is attached.
 */
export interface CrawlProgressPayload {
  readonly auditId: EntityId;
  readonly counters: AuditCounters;
  readonly throughputPerSecond: number;
  readonly elapsedSeconds: number;
  readonly estimatedRemainderSeconds: number | null;
  readonly phase: string;
  /** Additive: WAF-blocked sub-count. */
  readonly wafBlocked?: number;
  /** Additive: number of currently throttled hosts. */
  readonly throttledHostCount?: number;
  /** Additive: mean effective requests-per-second across hosts. */
  readonly meanEffectiveRate?: number;
  /** Additive: validated matrix rows (present while a matrix is attached). */
  readonly matrixValidated?: number;
  /** Additive: total matrix rows (present while a matrix is attached). */
  readonly matrixTotal?: number;
}

/** One url-batch entry; additive block reason/provider on blocked entries. */
export interface UrlBatchEntry {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly statusClass: HttpStatusClass;
  /** Additive: block reason on blocked entries. */
  readonly blockReason?: string | null;
  /** Additive: detected WAF provider on blocked entries. */
  readonly wafProvider?: string | null;
}

/** url-batch — throttled batches, not per-URL messages. */
export interface UrlBatchPayload {
  readonly auditId: EntityId;
  readonly urls: readonly UrlBatchEntry[];
}

/** stage-changed — on every lifecycle transition. */
export interface StageChangedPayload {
  readonly auditId: EntityId;
  readonly previousState: AuditState;
  readonly newState: AuditState;
}

/** findings-updated — severity/category count deltas, throttled. */
export interface FindingsUpdatedPayload {
  readonly auditId: EntityId;
  readonly severityDeltas: Readonly<Record<string, number>>;
  readonly categoryDeltas: Readonly<Record<string, number>>;
}

/**
 * audit-completed — once, on completion. Final counters include the
 * additive WAF sub-count (AuditCounters.wafBlocked).
 */
export interface AuditCompletedPayload {
  readonly auditId: EntityId;
  readonly finalCounters: AuditCounters;
  readonly reportReady: boolean;
}

/** audit-failed — once, on failure. */
export interface AuditFailedPayload {
  readonly auditId: EntityId;
  readonly errorCode: string;
  readonly message: string;
}

/** backend-lifecycle — startup readiness and graceful shutdown notices. */
export interface BackendLifecyclePayload {
  readonly kind: 'ready' | 'shutdown';
  readonly message?: string | null;
}

/** schedule-fired — on each scheduled or catch-up firing. */
export interface ScheduleFiredPayload {
  readonly scheduleId: EntityId;
  readonly auditId: EntityId;
  readonly triggerType: ScheduleTriggerType;
}

/** schedule-run-completed — once per scheduled run finalization. */
export interface ScheduleRunCompletedPayload {
  readonly scheduleId: EntityId;
  readonly auditId: EntityId;
  readonly outcome: ScheduleRunOutcome;
  readonly completionActionResults: readonly CompletionActionResult[];
}

/** export-delivery — on destination delivery state changes. */
export interface ExportDeliveryPayload {
  readonly exportId: EntityId;
  readonly destinationId: EntityId;
  readonly state: ExportDeliveryState;
}

// ── Additive server events (FS-CRAWL-002 §7.2, Appendix C) ─────────────────

/** One waf-blocks batch entry. */
export interface WafBlockBatchEntry {
  readonly urlId: EntityId;
  readonly canonicalUrl: string;
  readonly provider: string;
  readonly challengeClass: string;
  readonly confidence: number;
}

/** waf-blocks — throttled batches, at most once per second. */
export interface WafBlocksPayload {
  readonly auditId: EntityId;
  readonly blocks: readonly WafBlockBatchEntry[];
}

/**
 * host-throttle — on every adjustment, coalesced <= 1/s per host.
 * Mirrors the AIMD controller state (FS-CRAWL-002 §3.7).
 */
export interface HostThrottlePayload {
  readonly auditId: EntityId;
  readonly host: string;
  readonly trigger: string;
  readonly rateBefore: number;
  readonly rateAfter: number;
  readonly concurrencyBefore: number;
  readonly concurrencyAfter: number;
  readonly ttfbSampleMs: number | null;
  readonly rollingBaselineRate: number;
}

/**
 * system-health — every 2 seconds while the shell is connected, idle or
 * running, independent of per-audit subscriptions. Payload is identical to
 * GET /api/system/health (shared {@link SystemHealthSnapshot} type).
 */
export type SystemHealthPayload = SystemHealthSnapshot;

/** One matrix-row-validated batch entry. */
export interface MatrixRowValidatedEntry {
  readonly urlId: EntityId;
  readonly sourceUrl: string;
  readonly verdict: string;
}

/** matrix-row-validated — throttled batches, at most once per second. */
export interface MatrixRowValidatedPayload {
  readonly auditId: EntityId;
  readonly rows: readonly MatrixRowValidatedEntry[];
}

/** log-import-progress — at most once per second while importing. */
export interface LogImportProgressPayload {
  readonly importId: EntityId;
  readonly counters: LogImportCounters;
  readonly phase: string;
}

/** log-import-completed — once per import finalization. */
export interface LogImportCompletedPayload {
  readonly importId: EntityId;
  readonly finalCounters: LogImportCounters;
  readonly correlation: LogCorrelationSummary;
}

/** embedding-progress — at most once per second while running. */
export interface EmbeddingProgressPayload {
  readonly auditId: EntityId;
  readonly embeddedCount: number;
  readonly totalEligible: number;
  readonly provider: EmbeddingProvider;
  readonly phase: string;
}

/** Final counts of an embedding job. */
export interface EmbeddingFinalCounts {
  readonly embedded: number;
  readonly totalEligible: number;
  readonly skipped: number;
}

/** Pass summaries of an embedding job (clusters, opportunities, low-relevance links). */
export interface EmbeddingPassSummary {
  readonly clusters: number;
  readonly linkOpportunities: number;
  readonly lowRelevanceLinks: number;
}

/** embedding-completed — once per job finalization. */
export interface EmbeddingCompletedPayload {
  readonly auditId: EntityId;
  readonly finalCounts: EmbeddingFinalCounts;
  readonly passes: EmbeddingPassSummary;
}

/**
 * local-notification — CR-027 (05/02 §76): OS-level notification request.
 * The shell renders it through the platform Notification API; title and
 * message are the only payload members (no identity/machine dimensions).
 */
export interface LocalNotificationPayload {
  readonly title: string;
  readonly message: string;
}

/**
 * export-progress — G-I additive export telemetry (WP-09-10 producer).
 * At most once per second per export job while it runs; string EntityIds,
 * state from the frozen ExportJobState vocabulary. filePath is set when
 * completed; message carries failure/progress detail (omitted when null).
 */
export interface ExportProgressPayload {
  readonly exportId: EntityId;
  readonly auditId: EntityId;
  readonly format: string;
  readonly state: ExportJobState;
  readonly filePath: string | null;
  readonly message: string | null;
}

// ── Client channel (FS-CRAWL-001 §10.3: subscribe / unsubscribe) ───────────

/**
 * Subscribe/unsubscribe channel payload: audit identifier(s). The client
 * joins/leaves per-audit channels; no authorization beyond the local
 * boundary.
 */
export interface AuditChannelPayload {
  readonly auditIds: readonly EntityId[];
}

// ── Discriminated unions ───────────────────────────────────────────────────

/** All 20 server -> shell events (10 baseline + 10 additive). */
export type HubServerEvent =
  | HubEventEnvelope<'crawl-progress', CrawlProgressPayload>
  | HubEventEnvelope<'url-batch', UrlBatchPayload>
  | HubEventEnvelope<'stage-changed', StageChangedPayload>
  | HubEventEnvelope<'findings-updated', FindingsUpdatedPayload>
  | HubEventEnvelope<'audit-completed', AuditCompletedPayload>
  | HubEventEnvelope<'audit-failed', AuditFailedPayload>
  | HubEventEnvelope<'backend-lifecycle', BackendLifecyclePayload>
  | HubEventEnvelope<'schedule-fired', ScheduleFiredPayload>
  | HubEventEnvelope<'schedule-run-completed', ScheduleRunCompletedPayload>
  | HubEventEnvelope<'export-delivery', ExportDeliveryPayload>
  | HubEventEnvelope<'waf-blocks', WafBlocksPayload>
  | HubEventEnvelope<'host-throttle', HostThrottlePayload>
  | HubEventEnvelope<'system-health', SystemHealthPayload>
  | HubEventEnvelope<'matrix-row-validated', MatrixRowValidatedPayload>
  | HubEventEnvelope<'log-import-progress', LogImportProgressPayload>
  | HubEventEnvelope<'log-import-completed', LogImportCompletedPayload>
  | HubEventEnvelope<'embedding-progress', EmbeddingProgressPayload>
  | HubEventEnvelope<'embedding-completed', EmbeddingCompletedPayload>
  | HubEventEnvelope<'local-notification', LocalNotificationPayload>
  | HubEventEnvelope<'export-progress', ExportProgressPayload>;

/** Shell -> server channel messages (one channel, two messages). */
export type HubClientMessage =
  | HubEventEnvelope<'subscribe', AuditChannelPayload>
  | HubEventEnvelope<'unsubscribe', AuditChannelPayload>;

/** Any hub message in either direction. */
export type HubEvent = HubServerEvent | HubClientMessage;

/** Wire names of the 20 server events. */
export type HubServerEventName = HubServerEvent['event'];

/** Wire names of the 2 client channel messages. */
export type HubClientMessageName = HubClientMessage['event'];

/** All 22 wire names. */
export type HubEventName = HubEvent['event'];

/** Payload lookup by server event name. */
export type HubServerPayloadOf<E extends HubServerEventName> = Extract<
  HubServerEvent,
  { event: E }
>['payload'];

/** Payload lookup by client message name. */
export type HubClientPayloadOf<M extends HubClientMessageName> = Extract<
  HubClientMessage,
  { event: M }
>['payload'];

/** Payload lookup by any event name. */
export type HubPayloadOf<E extends HubEventName> = Extract<HubEvent, { event: E }>['payload'];

// ── Declarative event catalog (contract metadata, no behavior) ─────────────

/** Direction and provenance of one hub event contract. */
export interface HubEventDefinition {
  readonly direction: 'server' | 'client';
  /** True for FS-CRAWL-002 additive events. */
  readonly additive: boolean;
}

/**
 * Frozen catalog of all 22 wire names / 21 event contracts. The
 * subscribe/unsubscribe pair is one channel contract (baseline row 11).
 */
export const HUB_EVENT_CATALOG = {
  'crawl-progress': { direction: 'server', additive: false },
  'url-batch': { direction: 'server', additive: false },
  'stage-changed': { direction: 'server', additive: false },
  'findings-updated': { direction: 'server', additive: false },
  'audit-completed': { direction: 'server', additive: false },
  'audit-failed': { direction: 'server', additive: false },
  'backend-lifecycle': { direction: 'server', additive: false },
  'schedule-fired': { direction: 'server', additive: false },
  'schedule-run-completed': { direction: 'server', additive: false },
  'export-delivery': { direction: 'server', additive: false },
  'waf-blocks': { direction: 'server', additive: true },
  'host-throttle': { direction: 'server', additive: true },
  'system-health': { direction: 'server', additive: true },
  'matrix-row-validated': { direction: 'server', additive: true },
  'log-import-progress': { direction: 'server', additive: true },
  'log-import-completed': { direction: 'server', additive: true },
  'embedding-progress': { direction: 'server', additive: true },
  'embedding-completed': { direction: 'server', additive: true },
  'local-notification': { direction: 'server', additive: true },
  'export-progress': { direction: 'server', additive: true },
  subscribe: { direction: 'client', additive: false },
  unsubscribe: { direction: 'client', additive: false }
} as const satisfies Record<HubEventName, HubEventDefinition>;

/** Ordered wire names of the 20 server events (exhaustiveness-locked below). */
export const HUB_SERVER_EVENT_NAMES = [
  'crawl-progress',
  'url-batch',
  'stage-changed',
  'findings-updated',
  'audit-completed',
  'audit-failed',
  'backend-lifecycle',
  'schedule-fired',
  'schedule-run-completed',
  'export-delivery',
  'waf-blocks',
  'host-throttle',
  'system-health',
  'matrix-row-validated',
  'log-import-progress',
  'log-import-completed',
  'embedding-progress',
  'embedding-completed',
  'local-notification',
  'export-progress'
] as const satisfies readonly HubServerEventName[];

/** Wire names of the client channel messages. */
export const HUB_CLIENT_MESSAGE_NAMES = [
  'subscribe',
  'unsubscribe'
] as const satisfies readonly HubClientMessageName[];

/** Compile-time lock: the server name list covers the union exactly. */
export type AssertServerNamesExhaustive = [
  Exclude<HubServerEventName, (typeof HUB_SERVER_EVENT_NAMES)[number]>
] extends [never]
  ? true
  : never;

/** Compile-time lock: the client name list covers the union exactly. */
export type AssertClientNamesExhaustive = [
  Exclude<HubClientMessageName, (typeof HUB_CLIENT_MESSAGE_NAMES)[number]>
] extends [never]
  ? true
  : never;

/** Compile-time lock: the catalog covers every wire name. */
export type AssertCatalogExhaustive = [Exclude<HubEventName, keyof typeof HUB_EVENT_CATALOG>] extends [
  never
]
  ? true
  : never;
