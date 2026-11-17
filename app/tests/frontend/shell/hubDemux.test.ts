/**
 * Hub demultiplexer tests: all 18 server events route to the right slices,
 * counters aggregate (never raw history), the activity ring stays bounded,
 * system-health bypasses the feed, host-throttle reconciles, and the facade
 * manages subscriptions + reconnect re-subscribe + cleanup.
 */

import { describe, expect, it } from 'vitest';
import { HUB_SERVER_EVENT_NAMES } from '../../../src/frontend/src/contracts/hub/events';
import type { AuditSummary } from '../../../src/frontend/src/contracts/api/audits';
import { upsertEntity } from '../../../src/frontend/src/store/entityTable';
import { demuxGraph } from './helpers';

const audit = (id: string): AuditSummary => ({
  id,
  projectId: '1',
  configurationId: '1',
  state: 'running',
  phase: 'fetching',
  startedAt: '2026-11-14T10:00:00Z',
  completedAt: null,
  counters: { queued: 100, fetched: 0, error: 0, blocked: 0 }
});

const health = {
  timestamp: '2026-11-14T12:00:00Z',
  backendWorkingSetBytes: 1_500_000_000,
  managedHeapBytes: 700_000_000,
  gcGen0Count: 12,
  gcGen1Count: 4,
  gcGen2Count: 1,
  sqliteWalBytes: 4096,
  auditId: '7'
};

describe('event coverage', () => {
  it('registers a handler for every one of the 18 server events', async () => {
    const graph = demuxGraph();
    await graph.facade.start();
    for (const name of HUB_SERVER_EVENT_NAMES) {
      expect(graph.connection.handlerCount(name)).toBe(1);
    }
    expect(HUB_SERVER_EVENT_NAMES).toHaveLength(18);
  });
});

describe('crawl lifecycle ingestion', () => {
  it('crawl-progress replaces counters and phase immutably', () => {
    const { store, demux } = demuxGraph();
    store.setState((state) => ({ audits: { ...state.audits, entities: upsertEntity(state.audits.entities, audit('7')) } }));
    const before = store.getState().audits.entities.byId['7'];

    demux.ingest({
      event: 'crawl-progress',
      payload: {
        auditId: '7',
        counters: { queued: 50, fetched: 50, error: 2, blocked: 3, wafBlocked: 1 },
        throughputPerSecond: 8.5,
        elapsedSeconds: 12,
        estimatedRemainderSeconds: 6,
        phase: 'analyzing',
        throttledHostCount: 2,
        meanEffectiveRate: 4.2
      }
    });

    const after = store.getState().audits.entities.byId['7'];
    expect(after.counters.fetched).toBe(50);
    expect(after.counters.wafBlocked).toBe(1);
    expect(after.phase).toBe('analyzing');
    expect(before.counters.fetched).toBe(0); // immutable
  });

  it('url-batch aggregates status classes without retaining rows', () => {
    const { store, live, demux } = demuxGraph();
    demux.ingest({
      event: 'url-batch',
      payload: {
        auditId: '7',
        urls: [
          { urlId: 'u1', canonicalUrl: 'https://a/1', statusClass: '2xx' },
          { urlId: 'u2', canonicalUrl: 'https://a/2', statusClass: '4xx', blockReason: 'waf', wafProvider: 'cloudflare' },
          { urlId: 'u3', canonicalUrl: 'https://a/3', statusClass: '2xx' }
        ]
      }
    });
    expect(live.getState().urlClassByAudit['7']).toEqual({ '1xx': 0, '2xx': 2, '3xx': 0, '4xx': 1, '5xx': 0 });
    // No raw URL rows anywhere in the normalized store.
    expect(JSON.stringify(store.getState())).not.toContain('https://a/1');
  });

  it('stage-changed and audit-failed transition the entity state', () => {
    const { store, demux } = demuxGraph();
    store.setState((state) => ({ audits: { ...state.audits, entities: upsertEntity(state.audits.entities, audit('7')) } }));
    demux.ingest({ event: 'stage-changed', payload: { auditId: '7', previousState: 'running', newState: 'paused' } });
    expect(store.getState().audits.entities.byId['7'].state).toBe('paused');
    demux.ingest({ event: 'audit-failed', payload: { auditId: '7', errorCode: 'timeout', message: 'deadline' } });
    expect(store.getState().audits.entities.byId['7'].state).toBe('failed');
  });

  it('audit-completed finalizes counters and fires onCrawlComplete exactly once', () => {
    const { store, bus, demux } = demuxGraph();
    store.setState((state) => ({ audits: { ...state.audits, entities: upsertEntity(state.audits.entities, audit('7')) } }));
    const completions: string[] = [];
    bus.register('plugin-a', { onCrawlComplete: (ctx) => completions.push(ctx.auditId) });

    const payload = {
      auditId: '7',
      finalCounters: { queued: 0, fetched: 100, error: 2, blocked: 1, wafBlocked: 1 },
      reportReady: true
    };
    demux.ingest({ event: 'audit-completed', payload });
    demux.ingest({ event: 'audit-completed', payload }); // duplicate delivery

    expect(completions).toEqual(['7']); // exactly once
    const entity = store.getState().audits.entities.byId['7'];
    expect(entity.state).toBe('completed');
    expect(entity.counters.fetched).toBe(100);
    expect(entity.completedAt).not.toBeNull();
  });

  it('findings-updated aggregates severity/category deltas', () => {
    const { live, demux } = demuxGraph();
    demux.ingest({ event: 'findings-updated', payload: { auditId: '7', severityDeltas: { high: 2 }, categoryDeltas: { meta: 3 } } });
    demux.ingest({ event: 'findings-updated', payload: { auditId: '7', severityDeltas: { high: 1, low: 4 }, categoryDeltas: { meta: 1 } } });
    expect(live.getState().findingDeltasByAudit['7']).toEqual({
      severity: { high: 3, low: 4 },
      category: { meta: 4 }
    });
  });
});

