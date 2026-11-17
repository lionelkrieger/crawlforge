/**
 * Plugin lifecycle hook bus + read-only bridge (contracts/plugins).
 *
 * FS-CRAWL-002 §6.5: onLoad / onTabActivate / onDataUpdate / onCrawlComplete.
 * §7.4: onDataUpdate relays are coalesced to <= 1/s and dropped under
 * backpressure (latest state wins, accounting exposed); listeners detach on
 * tab deactivation. §8.8: the normalized store is the contract surface.
 * E5: plugins receive read-only data + UI contribution rights only — the
 * bridge exposes no mutation surface, and ungranted slices are never exposed.
 *
 * Determinism (WP-06-06 consumes this): onCrawlComplete fires exactly once
 * per plugin per audit; relay flushes deliver slices in sorted order;
 * coalescing keeps only the latest update per slice / the latest hub event.
 *
 * @module store/pluginBus
 */

import type {
  PluginHookBus,
  PluginLifecycleHooks,
  PluginLoadContext,
  PluginTabContext,
  PluginCrawlCompleteContext
} from '../contracts/plugins/hooks';
import { PLUGIN_LIFECYCLE_HOOKS } from '../contracts/plugins/hooks';
import type {
  PluginBridge,
  PluginDataUpdate,
  PluginReadableState,
  PluginRelaySliceName
} from '../contracts/plugins/bridge';
import type { PluginManifest, PluginPermission } from '../contracts/api/plugins';
import type { AuditCounters } from '../contracts/api/audits';
import type { HubServerEvent } from '../contracts/hub/events';
import type { CrawlForgeState } from '../contracts/store/state';
import type { CrawlForgeStore } from '../contracts/store/selectors';
import { getStoreRevision } from './crawlForgeStore';

/** Relay flush cadence: at most one relay tick per second (§7.4). */
export const RELAY_INTERVAL_MS = 1000;

/** The relayable slice names, in deterministic (sorted) delivery order. */
export const RELAY_SLICES: readonly PluginRelaySliceName[] = [
  'audits',
  'findingPages',
  'hostThrottle',
  'linkPages',
  'ngramPages',
  'plugins',
  'projects',
  'settings',
  'systemHealth',
  'urlPages'
];

interface RelaySubscription {
  readonly pluginId: string;
  readonly listener: (update: PluginDataUpdate) => void;
  /** Per-slice change counts since the last relay (backpressure accounting). */
  readonly pendingSlices: Map<PluginRelaySliceName, number>;
  /** Latest hub event + coalesced count since the last relay. */
  pendingHub: { event: HubServerEvent; count: number } | null;
}

interface PluginRegistration {
  readonly pluginId: string;
  hooks: PluginLifecycleHooks;
  manifest: PluginManifest | null;
  bridge: PluginBridge | null;
  readonly subscriptions: Set<RelaySubscription>;
  readonly activeTabs: Set<string>;
  /** True until the first tab deactivation leaves zero active tabs. */
  relayActive: boolean;
  loadFired: boolean;
}

/** Extended bus surface consumed by the shell and the demultiplexer. */
export interface CrawlForgePluginBus extends PluginHookBus {
  /** Registers hooks and fires onLoad exactly once with the read-only bridge. */
  loadPlugin(pluginId: string, manifest: PluginManifest, hooks: PluginLifecycleHooks): () => void;
  /** Fires onTabActivate and re-activates relays for the plugin. */
  notifyTabActivate(context: PluginTabContext): void;
  /** Detaches relays when the plugin's last tab deactivates (§7.4). */
  notifyTabDeactivate(pluginId: string, tabId: string): void;
  /** Fires onCrawlComplete exactly once per plugin per audit. */
  notifyCrawlComplete(auditId: string, finalCounters: AuditCounters, reportReady: boolean): void;
  /** Queues one hub event for coalesced relay (demultiplexer entry point). */
  relayHubEvent(event: HubServerEvent): void;
  /** Flushes pending relays now (the 1 s timer calls this; tests drive it). */
  flushRelays(): void;
  /** Starts the 1 s relay timer. */
  start(): void;
  /** Stops the timer and detaches every registration and subscription. */
  dispose(): void;
  /** Creates (or reuses) the read-only bridge for a plugin. */
  getBridge(pluginId: string, permissions: readonly PluginPermission[]): PluginBridge;
}

