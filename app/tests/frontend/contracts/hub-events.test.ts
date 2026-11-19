/**
 * Hub event contract tests: all 21 event contracts (11 baseline rows incl.
 * the subscribe/unsubscribe channel + 10 additive: Appendix C's 8 plus
 * CR-027 local-notification and G-I export-progress), discriminated-union
 * exhaustiveness, payload guards, and additive payload fields
 * (FS-CRAWL-001 §10.3; FS-CRAWL-002 §7.2, Appendix C).
 */

import { describe, expect, it } from 'vitest';
import {
  HUB_CLIENT_MESSAGE_NAMES,
  HUB_EVENT_CATALOG,
  HUB_SERVER_EVENT_NAMES,
  assertNeverHubEvent,
  isHubClientMessage,
  isHubEvent,
  isHubServerEvent,
  isHubServerEventName,
  isSystemHealthSnapshot
} from '../../../src/frontend/src/contracts';
import type {
  AssertCatalogExhaustive,
  AssertClientNamesExhaustive,
  AssertServerNamesExhaustive,
  CrawlProgressPayload,
  HubEvent,
  HubServerEvent,
  HubServerEventName,
  HubServerPayloadOf,
  SystemHealthSnapshot,
  UrlBatchEntry
} from '../../../src/frontend/src/contracts';
import {
  Equal,
  Expect,
  baseCounters,
  clientMessageFixtures,
  healthSnapshot,
  hubPayloadFixtures,
  wafCounters
} from './helpers';

const catalogNames = Object.keys(HUB_EVENT_CATALOG) as (keyof typeof HUB_EVENT_CATALOG)[];

/** Exhaustive switch over the union — the default arm is a compile error
 * unless every member was handled (assertNeverHubEvent takes `never`). */
function classify(event: HubServerEvent): 'baseline' | 'additive' {
  switch (event.event) {
    case 'crawl-progress':
    case 'url-batch':
    case 'stage-changed':
    case 'findings-updated':
    case 'audit-completed':
    case 'audit-failed':
    case 'backend-lifecycle':
    case 'schedule-fired':
    case 'schedule-run-completed':
    case 'export-delivery':
      return 'baseline';
    case 'waf-blocks':
    case 'host-throttle':
    case 'system-health':
    case 'matrix-row-validated':
    case 'log-import-progress':
    case 'log-import-completed':
    case 'embedding-progress':
    case 'embedding-completed':
    case 'local-notification':
    case 'export-progress':
      return 'additive';
    default:
      return assertNeverHubEvent(event);
  }
}

describe('hub event catalog (21 contracts / 22 wire names)', () => {
  it('lists exactly 20 server events and 2 client channel messages', () => {
    expect(HUB_SERVER_EVENT_NAMES).toHaveLength(20);
    expect(HUB_CLIENT_MESSAGE_NAMES).toHaveLength(2);
    expect(catalogNames).toHaveLength(22);
  });

  it('counts 11 baseline contracts (10 events + 1 channel) and 10 additive', () => {
    const baselineServer = catalogNames.filter(
      (name) => HUB_EVENT_CATALOG[name].direction === 'server' && !HUB_EVENT_CATALOG[name].additive
    );
    const additiveServer = catalogNames.filter(
      (name) => HUB_EVENT_CATALOG[name].direction === 'server' && HUB_EVENT_CATALOG[name].additive
    );
    const clientChannel = catalogNames.filter(
      (name) => HUB_EVENT_CATALOG[name].direction === 'client'
    );
    expect(baselineServer).toHaveLength(10);
    expect(additiveServer).toHaveLength(10);
    // subscribe/unsubscribe are two messages of ONE channel contract.
    expect(clientChannel).toHaveLength(2);
    expect(baselineServer.length + 1 + additiveServer.length).toBe(21);
  });

  it('names the 10 additive events exactly (Appendix C 8 + CR-027 + G-I)', () => {
    const additive = catalogNames
      .filter((name) => HUB_EVENT_CATALOG[name].additive)
      .sort();
    expect(additive).toEqual(
      [
        'embedding-completed',
        'embedding-progress',
        'export-progress',
        'host-throttle',
        'local-notification',
        'log-import-completed',
        'log-import-progress',
        'matrix-row-validated',
        'system-health',
        'waf-blocks'
      ].sort()
    );
  });

  it('keeps catalog, name lists, and unions locked together (compile time)', () => {
    type Checks = [
      Expect<Equal<AssertServerNamesExhaustive, true>>,
      Expect<Equal<AssertClientNamesExhaustive, true>>,
      Expect<Equal<AssertCatalogExhaustive, true>>,
      Expect<Equal<(typeof HUB_SERVER_EVENT_NAMES)[number], HubServerEventName>>
    ];
    const checks: Checks = [true, true, true, true];
    expect(checks.every(Boolean)).toBe(true);
    // Runtime cross-check: catalog server names == name list.
    const serverFromCatalog = catalogNames
      .filter((name) => HUB_EVENT_CATALOG[name].direction === 'server')
      .sort();
    expect(serverFromCatalog).toEqual([...HUB_SERVER_EVENT_NAMES].sort());
  });
});

