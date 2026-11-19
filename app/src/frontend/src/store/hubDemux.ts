/**
 * Hub event demultiplexer (FS-CRAWL-002 §8.1/§8.8): one connection feeds
 * typed per-event handlers that update normalized slices immutably, the
 * bounded live-telemetry store (aggregated counters, latest-wins), the
 * bounded recent-activity ring buffer, and the plugin relay bus.
 *
 * Retention discipline (§7.4): url-batch / waf-blocks / matrix rows are
 * AGGREGATED, never retained as raw history; crawl-progress counters
 * replace (no event history); system-health is telemetry and does not flood
 * the activity feed.
 *
 * @module store/hubDemux
 */

import type { CrawlForgeStore } from '../contracts/store/selectors';
import type {
  HubServerEvent,
  HubServerEventName,
  HubServerPayloadOf,
  CrawlProgressPayload,
  UrlBatchPayload,
  StageChangedPayload,
  FindingsUpdatedPayload,
  AuditCompletedPayload,
  AuditFailedPayload,
  BackendLifecyclePayload,
  ScheduleFiredPayload,
  ScheduleRunCompletedPayload,
  ExportDeliveryPayload,
  WafBlocksPayload,
  HostThrottlePayload,
  SystemHealthPayload,
  MatrixRowValidatedPayload,
  LogImportProgressPayload,
  LogImportCompletedPayload,
  EmbeddingProgressPayload,
  EmbeddingCompletedPayload,
  LocalNotificationPayload,
  ExportProgressPayload
} from '../contracts/hub/events';
import { HUB_SERVER_EVENT_NAMES } from '../contracts/hub/events';
import type { HubActivityEntry } from '../contracts/store/state';
import type { HostState } from '../contracts/api/hosts';
import type { CrawlForgeStoreActions } from './crawlForgeStore';
import { upsertEntity } from './entityTable';
import { pushActivity } from './ringBuffer';
import { measureFrontendJsHeap } from './frontendHeap';
import type { LiveProgressStore } from './liveProgress';
import {
  ingestBackendLifecycle,
  ingestCrawlProgress,
  ingestEmbeddingCompleted,
  ingestEmbeddingProgress,
  ingestFindingDeltas,
  ingestLogImportCompleted,
  ingestLogImportProgress,
  ingestMatrixRows,
  ingestUrlBatchClasses,
  ingestWafBlocks
} from './liveProgress';
import type { CrawlForgePluginBus } from './pluginBus';

/** Demultiplexer dependencies. */
export interface HubDemuxOptions {
  readonly store: CrawlForgeStore;
  readonly actions: CrawlForgeStoreActions;
  readonly live: LiveProgressStore;
  readonly bus?: CrawlForgePluginBus;
  /** Clock for activity timestamps (injectable for deterministic tests). */
  readonly now?: () => string;
}

/** Typed handler map over every server event. */
export type HubDemuxHandlers = {
  [E in HubServerEventName]: (payload: HubServerPayloadOf<E>) => void;
};

/** Extracts an audit id from any payload that carries one (activity feed). */
function activityAuditId(payload: unknown): string | null {
  if (typeof payload === 'object' && payload !== null && 'auditId' in payload) {
    const value = (payload as { auditId?: unknown }).auditId;
    return typeof value === 'string' ? value : null;
  }
  return null;
}

/** HOTFIX-01: terminal audit states must not be overwritten by live progress. */
function isTerminalState(state: string | undefined): boolean {
  return state === 'completed' || state === 'failed' || state === 'cancelled';
}

/**
 * Creates the demultiplexer. `ingest(event)` is the single entry point used
 * by the SignalR adapter; `handlers` exposes the typed per-event surface.
 */
