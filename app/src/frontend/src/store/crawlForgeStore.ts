/**
 * The normalized CrawlForge store (Zustand v5) implementing the locked
 * contracts/store shape exactly: entity tables, bounded windowed pages,
 * host-throttle reconciliation, system-health, plugin registry, and the hub
 * connection slice with a bounded recent-activity ring buffer.
 *
 * FS-CRAWL-002 §8.8: selector subscriptions drive rendering; the hub
 * demultiplexer updates slices immutably; no slice ever holds a full
 * dataset. FS-CRAWL-002 §7.4: LRU-bounded window caches, <= 1,000 activity
 * entries, aggregated counters only.
 *
 * The state shape is pure data (the locked contract); loaders and ingest
 * functions live in the actions object returned alongside the store.
 *
 * @module store/crawlForgeStore
 */

import { useCallback, useRef, useSyncExternalStore } from 'react';
import { createStore } from 'zustand/vanilla';
import type {
  CrawlForgeState,
  WindowedPage,
  WindowedSlice
} from '../contracts/store/state';
import { DEFAULT_RECENT_ACTIVITY_CAPACITY } from '../contracts/store/state';
import type {
  CrawlForgeStore,
  CrawlForgeStoreHook,
  EqualityFn,
  Selector
} from '../contracts/store/selectors';
import type { LoopbackApiClient } from '../contracts/api/client';
import type { ListQuery } from '../contracts/common/pagination';
import type { AppSettings } from '../contracts/api/system';
import type { ProjectSummary } from '../contracts/api/projects';
import type {
  AuditSummary,
  Finding,
  LinkEdge,
  UrlRecordSummary
} from '../contracts/api/audits';
import type { NgramRow } from '../contracts/api/ngrams';
import type { HostState } from '../contracts/api/hosts';
import type { PluginRegistryEntry } from '../contracts/api/plugins';
import type { HubConnectionState } from '../contracts/hub/client';
import { emptyTable, tableFromEntities, upsertEntities, upsertEntity } from './entityTable';
import {
  canonicalQueryKey,
  emptyWindowedSlice,
  pageFromResponse,
  putPage
} from './windowedCache';
import { emptyActivityBuffer } from './ringBuffer';
import { measureFrontendJsHeap } from './frontendHeap';

/** Default list window size for shell loaders (server clamps its own bound). */
export const DEFAULT_LIST_LIMIT = 50;

/** Creates the initial (empty, offline-safe) normalized state. */
export function createInitialState(): CrawlForgeState {
  return {
    settings: { settings: null, loaded: false },
    projects: { entities: emptyTable<ProjectSummary>(), listPage: null },
    audits: { entities: emptyTable<AuditSummary>(), activeAuditId: null, listPage: null },
    urlPages: emptyWindowedSlice<UrlRecordSummary>(),
    findingPages: emptyWindowedSlice<Finding>(),
    linkPages: emptyWindowedSlice<LinkEdge>(),
    ngramPages: emptyWindowedSlice<NgramRow>(),
    hostThrottle: { auditId: null, byHostKey: {} },
    systemHealth: { latest: null, frontendJsHeapBytes: null, lastHeartbeatAt: null },
    plugins: { entities: emptyTable<PluginRegistryEntry>() },
    hub: {
      connectionState: 'Disconnected',
      subscribedAuditIds: [],
      recentActivity: emptyActivityBuffer(DEFAULT_RECENT_ACTIVITY_CAPACITY)
    }
  };
}

/** Monotonic store revisions (plugin-bridge relay accounting). */
const revisions = new WeakMap<object, number>();

/** Reads the current monotonic revision of a store. */
export function getStoreRevision(store: CrawlForgeStore): number {
  return revisions.get(store) ?? 0;
}

/**
 * Creates the vanilla normalized store plus its revision counter. Every
 * state change bumps the revision exactly once (subscribe fires after each
 * committed update).
 */
export function createCrawlForgeStore(): CrawlForgeStore {
  const store = createStore<CrawlForgeState>()(() => createInitialState());
  revisions.set(store, 0);
  store.subscribe(() => {
    revisions.set(store, (revisions.get(store) ?? 0) + 1);
  });
  return store;
}

/**
 * Binds the React hook surface: calling the hook with a selector subscribes
 * the component to that projection only (§8.8 selector subscriptions).
 * Built on React's useSyncExternalStore with a cached projection and the
 * caller's equality function (zustand v5's traditional entry would require
 * an extra peer package; this keeps the approved dependency set intact).
 */
