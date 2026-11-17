/**
 * Plugin contract tests: the four lifecycle hooks (onLoad / onTabActivate /
 * onDataUpdate / onCrawlComplete), the hook bus, and the read-only bridge
 * (FS-CRAWL-002 §6.4, §6.5, §8.8; clean-room E5: read-only data + UI
 * contribution rights only, never crawl lifecycle or configuration mutation).
 */

import { describe, expect, it } from 'vitest';
import { PLUGIN_LIFECYCLE_HOOKS } from '../../../src/frontend/src/contracts';
import type {
  AssertHookNamesExhaustive,
  AuditCounters,
  PluginBridge,
  PluginCrawlCompleteContext,
  PluginDataUpdate,
  PluginHookBus,
  PluginLifecycleHooks,
  PluginReadableState,
  PluginRelaySliceName
} from '../../../src/frontend/src/contracts';
import { Equal, Expect, emptyCrawlForgeState } from './helpers';

declare const typedBridge: PluginBridge;
declare const typedReadable: PluginReadableState;

/** Read-only snapshot of the relayable slices (mutable fixture is assignable). */
const readable: PluginReadableState = {
  settings: emptyCrawlForgeState.settings,
  projects: emptyCrawlForgeState.projects,
  audits: emptyCrawlForgeState.audits,
  urlPages: emptyCrawlForgeState.urlPages,
  findingPages: emptyCrawlForgeState.findingPages,
  linkPages: emptyCrawlForgeState.linkPages,
  ngramPages: emptyCrawlForgeState.ngramPages,
  hostThrottle: emptyCrawlForgeState.hostThrottle,
  systemHealth: emptyCrawlForgeState.systemHealth,
  plugins: emptyCrawlForgeState.plugins
};

/** Test-local bridge implementation proving the contract is implementable. */
function createTestBridge(): { bridge: PluginBridge; emit: (update: PluginDataUpdate) => void } {
  const listeners = new Set<(update: PluginDataUpdate) => void>();
  const bridge: PluginBridge = {
    pluginId: 'plugin-1',
    permissions: ['read-data', 'ui-contributions'],
    getState: () => readable,
    getSlice: (slice) => readable[slice],
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    }
  };
  return { bridge, emit: (update) => listeners.forEach((listener) => listener(update)) };
}

/** Test-local hook bus implementation with a dispatch side for tests. */
function createTestBus(): { bus: PluginHookBus; dispatch: (update: PluginDataUpdate) => void } {
  const registered = new Map<string, PluginLifecycleHooks>();
  const bus: PluginHookBus = {
    supportedHooks: PLUGIN_LIFECYCLE_HOOKS,
    register(pluginId, hooks) {
      registered.set(pluginId, hooks);
      return () => {
        registered.delete(pluginId);
      };
    },
    unregister(pluginId) {
      registered.delete(pluginId);
    }
  };
  return {
    bus,
    dispatch(update) {
      for (const hooks of registered.values()) {
        hooks.onDataUpdate?.(update);
      }
    }
  };
}