describe('hub payload guards', () => {
  it('accepts every valid server event (fixture per union member)', () => {
    for (const name of HUB_SERVER_EVENT_NAMES) {
      const wire = { event: name, payload: hubPayloadFixtures[name] };
      expect(isHubServerEvent(wire), `event ${name}`).toBe(true);
      expect(isHubEvent(wire), `event ${name}`).toBe(true);
    }
  });

  it('rejects unknown event names and malformed shapes', () => {
    expect(isHubServerEvent({ event: 'not-an-event', payload: {} })).toBe(false);
    expect(isHubServerEvent({ event: 'crawl-progress' })).toBe(false);
    expect(isHubServerEvent({ event: 'crawl-progress', payload: null })).toBe(false);
    expect(isHubServerEvent(null)).toBe(false);
    expect(isHubServerEvent('crawl-progress')).toBe(false);
    expect(isHubServerEventName('system-health')).toBe(true);
    expect(isHubServerEventName('subscribe')).toBe(false);
  });

  it('rejects payloads missing required fields', () => {
    const { counters: _counters, ...withoutCounters } = hubPayloadFixtures['crawl-progress'];
    expect(isHubServerEvent({ event: 'crawl-progress', payload: withoutCounters })).toBe(false);
    expect(
      isHubServerEvent({
        event: 'host-throttle',
        payload: { ...hubPayloadFixtures['host-throttle'], rateAfter: 'fast' }
      })
    ).toBe(false);
    expect(
      isHubServerEvent({
        event: 'embedding-progress',
        payload: { ...hubPayloadFixtures['embedding-progress'], provider: 'remote-api' }
      })
    ).toBe(false);
    expect(
      isHubServerEvent({
        event: 'export-delivery',
        payload: { ...hubPayloadFixtures['export-delivery'], state: 'pending' }
      })
    ).toBe(false);
    // CR-027 local-notification: both members required strings.
    expect(
      isHubServerEvent({
        event: 'local-notification',
        payload: { title: 'Audit completed' }
      })
    ).toBe(false);
    // G-I export-progress: frozen job-state vocabulary; 'delivering' is
    // ledger-only (WP-09-10 G-F) and must be rejected on the hub.
    expect(
      isHubServerEvent({
        event: 'export-progress',
        payload: { ...hubPayloadFixtures['export-progress'], state: 'delivering' }
      })
    ).toBe(false);
    expect(
      isHubServerEvent({
        event: 'export-progress',
        payload: { ...hubPayloadFixtures['export-progress'], exportId: 90 }
      })
    ).toBe(false);
  });

  it('accepts the subscribe/unsubscribe channel and rejects it as server events', () => {
    for (const message of clientMessageFixtures) {
      expect(isHubClientMessage(message)).toBe(true);
      expect(isHubEvent(message)).toBe(true);
      expect(isHubServerEvent(message)).toBe(false);
    }
    expect(isHubClientMessage({ event: 'subscribe', payload: { auditIds: 'audit-1' } })).toBe(false);
    expect(isHubClientMessage({ event: 'subscribe', payload: {} })).toBe(false);
  });

  it('classifies all 20 events through the exhaustive switch', () => {
    const events: HubServerEvent[] = HUB_SERVER_EVENT_NAMES.map((name) => ({
      event: name,
      payload: hubPayloadFixtures[name]
    })) as HubServerEvent[];
    const baseline = events.filter((event) => classify(event) === 'baseline');
    const additive = events.filter((event) => classify(event) === 'additive');
    expect(baseline).toHaveLength(10);
    expect(additive).toHaveLength(10);
  });
});