describe('additive event ingestion', () => {
  it('waf-blocks aggregates per provider without raw retention', () => {
    const { store, live, demux } = demuxGraph();
    demux.ingest({
      event: 'waf-blocks',
      payload: {
        auditId: '7',
        blocks: [
          { urlId: 'u1', canonicalUrl: 'https://a/1', provider: 'cloudflare', challengeClass: 'botblock', confidence: 0.9 },
          { urlId: 'u2', canonicalUrl: 'https://a/2', provider: 'cloudflare', challengeClass: 'jschallenge', confidence: 0.8 },
          { urlId: 'u3', canonicalUrl: 'https://a/3', provider: 'akamai', challengeClass: 'botblock', confidence: 0.7 }
        ]
      }
    });
    expect(live.getState().wafCountsByAudit['7']).toEqual({ cloudflare: 2, akamai: 1 });
    expect(JSON.stringify(store.getState())).not.toContain('https://a/1');
  });

  it('host-throttle reconciles AIMD state keyed by audit:host', () => {
    const { store, demux } = demuxGraph();
    demux.ingest({
      event: 'host-throttle',
      payload: {
        auditId: '7',
        host: 'example.com',
        trigger: 'ttfb-spike',
        rateBefore: 8,
        rateAfter: 4,
        concurrencyBefore: 6,
        concurrencyAfter: 3,
        ttfbSampleMs: 950,
        rollingBaselineRate: 8
      }
    });
    const host = store.getState().hostThrottle.byHostKey['7:example.com'];
    expect(host.effectiveRate).toBe(4);
    expect(host.concurrency).toBe(3);
    expect(host.throttled).toBe(true);
    expect(host.ttfbEwmaMs).toBe(950);
    expect(store.getState().hostThrottle.auditId).toBe('7');

    demux.ingest({
      event: 'host-throttle',
      payload: {
        auditId: '7', host: 'example.com', trigger: 'recovery',
        rateBefore: 4, rateAfter: 5, concurrencyBefore: 3, concurrencyAfter: 4,
        ttfbSampleMs: 200, rollingBaselineRate: 8
      }
    });
    expect(store.getState().hostThrottle.byHostKey['7:example.com'].throttled).toBe(false);
  });

  it('system-health updates the slice and never enters the activity feed', () => {
    const { store, demux } = demuxGraph();
    demux.ingest({ event: 'system-health', payload: health });
    const slice = store.getState().systemHealth;
    expect(slice.latest?.backendWorkingSetBytes).toBe(1_500_000_000);
    expect(slice.latest?.gcGen0Count).toBe(12);
    expect(slice.latest?.sqliteWalBytes).toBe(4096);
    expect(slice.lastHeartbeatAt).toBe('2026-11-14T12:00:00Z');
    expect(store.getState().hub.recentActivity.entries).toHaveLength(0);
  });

  it('system-health tolerates null WAL (external provider)', () => {
    const { store, demux } = demuxGraph();
    demux.ingest({ event: 'system-health', payload: { ...health, sqliteWalBytes: null } });
    expect(store.getState().systemHealth.latest?.sqliteWalBytes).toBeNull();
  });

  it('matrix rows aggregate; embedding and log-import progress stay latest-wins', () => {
    const { live, demux } = demuxGraph();
    demux.ingest({ event: 'matrix-row-validated', payload: { auditId: '7', rows: [{ urlId: 'u1', sourceUrl: 'https://a', verdict: 'match' }, { urlId: 'u2', sourceUrl: 'https://b', verdict: 'targetmismatch' }] } });
    demux.ingest({ event: 'matrix-row-validated', payload: { auditId: '7', rows: [{ urlId: 'u3', sourceUrl: 'https://c', verdict: 'match' }] } });
    expect(live.getState().matrixByAudit['7'].validated).toBe(3);

    demux.ingest({ event: 'embedding-progress', payload: { auditId: '7', embeddedCount: 10, totalEligible: 100, provider: 'loopback-daemon', phase: 'embedding' } });
    demux.ingest({ event: 'embedding-progress', payload: { auditId: '7', embeddedCount: 60, totalEligible: 100, provider: 'loopback-daemon', phase: 'embedding' } });
    expect(live.getState().embeddingByAudit['7'].embeddedCount).toBe(60);
    demux.ingest({ event: 'embedding-completed', payload: { auditId: '7', finalCounts: { embedded: 95, totalEligible: 100, skipped: 5 }, passes: { clusters: 4, linkOpportunities: 9, lowRelevanceLinks: 2 } } });
    expect(live.getState().embeddingByAudit['7'].isFinal).toBe(true);
    expect(live.getState().embeddingByAudit['7'].passes?.clusters).toBe(4);

    demux.ingest({ event: 'log-import-progress', payload: { importId: 'i1', counters: { linesParsed: 100, linesAccepted: 90, linesRejected: 10 }, phase: 'parsing' } });
    expect(live.getState().logImportById['i1'].linesAccepted).toBe(90);
    demux.ingest({ event: 'log-import-completed', payload: { importId: 'i1', finalCounters: { linesParsed: 200, linesAccepted: 180, linesRejected: 20 }, correlation: { orphanCount: 5, budgetWasteCount: 1, uncrawledIndexableCount: 2 } } });
    expect(live.getState().logImportById['i1'].isFinal).toBe(true);
    expect(live.getState().logImportById['i1'].linesParsed).toBe(200);
  });

  it('backend-lifecycle, schedule, and export events land in the bounded feed', () => {
    const { store, live, demux } = demuxGraph();
    demux.ingest({ event: 'backend-lifecycle', payload: { kind: 'ready' } });
    demux.ingest({ event: 'schedule-fired', payload: { scheduleId: 's1', auditId: '7', triggerType: 'scheduled' } });
    demux.ingest({ event: 'schedule-run-completed', payload: { scheduleId: 's1', auditId: '7', outcome: 'completed', completionActionResults: [] } });
    demux.ingest({ event: 'export-delivery', payload: { exportId: 'e1', destinationId: 'd1', state: 'delivered' } });
    expect(live.getState().backendLifecycle?.kind).toBe('ready');
    expect(store.getState().hub.recentActivity.entries.map((e) => e.event)).toEqual([
      'backend-lifecycle', 'schedule-fired', 'schedule-run-completed', 'export-delivery'
    ]);
  });

  it('keeps the activity ring bounded under flood', () => {
    const { store, demux } = demuxGraph();
    for (let i = 0; i < 1200; i += 1) {
      demux.ingest({ event: 'schedule-fired', payload: { scheduleId: `s${i}`, auditId: '7', triggerType: 'manual' } });
    }
    const activity = store.getState().hub.recentActivity;
    expect(activity.entries.length).toBeLessThanOrEqual(1000);
    expect(activity.droppedCount).toBe(200);
  });
});

