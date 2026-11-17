/**
 * Plugin hook bus + bridge tests (FS-CRAWL-002 §6.5/§7.4, E5): onLoad once,
 * onTabActivate, coalesced <=1/s relays with backpressure accounting,
 * detach on tab deactivation, exactly-once onCrawlComplete, read-only
 * snapshots, and permission gating.
 */

import { describe, expect, it } from 'vitest';
import type { PluginManifest } from '../../../src/frontend/src/contracts/api/plugins';
import type { PluginDataUpdate } from '../../../src/frontend/src/contracts/plugins/bridge';
import { PLUGIN_LIFECYCLE_HOOKS } from '../../../src/frontend/src/contracts/plugins/hooks';
import { RELAY_INTERVAL_MS } from '../../../src/frontend/src/store/pluginBus';
import { demuxGraph } from './helpers';

const manifest = (id: string, permissions: PluginManifest['permissions'] = ['read-data']): PluginManifest => ({
  id,
  name: `Plugin ${id}`,
  version: '1.0.0',
  permissions
});

describe('lifecycle hooks', () => {
  it('supports exactly the four contract hooks', () => {
    const { bus } = demuxGraph();
    expect(bus.supportedHooks).toEqual(PLUGIN_LIFECYCLE_HOOKS);
    expect(bus.supportedHooks).toEqual(['onLoad', 'onTabActivate', 'onDataUpdate', 'onCrawlComplete']);
  });

  it('fires onLoad exactly once with the read-only bridge', () => {
    const { bus } = demuxGraph();
    const loads: string[] = [];
    const hooks = { onLoad: (ctx: { pluginId: string }) => { loads.push(ctx.pluginId); } };
    bus.loadPlugin('p1', manifest('p1'), hooks);
    bus.loadPlugin('p1', manifest('p1'), hooks); // re-load does not re-fire
    expect(loads).toEqual(['p1']);
  });

  it('fires onTabActivate with surface and active audit', () => {
    const { bus } = demuxGraph();
    const activations: string[] = [];
    bus.register('p1', { onTabActivate: (ctx) => activations.push(`${ctx.tabId}:${ctx.surface}:${ctx.activeAuditId}`) });
    bus.notifyTabActivate({ pluginId: 'p1', tabId: 't1', surface: 'report', activeAuditId: '7' });
    expect(activations).toEqual(['t1:report:7']);
  });

  it('fires onCrawlComplete exactly once per plugin per audit', () => {
    const { bus } = demuxGraph();
    const seen: string[] = [];
    bus.register('p1', { onCrawlComplete: (ctx) => seen.push(`${ctx.pluginId}:${ctx.auditId}:${ctx.finalCounters.fetched}`) });
    bus.register('p2', { onCrawlComplete: (ctx) => seen.push(`${ctx.pluginId}:${ctx.auditId}`) });

    const counters = { queued: 0, fetched: 10, error: 0, blocked: 0 };
    bus.notifyCrawlComplete('7', counters, true);
    bus.notifyCrawlComplete('7', counters, true);
    bus.notifyCrawlComplete('8', counters, false);

    expect(seen).toEqual(['p1:7:10', 'p2:7', 'p1:8:10', 'p2:8']);
  });

  it('isolates throwing handlers from the bus', () => {
    const { bus } = demuxGraph();
    const seen: string[] = [];
    bus.register('bad', { onCrawlComplete: () => { throw new Error('plugin bug'); } });
    bus.register('good', { onCrawlComplete: () => seen.push('good') });
    expect(() => bus.notifyCrawlComplete('7', { queued: 0, fetched: 1, error: 0, blocked: 0 }, true)).not.toThrow();
    expect(seen).toEqual(['good']);
  });
});