describe('additive payload fields (Appendix C)', () => {
  it('crawl-progress accepts wafBlocked, throttled hosts, mean rate, matrix counters', () => {
    const payload: CrawlProgressPayload = {
      ...hubPayloadFixtures['crawl-progress'],
      wafBlocked: 4,
      throttledHostCount: 2,
      meanEffectiveRate: 5.25,
      matrixValidated: 10,
      matrixTotal: 25
    };
    expect(isHubServerEvent({ event: 'crawl-progress', payload })).toBe(true);
    // Baseline payload without additive fields stays valid (additive-only evolution).
    expect(isHubServerEvent({ event: 'crawl-progress', payload: hubPayloadFixtures['crawl-progress'] })).toBe(true);
  });

  it('url-batch accepts optional block reason/provider on blocked entries', () => {
    const blocked: UrlBatchEntry = {
      urlId: 'url-9',
      canonicalUrl: 'https://target.test/blocked',
      statusClass: '2xx',
      blockReason: 'waf-challenge',
      wafProvider: 'example-waf'
    };
    const payload = { auditId: 'audit-1', urls: [blocked] };
    expect(isHubServerEvent({ event: 'url-batch', payload })).toBe(true);
  });

  it('audit-completed final counters accept the WAF sub-count', () => {
    const payload = {
      auditId: 'audit-1',
      finalCounters: wafCounters,
      reportReady: true
    };
    expect(isHubServerEvent({ event: 'audit-completed', payload })).toBe(true);
    expect(wafCounters.wafBlocked).toBe(4);
    expect(baseCounters.wafBlocked).toBeUndefined();
  });

  it('system-health payload is identical to GET /api/system/health', () => {
    type Checks = [
      Expect<Equal<HubServerPayloadOf<'system-health'>, SystemHealthSnapshot>>,
      Expect<Equal<HubServerPayloadOf<'system-health'>, HubServerPayloadOf<'system-health'>>>
    ];
    const checks: Checks = [true, true];
    expect(checks.every(Boolean)).toBe(true);
    // WAL is null when the external provider is active; auditId optional.
    const externalProvider: SystemHealthSnapshot = {
      ...healthSnapshot,
      sqliteWalBytes: null,
      auditId: undefined
    };
    expect(isSystemHealthSnapshot(externalProvider)).toBe(true);
    expect(isHubServerEvent({ event: 'system-health', payload: externalProvider })).toBe(true);
    expect(isSystemHealthSnapshot({ ...healthSnapshot, managedHeapBytes: 'big' })).toBe(false);
  });

  it('keeps parent emission contracts unchanged (subscribe payload shape)', () => {
    type Checks = [
      Expect<Equal<HubEvent['event'], HubServerEventName | 'subscribe' | 'unsubscribe'>>
    ];
    const checks: Checks = [true];
    expect(checks.every(Boolean)).toBe(true);
  });
});