/**
 * Creates the plugin hook bus bound to the normalized store. Slice changes
 * are detected by reference comparison (immutable updates, §8.8).
 */
export function createPluginHookBus(store: CrawlForgeStore): CrawlForgePluginBus {
  const registrations = new Map<string, PluginRegistration>();
  const completedAudits = new Set<string>();
  let timer: ReturnType<typeof setInterval> | null = null;
  let disposed = false;
  let previousState: CrawlForgeState = store.getState();

  function registrationFor(pluginId: string): PluginRegistration {
    let registration = registrations.get(pluginId);
    if (!registration) {
      registration = {
        pluginId,
        hooks: {},
        manifest: null,
        bridge: null,
        subscriptions: new Set(),
        activeTabs: new Set(),
        relayActive: true,
        loadFired: false
      };
      registrations.set(pluginId, registration);
    }
    return registration;
  }

  function freezeSnapshot(state: CrawlForgeState): PluginReadableState {
    const snapshot: Record<string, unknown> = {};
    for (const slice of RELAY_SLICES) {
      snapshot[slice] = Object.freeze({ ...state[slice] });
    }
    return Object.freeze(snapshot) as PluginReadableState;
  }

  function buildBridge(pluginId: string, permissions: readonly PluginPermission[]): PluginBridge {
    const canRead = permissions.includes('read-data');
    const registration = registrationFor(pluginId);
    const bridge: PluginBridge = {
      pluginId,
      permissions: [...permissions],
      getState(): PluginReadableState {
        if (!canRead) return Object.freeze({}) as PluginReadableState;
        return freezeSnapshot(store.getState());
      },
      getSlice<K extends PluginRelaySliceName>(slice: K): PluginReadableState[K] {
        if (!canRead) {
          throw new Error(`Plugin "${pluginId}" lacks the read-data permission.`);
        }
        return Object.freeze({ ...store.getState()[slice] }) as PluginReadableState[K];
      },
      subscribe(listener: (update: PluginDataUpdate) => void): () => void {
        if (!canRead || disposed) return () => undefined;
        const subscription: RelaySubscription = {
          pluginId,
          listener,
          pendingSlices: new Map(),
          pendingHub: null
        };
        registration.subscriptions.add(subscription);
        return () => {
          registration.subscriptions.delete(subscription);
        };
      }
    };
    registration.bridge = bridge;
    return bridge;
  }

  // Slice-change detection: reference comparison across relayable slices.
  const unsubscribeStore = store.subscribe((state) => {
    const changed: PluginRelaySliceName[] = [];
    for (const slice of RELAY_SLICES) {
      if (state[slice] !== previousState[slice]) changed.push(slice);
    }
    previousState = state;
    if (changed.length === 0) return;
    for (const registration of registrations.values()) {
      if (!registration.relayActive) continue;
      for (const subscription of registration.subscriptions) {
        for (const slice of changed) {
          subscription.pendingSlices.set(slice, (subscription.pendingSlices.get(slice) ?? 0) + 1);
        }
      }
    }
  });

  function flushRelays(): void {
    if (disposed) return;
    const revision = getStoreRevision(store);
    for (const registration of registrations.values()) {
      if (!registration.relayActive) continue;
      for (const subscription of registration.subscriptions) {
        // Deterministic order: sorted slice names, then the hub event.
        const slices = [...subscription.pendingSlices.keys()].sort();
        for (const slice of slices) {
          const changes = subscription.pendingSlices.get(slice) ?? 1;
          subscription.pendingSlices.delete(slice);
          const update: PluginDataUpdate = {
            kind: 'slice',
            slice,
            revision,
            droppedSinceLastRelay: Math.max(0, changes - 1)
          };
          safeDeliver(subscription, update);
        }
        if (subscription.pendingHub) {
          const { event, count } = subscription.pendingHub;
          subscription.pendingHub = null;
          safeDeliver(subscription, { kind: 'hub-event', event, coalescedCount: count });
        }
      }
    }
  }

  function safeDeliver(subscription: RelaySubscription, update: PluginDataUpdate): void {
    try {
      subscription.listener(update);
    } catch {
      // A misbehaving plugin handler must never break the bus (E5 isolation).
    }
  }

  const bus: CrawlForgePluginBus = {
    supportedHooks: PLUGIN_LIFECYCLE_HOOKS,

    register(pluginId: string, hooks: PluginLifecycleHooks): () => void {
      const registration = registrationFor(pluginId);
      registration.hooks = hooks;
      return () => bus.unregister(pluginId);
    },

    unregister(pluginId: string): void {
      const registration = registrations.get(pluginId);
      if (!registration) return;
      registration.subscriptions.clear();
      registration.activeTabs.clear();
      registrations.delete(pluginId);
    },

    loadPlugin(pluginId, manifest, hooks): () => void {
      const registration = registrationFor(pluginId);
      registration.hooks = hooks;
      registration.manifest = manifest;
      const bridge = buildBridge(pluginId, manifest.permissions);
      if (!registration.loadFired) {
        registration.loadFired = true;
        const context: PluginLoadContext = { pluginId, manifest, bridge };
        try {
          void hooks.onLoad?.(context);
        } catch {
          // onLoad failures are isolated; the plugin stays registered.
        }
      }
      return () => bus.unregister(pluginId);
    },

    notifyTabActivate(context: PluginTabContext): void {
      const registration = registrations.get(context.pluginId);
      if (!registration) return;
      registration.activeTabs.add(context.tabId);
      registration.relayActive = true;
      try {
        registration.hooks.onTabActivate?.(context);
      } catch {
        // Isolated.
      }
    },

    notifyTabDeactivate(pluginId: string, tabId: string): void {
      const registration = registrations.get(pluginId);
      if (!registration) return;
      registration.activeTabs.delete(tabId);
      if (registration.activeTabs.size === 0) {
        // Detach listeners on tab deactivation (§7.4): drop pending relays.
        registration.relayActive = false;
        for (const subscription of registration.subscriptions) {
          subscription.pendingSlices.clear();
          subscription.pendingHub = null;
        }
      }
    },

    notifyCrawlComplete(auditId, finalCounters, reportReady): void {
      for (const registration of registrations.values()) {
        const key = `${registration.pluginId}:${auditId}`;
        if (completedAudits.has(key)) continue;
        completedAudits.add(key);
        const hook = registration.hooks.onCrawlComplete;
        if (!hook) continue;
        const context: PluginCrawlCompleteContext = {
          pluginId: registration.pluginId,
          auditId,
          finalCounters,
          reportReady
        };
        try {
          hook(context);
        } catch {
          // Isolated.
        }
      }
    },

    relayHubEvent(event: HubServerEvent): void {
      if (disposed) return;
      for (const registration of registrations.values()) {
        if (!registration.relayActive) continue;
        for (const subscription of registration.subscriptions) {
          if (subscription.pendingHub) {
            subscription.pendingHub = { event, count: subscription.pendingHub.count + 1 };
          } else {
            subscription.pendingHub = { event, count: 1 };
          }
        }
      }
    },

    flushRelays,

    start(): void {
      if (timer !== null || disposed) return;
      timer = setInterval(flushRelays, RELAY_INTERVAL_MS);
    },

    dispose(): void {
      disposed = true;
      if (timer !== null) {
        clearInterval(timer);
        timer = null;
      }
      unsubscribeStore();
      registrations.clear();
      completedAudits.clear();
    },

    getBridge(pluginId, permissions): PluginBridge {
      const registration = registrations.get(pluginId);
      if (registration?.bridge) return registration.bridge;
      return buildBridge(pluginId, permissions);
    }
  };

  return bus;
}
