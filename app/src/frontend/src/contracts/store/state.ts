/**
 * Normalized frontend state store shape contract (Zustand-class).
 *
 * Spec surface: FS-CRAWL-002 §8.8 — "Normalized slices: settings, projects,
 * audits, windowed URL/finding/link/n-gram pages, host-throttle state,
 * system-health, plugin registry. Selector subscriptions drive rendering;
 * the hub demultiplexer updates slices immutably; the plugin bridge relays
 * coalesced read-only slices and hub events. No slice ever holds a full
 * dataset." §7.4 — hub ingestion uses bounded ring buffers (recent-activity
 * <= 1,000 entries); counters are aggregated, never raw event history.
 *
 * All state is deeply readonly: updates produce new slice objects
 * (immutability is part of the contract).
 *
 * @module contracts/store/state
 */

import type { IsoTimestamp } from '../common/primitives';
import type { AppSettings, SystemHealthSnapshot } from '../api/system';
import type { ProjectSummary } from '../api/projects';
import type { AuditSummary, Finding, LinkEdge, UrlRecordSummary } from '../api/audits';
import type { NgramRow } from '../api/ngrams';
import type { HostState } from '../api/hosts';
import type { PluginRegistryEntry } from '../api/plugins';
import type { HubConnectionState } from '../hub/client';
import type { HubServerEventName } from '../hub/events';

/**
 * Normalized entity table: O(1) lookup by id plus a stable id order.
 * Tables hold only the entities the shell has actually loaded — never a
 * full dataset (server-windowed lists, §7.4).
 */
export interface EntityTable<T> {
  readonly byId: Readonly<Record<string, T>>;
  readonly allIds: readonly string[];
}

/**
 * One server-windowed page. `key` is the canonical serialization of the
 * list query (filters + sort + offset/limit); `items` is bounded by `limit`.
 */
export interface WindowedPage<T> {
  readonly key: string;
  readonly offset: number;
  readonly limit: number;
  readonly total: number;
  readonly items: readonly T[];
  readonly fetchedAt: IsoTimestamp;
}

/**
 * Windowed-page slice: a bounded cache of pages keyed by query key.
 * Implementations evict pages under an LRU bound (§7.4 window caches).
 */
export interface WindowedSlice<T> {
  readonly pages: Readonly<Record<string, WindowedPage<T>>>;
  readonly activeKey: string | null;
}

/** Settings slice (full settings object; defaults guaranteed by the API). */
export interface SettingsSlice {
  readonly settings: AppSettings | null;
  readonly loaded: boolean;
}

/** Projects slice: normalized summaries plus the current list window. */
export interface ProjectsSlice {
  readonly entities: EntityTable<ProjectSummary>;
  readonly listPage: WindowedPage<ProjectSummary> | null;
}

/** Audits slice: normalized summaries, active audit, current list window. */
export interface AuditsSlice {
  readonly entities: EntityTable<AuditSummary>;
  readonly activeAuditId: string | null;
  readonly listPage: WindowedPage<AuditSummary> | null;
}

/**
 * Host-throttle slice: per-host AIMD state for one audit, keyed by
 * `${auditId}:${host}`. Reconciles host-throttle events with GET hosts.
 */
export interface HostThrottleSlice {
  readonly auditId: string | null;
  readonly byHostKey: Readonly<Record<string, HostState>>;
}

/**
 * System-health slice: latest backend heartbeat plus the locally measured
 * renderer JS heap (performance-memory API where available, otherwise
 * null). The frontend heap is composed client-side and never sent to the
 * backend (FS-CRAWL-002 §7.2).
 */
export interface SystemHealthSlice {
  readonly latest: SystemHealthSnapshot | null;
  readonly frontendJsHeapBytes: number | null;
  readonly lastHeartbeatAt: IsoTimestamp | null;
}

/** Plugin registry slice (local plugins only, E1). */
export interface PluginsSlice {
  readonly entities: EntityTable<PluginRegistryEntry>;
}

/**
 * One recent-activity entry: lightweight metadata only. Raw event payloads
 * are never accumulated as history (§7.4) — counters are aggregated in
 * their slices instead.
 */
export interface HubActivityEntry {
  readonly event: HubServerEventName;
  readonly receivedAt: IsoTimestamp;
  readonly auditId: string | null;
}

/**
 * Bounded ring buffer contract for recent activity. Invariant:
 * `entries.length <= capacity`; overflow increments `droppedCount`.
 */
export interface RecentActivityBuffer {
  readonly entries: readonly HubActivityEntry[];
  readonly capacity: number;
  readonly droppedCount: number;
}

/** Default recent-activity capacity (FS-CRAWL-002 §7.4: <= 1,000 entries). */
export const DEFAULT_RECENT_ACTIVITY_CAPACITY = 1000;

/** Hub connection slice: one connection feeding the event demultiplexer. */
export interface HubSlice {
  readonly connectionState: HubConnectionState;
  readonly subscribedAuditIds: readonly string[];
  readonly recentActivity: RecentActivityBuffer;
}

/**
 * The complete normalized store shape. Slice names mirror §8.8 exactly
 * (settings, projects, audits, windowed URL/finding/link/n-gram pages,
 * host-throttle, system-health, plugin registry) plus the hub connection
 * slice required by §8.1's single-connection demultiplexer.
 */
export interface CrawlForgeState {
  readonly settings: SettingsSlice;
  readonly projects: ProjectsSlice;
  readonly audits: AuditsSlice;
  readonly urlPages: WindowedSlice<UrlRecordSummary>;
  readonly findingPages: WindowedSlice<Finding>;
  readonly linkPages: WindowedSlice<LinkEdge>;
  readonly ngramPages: WindowedSlice<NgramRow>;
  readonly hostThrottle: HostThrottleSlice;
  readonly systemHealth: SystemHealthSlice;
  readonly plugins: PluginsSlice;
  readonly hub: HubSlice;
}

/** Slice names of the normalized store. */
export type CrawlForgeSliceName = keyof CrawlForgeState;