export function createHubDemux(options: HubDemuxOptions) {
  const { store, live, bus } = options;
  const now = options.now ?? (() => new Date().toISOString());

  function recordActivity(event: HubServerEventName, payload: unknown): void {
    const entry: HubActivityEntry = {
      event,
      receivedAt: now(),
      auditId: activityAuditId(payload)
    };
    store.setState((state) => ({
      hub: { ...state.hub, recentActivity: pushActivity(state.hub.recentActivity, entry) }
    }));
  }

  function relay(event: HubServerEvent): void {
    bus?.relayHubEvent(event);
  }

  const handlers: HubDemuxHandlers = {
    'crawl-progress': (payload: CrawlProgressPayload) => {
      ingestCrawlProgress(live, payload);
      store.setState((state) => {
        const existing = state.audits.entities.byId[payload.auditId];
        if (!existing) return state;
        return {
          audits: {
            ...state.audits,
            entities: upsertEntity(state.audits.entities, {
              ...existing,
              counters: payload.counters,
              phase: payload.phase,
              // HOTFIX-01: a late/coalesced progress event must not resurrect
              // an audit that already reached a terminal state.
              state: isTerminalState(existing.state) ? existing.state : 'running'
            })
          }
        };
      });
      recordActivity('crawl-progress', payload);
      relay({ event: 'crawl-progress', payload });
    },

    'url-batch': (payload: UrlBatchPayload) => {
      // Aggregate status classes only — no raw URL row retention (§7.4).
      ingestUrlBatchClasses(live, payload.auditId, payload.urls.map((url) => url.statusClass));
      recordActivity('url-batch', payload);
      relay({ event: 'url-batch', payload });
    },

    'stage-changed': (payload: StageChangedPayload) => {
      store.setState((state) => {
        const existing = state.audits.entities.byId[payload.auditId];
        if (!existing) return state;
        return {
          audits: {
            ...state.audits,
            entities: upsertEntity(state.audits.entities, { ...existing, state: payload.newState })
          }
        };
      });
      recordActivity('stage-changed', payload);
      relay({ event: 'stage-changed', payload });
    },

    'findings-updated': (payload: FindingsUpdatedPayload) => {
      ingestFindingDeltas(live, payload.auditId, payload.severityDeltas, payload.categoryDeltas);
      recordActivity('findings-updated', payload);
      relay({ event: 'findings-updated', payload });
    },

    'audit-completed': (payload: AuditCompletedPayload) => {
      store.setState((state) => {
        const existing = state.audits.entities.byId[payload.auditId];
        const base = existing ?? {
          id: payload.auditId,
          projectId: '',
          configurationId: '',
          state: 'completed' as const,
          phase: 'completed',
          startedAt: now(),
          completedAt: null,
          counters: payload.finalCounters
        };
        return {
          audits: {
            ...state.audits,
            entities: upsertEntity(state.audits.entities, {
              ...base,
              // HOTFIX-01: reportReady=false means a non-success terminal
              // finalize (e.g. cancelled); do not present it as completed.
              state: payload.reportReady ? 'completed' : 'cancelled',
              counters: payload.finalCounters,
              completedAt: now()
            })
          }
        };
      });
      bus?.notifyCrawlComplete(payload.auditId, payload.finalCounters, payload.reportReady);
      recordActivity('audit-completed', payload);
      relay({ event: 'audit-completed', payload });
    },

    'audit-failed': (payload: AuditFailedPayload) => {
      store.setState((state) => {
        const existing = state.audits.entities.byId[payload.auditId];
        const base = existing ?? {
          id: payload.auditId,
          projectId: '',
          configurationId: '',
          state: 'running' as const,
          phase: 'finalizing',
          startedAt: now(),
          completedAt: null,
          counters: { queued: 0, fetched: 0, error: 0, blocked: 0 }
        };
        return {
          audits: {
            ...state.audits,
            entities: upsertEntity(state.audits.entities, {
              ...base,
              state: 'failed',
              completedAt: now(),
              errorMessage: payload.message
            })
          }
        };
      });
      recordActivity('audit-failed', payload);
      relay({ event: 'audit-failed', payload });
    },

    'backend-lifecycle': (payload: BackendLifecyclePayload) => {
      ingestBackendLifecycle(live, payload);
      recordActivity('backend-lifecycle', payload);
      relay({ event: 'backend-lifecycle', payload });
    },

    'schedule-fired': (payload: ScheduleFiredPayload) => {
      recordActivity('schedule-fired', payload);
      relay({ event: 'schedule-fired', payload });
    },

    'schedule-run-completed': (payload: ScheduleRunCompletedPayload) => {
      recordActivity('schedule-run-completed', payload);
      relay({ event: 'schedule-run-completed', payload });
    },

    'export-delivery': (payload: ExportDeliveryPayload) => {
      recordActivity('export-delivery', payload);
      relay({ event: 'export-delivery', payload });
    },

    'waf-blocks': (payload: WafBlocksPayload) => {
      ingestWafBlocks(live, payload.auditId, payload.blocks.map((block) => block.provider));
      recordActivity('waf-blocks', payload);
      relay({ event: 'waf-blocks', payload });
    },

    'host-throttle': (payload: HostThrottlePayload) => {
      store.setState((state) => {
        const key = `${payload.auditId}:${payload.host}`;
        const existing = state.hostThrottle.byHostKey[key];
        const next: HostState = {
          host: payload.host,
          effectiveRate: payload.rateAfter,
          concurrency: payload.concurrencyAfter,
          ttfbEwmaMs: payload.ttfbSampleMs,
          rollingBaselineRate: payload.rollingBaselineRate,
          throttled: payload.rateAfter < payload.rateBefore,
          override: existing?.override ?? null
        };
        return {
          hostThrottle: {
            auditId: payload.auditId,
            byHostKey: { ...state.hostThrottle.byHostKey, [key]: next }
          }
        };
      });
      recordActivity('host-throttle', payload);
      relay({ event: 'host-throttle', payload });
    },

    'system-health': (payload: SystemHealthPayload) => {
      // Telemetry, not activity: never floods the bounded feed.
      store.setState({
        systemHealth: {
          latest: payload,
          frontendJsHeapBytes: measureFrontendJsHeap(),
          lastHeartbeatAt: payload.timestamp
        }
      });
      relay({ event: 'system-health', payload });
    },

    'matrix-row-validated': (payload: MatrixRowValidatedPayload) => {
      ingestMatrixRows(live, payload.auditId, payload.rows.length);
      recordActivity('matrix-row-validated', payload);
      relay({ event: 'matrix-row-validated', payload });
    },

    'log-import-progress': (payload: LogImportProgressPayload) => {
      ingestLogImportProgress(live, payload);
      recordActivity('log-import-progress', payload);
      relay({ event: 'log-import-progress', payload });
    },

    'log-import-completed': (payload: LogImportCompletedPayload) => {
      ingestLogImportCompleted(live, payload.importId, payload.finalCounters ?? {});
      recordActivity('log-import-completed', payload);
      relay({ event: 'log-import-completed', payload });
    },

    'embedding-progress': (payload: EmbeddingProgressPayload) => {
      ingestEmbeddingProgress(live, payload);
      recordActivity('embedding-progress', payload);
      relay({ event: 'embedding-progress', payload });
    },

    'embedding-completed': (payload: EmbeddingCompletedPayload) => {
      ingestEmbeddingCompleted(live, payload.auditId, payload.finalCounts, payload.passes);
      recordActivity('embedding-completed', payload);
      relay({ event: 'embedding-completed', payload });
    },

    'local-notification': (payload: LocalNotificationPayload) => {
      // CR-027 (05/02 §76): OS-level notification through the platform API
      // (Electron renderer). Guarded — unavailable/denied contexts degrade to
      // the activity feed + plugin relay only; never throws into the demux.
      try {
        if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
          new Notification(payload.title, { body: payload.message });
        }
      } catch {
        // Notification construction can throw in restricted contexts — advisory.
      }
      recordActivity('local-notification', payload);
      relay({ event: 'local-notification', payload });
    },

    'export-progress': (payload: ExportProgressPayload) => {
      // G-I additive export telemetry: activity feed + plugin relay. Export
      // job state is polled over REST by the export dialog (no live slice —
      // retention discipline: no raw export history in the telemetry store).
      recordActivity('export-progress', payload);
      relay({ event: 'export-progress', payload });
    }
  };

  /** Single entry point: dispatch one typed server event. */
  function ingest(event: HubServerEvent): void {
    const handler = handlers[event.event] as (payload: unknown) => void;
    handler(event.payload);
  }

  return { handlers, ingest, eventNames: HUB_SERVER_EVENT_NAMES };
}