describe('plugin lifecycle hooks (contract lock)', () => {
  it('defines exactly the four hooks', () => {
    expect([...PLUGIN_LIFECYCLE_HOOKS]).toEqual([
      'onLoad',
      'onTabActivate',
      'onDataUpdate',
      'onCrawlComplete'
    ]);
    type Checks = [
      Expect<Equal<AssertHookNamesExhaustive, true>>,
      Expect<Equal<keyof PluginLifecycleHooks, 'onLoad' | 'onTabActivate' | 'onDataUpdate' | 'onCrawlComplete'>>
    ];
    const checks: Checks = [true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('accepts partial hook implementations (all hooks optional)', () => {
    const hooks: PluginLifecycleHooks = {
      onLoad(context) {
        expect(context.bridge.pluginId).toBe('plugin-1');
      }
    };
    expect(typeof hooks.onLoad).toBe('function');
    expect(hooks.onTabActivate).toBeUndefined();
  });

  it('relays coalesced updates through the bus and bridge', () => {
    const { bus, dispatch } = createTestBus();
    const { bridge, emit } = createTestBridge();
    const bridgeReceived: PluginDataUpdate[] = [];
    const hookReceived: PluginDataUpdate[] = [];

    const disposeBridge = bridge.subscribe((update) => bridgeReceived.push(update));
    const disposeBus = bus.register('plugin-1', {
      onDataUpdate(update) {
        hookReceived.push(update);
      }
    });

    const sliceUpdate: PluginDataUpdate = {
      kind: 'slice',
      slice: 'audits',
      revision: 7,
      droppedSinceLastRelay: 2
    };
    // Bridge relays and hook-bus dispatch are independent delivery paths.
    emit(sliceUpdate);
    dispatch(sliceUpdate);
    expect(bridgeReceived).toHaveLength(1);
    expect(hookReceived).toHaveLength(1);
    expect(bridgeReceived[0]).toEqual(sliceUpdate);

    // Listeners detach on tab deactivation (§7.4).
    disposeBridge();
    disposeBus();
    emit(sliceUpdate);
    dispatch(sliceUpdate);
    expect(bridgeReceived).toHaveLength(1);
    expect(hookReceived).toHaveLength(1);
  });

  it('carries hub events as the second update kind', () => {
    const update: PluginDataUpdate = {
      kind: 'hub-event',
      event: { event: 'audit-completed', payload: { auditId: 'audit-1', finalCounters: { queued: 0, fetched: 10, error: 0, blocked: 0 }, reportReady: true } },
      coalescedCount: 3
    };
    type Checks = [Expect<Equal<PluginDataUpdate['kind'], 'slice' | 'hub-event'>>];
    const checks: Checks = [true];
    expect(checks.every(Boolean)).toBe(true);
    expect(update.kind).toBe('hub-event');
  });

  it('onCrawlComplete mirrors the audit-completed payload', () => {
    type Checks = [
      Expect<Equal<PluginCrawlCompleteContext['finalCounters'], AuditCounters>>,
      Expect<Equal<PluginCrawlCompleteContext['reportReady'], boolean>>
    ];
    const checks: Checks = [true, true];
    expect(checks.every(Boolean)).toBe(true);
  });
});

describe('read-only bridge (E5)', () => {
  it('relays exactly the ten data slices — never the hub connection slice', () => {
    type Checks = [
      Expect<
        Equal<
          PluginRelaySliceName,
          | 'settings'
          | 'projects'
          | 'audits'
          | 'urlPages'
          | 'findingPages'
          | 'linkPages'
          | 'ngramPages'
          | 'hostThrottle'
          | 'systemHealth'
          | 'plugins'
        >
      >
    ];
    const checks: Checks = [true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('exposes only the read-only surface', () => {
    type Checks = [
      Expect<Equal<keyof PluginBridge, 'pluginId' | 'permissions' | 'getState' | 'getSlice' | 'subscribe'>>
    ];
    const checks: Checks = [true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('has no mutation surface (compile-time negatives)', () => {
    // Compile-time negatives only: never invoked, so the ambient
    // declarations are never evaluated at runtime.
    const negativeAccess = (): void => {
      // @ts-expect-error — the bridge exposes no setState (E5: no store mutation).
      typedBridge.setState({});
      // @ts-expect-error — the bridge exposes no API client (E5: no crawl/config mutation).
      typedBridge.api;
      // @ts-expect-error — relayed slices are deeply readonly.
      typedReadable.settings.loaded = false;
      // @ts-expect-error — entity tables inside relays are readonly too.
      typedReadable.projects.entities.allIds = [];
    };
    expect(typeof negativeAccess).toBe('function');
  });

  it('serves snapshots and per-slice reads at runtime', () => {
    const { bridge } = createTestBridge();
    expect(bridge.getState()).toBe(readable);
    expect(bridge.getSlice('systemHealth')).toBe(readable.systemHealth);
    expect(bridge.getSlice('audits')).toBe(readable.audits);
    expect(bridge.permissions).toContain('read-data');
  });
});