describe('facade subscription lifecycle', () => {
  it('subscribe/unsubscribe invoke the channel and track ids', async () => {
    const { store, connection, facade } = demuxGraph();
    await facade.start();
    await facade.subscribe(['7', '8']);
    expect(connection.invocations[0]).toEqual({ message: 'subscribe', payload: { auditIds: ['7', '8'] } });
    expect(store.getState().hub.subscribedAuditIds).toEqual(['7', '8']);

    await facade.subscribe(['8', '9']); // dedupe merge
    expect(store.getState().hub.subscribedAuditIds).toEqual(['7', '8', '9']);

    await facade.unsubscribe(['8']);
    expect(connection.invocations[2]).toEqual({ message: 'unsubscribe', payload: { auditIds: ['8'] } });
    expect(store.getState().hub.subscribedAuditIds).toEqual(['7', '9']);
  });

  it('re-subscribes stored channels after reconnect', async () => {
    const { connection, facade } = demuxGraph();
    await facade.start();
    await facade.subscribe(['7']);
    connection.invocations.length = 0;
    connection.simulateReconnected();
    await Promise.resolve();
    expect(connection.invocations[0]).toEqual({ message: 'subscribe', payload: { auditIds: ['7'] } });
  });

  it('mirrors connection state and cleans up handlers on stop', async () => {
    const { store, connection, facade } = demuxGraph();
    await facade.start();
    expect(store.getState().hub.connectionState).toBe('Connected');
    await facade.stop();
    expect(store.getState().hub.connectionState).toBe('Disconnected');
    expect(store.getState().hub.subscribedAuditIds).toEqual([]);
    for (const name of HUB_SERVER_EVENT_NAMES) {
      expect(connection.handlerCount(name)).toBe(0);
    }
    expect(connection.stopCalls).toBe(1);
  });

  it('start is idempotent', async () => {
    const { connection, facade } = demuxGraph();
    await facade.start();
    await facade.start();
    expect(connection.startCalls).toBe(1);
  });
});