describe('coalesced relays (<=1/s, backpressure accounting)', () => {
  it('coalesces intermediate slice updates into one relay with drop counts', () => {
    const { bus, actions } = demuxGraph();
    const updates: PluginDataUpdate[] = [];
    bus.loadPlugin('p1', manifest('p1'), {});
    bus.getBridge('p1', ['read-data']).subscribe((update) => updates.push(update));

    actions.setConnectionState('Connecting'); // hub slice is NOT relayable
    actions.setActiveAudit('1');
    actions.setActiveAudit('2');
    actions.setActiveAudit('3'); // three audits-slice changes before flush

    bus.flushRelays();
    const sliceUpdates = updates.filter((u) => u.kind === 'slice');
    expect(sliceUpdates).toHaveLength(1);
    expect(sliceUpdates[0]).toMatchObject({ kind: 'slice', slice: 'audits', droppedSinceLastRelay: 2 });
    expect(sliceUpdates.map((u) => (u.kind === 'slice' ? u.slice : ''))).not.toContain('hub');
  });

  it('delivers slices in deterministic sorted order', () => {
    const { bus, actions } = demuxGraph();
    const order: string[] = [];
    bus.loadPlugin('p1', manifest('p1'), {});
    bus.getBridge('p1', ['read-data']).subscribe((update) => {
      if (update.kind === 'slice') order.push(update.slice);
    });
    actions.setActiveAudit('1'); // audits
    actions.setConnectionState('Connected'); // hub (not relayed)
    bus.flushRelays();
    expect(order).toEqual(['audits']);
  });

  it('coalesces hub events to the latest with a coalesced count', () => {
    const { bus, demux } = demuxGraph();
    const updates: PluginDataUpdate[] = [];
    bus.loadPlugin('p1', manifest('p1'), {});
    bus.getBridge('p1', ['read-data']).subscribe((update) => updates.push(update));

    for (let i = 0; i < 5; i += 1) {
      demux.ingest({
        event: 'crawl-progress',
        payload: {
          auditId: '7',
          counters: { queued: 0, fetched: i, error: 0, blocked: 0 },
          throughputPerSecond: 1,
          elapsedSeconds: i,
          estimatedRemainderSeconds: null,
          phase: 'fetching'
        }
      });
    }
    bus.flushRelays();
    const hubUpdates = updates.filter((u) => u.kind === 'hub-event');
    expect(hubUpdates).toHaveLength(1);
    const relay = hubUpdates[0] as Extract<PluginDataUpdate, { kind: 'hub-event' }>;
    expect(relay.coalescedCount).toBe(5);
    expect(relay.event.event).toBe('crawl-progress');
    expect((relay.event.payload as { counters: { fetched: number } }).counters.fetched).toBe(4); // latest wins
  });

  it('uses a 1-second relay cadence constant', () => {
    expect(RELAY_INTERVAL_MS).toBe(1000);
  });

  it('detaches relays when the last tab deactivates', () => {
    const { bus, actions } = demuxGraph();
    const updates: PluginDataUpdate[] = [];
    bus.loadPlugin('p1', manifest('p1'), {});
    bus.getBridge('p1', ['read-data']).subscribe((update) => updates.push(update));
    bus.notifyTabActivate({ pluginId: 'p1', tabId: 't1', surface: 'report', activeAuditId: null });

    actions.setActiveAudit('1');
    bus.flushRelays();
    expect(updates.length).toBeGreaterThan(0);

    bus.notifyTabDeactivate('p1', 't1');
    const countAfterDetach = updates.length;
    actions.setActiveAudit('2');
    bus.flushRelays();
    expect(updates.length).toBe(countAfterDetach); // detached: no relays

    bus.notifyTabActivate({ pluginId: 'p1', tabId: 't2', surface: 'report', activeAuditId: null });
    actions.setActiveAudit('3');
    bus.flushRelays();
    expect(updates.length).toBeGreaterThan(countAfterDetach); // re-activated
  });

  it('unregister removes all listeners', () => {
    const { bus, actions } = demuxGraph();
    const updates: PluginDataUpdate[] = [];
    const dispose = bus.loadPlugin('p1', manifest('p1'), {});
    bus.getBridge('p1', ['read-data']).subscribe((update) => updates.push(update));
    dispose();
    actions.setActiveAudit('1');
    bus.flushRelays();
    expect(updates).toHaveLength(0);
  });
});

describe('read-only bridge (E5)', () => {
  it('exposes frozen snapshots; mutation throws in strict mode', () => {
    const { store, bus, actions } = demuxGraph();
    actions.setActiveAudit('1');
    bus.loadPlugin('p1', manifest('p1'), {});
    const bridge = bus.getBridge('p1', ['read-data']);
    const snapshot = bridge.getState();
    expect(Object.isFrozen(snapshot)).toBe(true);
    expect(Object.isFrozen(snapshot.audits)).toBe(true);
    expect(() => {
      (snapshot as { audits: unknown }).audits = null;
    }).toThrow();
    const slice = bridge.getSlice('audits');
    expect(Object.isFrozen(slice)).toBe(true);
    expect(slice.activeAuditId).toBe('1');
    expect(store.getState().audits.activeAuditId).toBe('1');
  });

  it('never exposes slices without the read-data permission', () => {
    const { bus } = demuxGraph();
    bus.loadPlugin('p1', manifest('p1', ['ui-contributions']), {});
    const bridge = bus.getBridge('p1', ['ui-contributions']);
    expect(Object.keys(bridge.getState())).toHaveLength(0);
    expect(() => bridge.getSlice('settings')).toThrow(/read-data/);
    const updates: PluginDataUpdate[] = [];
    const dispose = bridge.subscribe((update) => updates.push(update));
    bus.flushRelays();
    expect(updates).toHaveLength(0);
    dispose();
  });

  it('exposes no mutation surface', () => {
    const { bus } = demuxGraph();
    const bridge = bus.getBridge('p1', ['read-data']);
    expect(Object.keys(bridge).sort()).toEqual(['getSlice', 'getState', 'permissions', 'pluginId', 'subscribe']);
    expect('setState' in bridge).toBe(false);
  });
});