export function createStoreHook(store: CrawlForgeStore): CrawlForgeStoreHook {
  const hook = <T>(selector: Selector<T>, equalityFn?: EqualityFn<T>): T => {
    const equals = equalityFn ?? Object.is;
    const cache = useRef<{ state: CrawlForgeState; value: T } | null>(null);
    const getSnapshot = useCallback((): T => {
      const state = store.getState();
      const previous = cache.current;
      if (previous && previous.state === state) return previous.value;
      const next = selector(state);
      if (previous && equals(previous.value, next)) {
        cache.current = { state, value: previous.value };
        return previous.value;
      }
      cache.current = { state, value: next };
      return next;
    }, [store, selector, equals]);
    return useSyncExternalStore(store.subscribe, getSnapshot, getSnapshot);
  };
  return Object.assign(hook, store) as CrawlForgeStoreHook;
}

/** Per-audit window key: audit scope + canonical query serialization. */
export function auditPageKey(auditId: string, query: ListQuery | undefined): string {
  return `${auditId}|${canonicalQueryKey(query)}`;
}

/** Result of a loader: the loaded page/entities or the envelope error. */
export interface LoadOutcome<T> {
  readonly ok: boolean;
  readonly data?: T;
  readonly errorCode?: string;
  readonly errorMessage?: string;
}

/**
 * Store actions: REST loaders (offline-first — every loader degrades to an
 * error outcome, never a throw) and slice setters used by the demultiplexer.
 */
export interface CrawlForgeStoreActions {
  loadSettings(): Promise<LoadOutcome<AppSettings>>;
  loadProjects(query?: ListQuery): Promise<LoadOutcome<readonly ProjectSummary[]>>;
  loadAudits(query?: ListQuery): Promise<LoadOutcome<readonly AuditSummary[]>>;
  setActiveAudit(auditId: string | null): void;
  loadUrlPage(auditId: string, query?: ListQuery): Promise<LoadOutcome<WindowedPage<UrlRecordSummary>>>;
  loadFindingPage(auditId: string, query?: ListQuery): Promise<LoadOutcome<WindowedPage<Finding>>>;
  loadLinkPage(auditId: string, query?: ListQuery): Promise<LoadOutcome<WindowedPage<LinkEdge>>>;
  loadNgramPage(auditId: string, query?: ListQuery): Promise<LoadOutcome<WindowedPage<NgramRow>>>;
  loadHosts(auditId: string): Promise<LoadOutcome<readonly HostState[]>>;
  loadPlugins(): Promise<LoadOutcome<readonly PluginRegistryEntry[]>>;
  setConnectionState(state: HubConnectionState): void;
  setSubscribedAuditIds(auditIds: readonly string[]): void;
  /** Replaces the windowed slice for one entity kind (bounded LRU insert). */
  putWindowedPage<K extends 'urlPages' | 'findingPages' | 'linkPages' | 'ngramPages'>(
    slice: K,
    page: WindowedPage<CrawlForgeState[K]['pages'][string]['items'][number]>
  ): void;
}

/** Creates the action surface bound to a store and (optional) API client. */
export function createStoreActions(
  store: CrawlForgeStore,
  api: LoopbackApiClient | null
): CrawlForgeStoreActions {
  const now = (): string => new Date().toISOString();

  async function loadList<T extends { readonly id: string }>(
    route: 'projects.list' | 'audits.list' | 'plugins.list',
    query: ListQuery | undefined,
    apply: (items: readonly T[], page: WindowedPage<T> | null) => void
  ): Promise<LoadOutcome<readonly T[]>> {
    if (!api) return { ok: false, errorCode: 'offline', errorMessage: 'No API client bound.' };
    const envelope = await api.request(route, { query: query ?? {} } as never);
    const result = envelope as { data: readonly T[] | null; pagination?: { offset: number; limit: number; total: number }; error?: { code: string; message: string } };
    if (result.error || result.data === null) {
      return { ok: false, errorCode: result.error?.code ?? 'internal', errorMessage: result.error?.message };
    }
    const page =
      route === 'plugins.list'
        ? null
        : pageFromResponse(canonicalQueryKey(query), result.data, result.pagination, now(), query?.limit ?? DEFAULT_LIST_LIMIT);
    apply(result.data, page);
    return { ok: true, data: result.data };
  }

  async function loadAuditPage<T>(
    slice: 'urlPages' | 'findingPages' | 'linkPages' | 'ngramPages',
    route: 'audits.listUrls' | 'audits.listFindings' | 'audits.listLinks' | 'ngrams.list',
    auditId: string,
    query: ListQuery | undefined
  ): Promise<LoadOutcome<WindowedPage<T>>> {
    if (!api) return { ok: false, errorCode: 'offline', errorMessage: 'No API client bound.' };
    const key = auditPageKey(auditId, query);
    const envelope = await api.request(route, {
      params: { id: auditId },
      query: query ?? {}
    } as never);
    const result = envelope as { data: readonly T[] | null; pagination?: { offset: number; limit: number; total: number }; error?: { code: string; message: string } };
    if (result.error || result.data === null) {
      return { ok: false, errorCode: result.error?.code ?? 'internal', errorMessage: result.error?.message };
    }
    const page = pageFromResponse<T>(key, result.data, result.pagination, now(), query?.limit ?? DEFAULT_LIST_LIMIT);
    store.setState((state) => ({
      [slice]: putPage(state[slice] as WindowedSlice<T>, page)
    }) as Partial<CrawlForgeState>);
    return { ok: true, data: page };
  }

  return {
    async loadSettings() {
      if (!api) return { ok: false, errorCode: 'offline', errorMessage: 'No API client bound.' };
      const envelope = await api.request('settings.get');
      if (envelope.error || envelope.data === null) {
        return { ok: false, errorCode: envelope.error?.code ?? 'internal', errorMessage: envelope.error?.message };
      }
      store.setState({ settings: { settings: envelope.data, loaded: true } });
      return { ok: true, data: envelope.data };
    },

    loadProjects(query) {
      return loadList<ProjectSummary>('projects.list', query, (items, page) => {
        store.setState((state) => ({
          projects: { entities: upsertEntities(state.projects.entities, items), listPage: page }
        }));
      });
    },

    loadAudits(query) {
      return loadList<AuditSummary>('audits.list', query, (items, page) => {
        store.setState((state) => ({
          audits: { ...state.audits, entities: upsertEntities(state.audits.entities, items), listPage: page }
        }));
      });
    },

    setActiveAudit(auditId) {
      store.setState((state) => ({ audits: { ...state.audits, activeAuditId: auditId } }));
    },

    loadUrlPage(auditId, query) {
      return loadAuditPage<UrlRecordSummary>('urlPages', 'audits.listUrls', auditId, query);
    },
    loadFindingPage(auditId, query) {
      return loadAuditPage<Finding>('findingPages', 'audits.listFindings', auditId, query);
    },
    loadLinkPage(auditId, query) {
      return loadAuditPage<LinkEdge>('linkPages', 'audits.listLinks', auditId, query);
    },
    loadNgramPage(auditId, query) {
      return loadAuditPage<NgramRow>('ngramPages', 'ngrams.list', auditId, query);
    },

    async loadHosts(auditId) {
      if (!api) return { ok: false, errorCode: 'offline', errorMessage: 'No API client bound.' };
      const envelope = await api.request('hosts.list', { params: { id: auditId } });
      if (envelope.error || envelope.data === null) {
        return { ok: false, errorCode: envelope.error?.code ?? 'internal', errorMessage: envelope.error?.message };
      }
      const byHostKey: Record<string, HostState> = {};
      for (const host of envelope.data) {
        byHostKey[`${auditId}:${host.host}`] = host;
      }
      store.setState({ hostThrottle: { auditId, byHostKey } });
      return { ok: true, data: envelope.data };
    },

    loadPlugins() {
      return loadList<PluginRegistryEntry>('plugins.list', undefined, (items) => {
        store.setState({ plugins: { entities: tableFromEntities(items) } });
      });
    },

    setConnectionState(connectionState) {
      store.setState((state) => ({ hub: { ...state.hub, connectionState } }));
    },

    setSubscribedAuditIds(subscribedAuditIds) {
      store.setState((state) => ({ hub: { ...state.hub, subscribedAuditIds: [...subscribedAuditIds] } }));
    },

    putWindowedPage(slice, page) {
      store.setState((state) => ({
        [slice]: putPage(state[slice] as WindowedSlice<never>, page as WindowedPage<never>)
      }) as Partial<CrawlForgeState>);
    }
  };
}

/** Convenience: re-measure the renderer heap into the system-health slice. */
export function refreshFrontendHeap(store: CrawlForgeStore): void {
  const heap = measureFrontendJsHeap();
  store.setState((state) =>
    state.systemHealth.frontendJsHeapBytes === heap
      ? state
      : { systemHealth: { ...state.systemHealth, frontendJsHeapBytes: heap } }
  );
}

/** Upserts one audit summary (demultiplexer helper, immutable). */
export function upsertAudit(store: CrawlForgeStore, audit: AuditSummary): void {
  store.setState((state) => ({
    audits: { ...state.audits, entities: upsertEntity(state.audits.entities, audit) }
  }));
}
