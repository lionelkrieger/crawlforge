/**
 * Shared test helpers for the contracts suites: type-level assertion
 * utilities and fully-typed wire fixtures. Fixtures are typed against the
 * contract unions, so any contract drift fails `tsc --noEmit` here too.
 */

import type {
  ApiEnvelope,
  AuditCounters,
  CrawlForgeState,
  HubClientMessage,
  HubServerEventName,
  HubServerPayloadOf,
  LogCorrelationSummary,
  LogImportCounters,
  SystemHealthSnapshot
} from '../../../src/frontend/src/contracts';
import { DEFAULT_RECENT_ACTIVITY_CAPACITY } from '../../../src/frontend/src/contracts';

/** Exact type equality (invariant position trick). */
export type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends <T>() => T extends B ? 1 : 2
  ? true
  : false;

/** Compile-time assertion: T must resolve to true. */
export type Expect<T extends true> = T;

/** Baseline crawl counters (no additive WAF sub-count). */
export const baseCounters: AuditCounters = {
  queued: 12,
  fetched: 400,
  error: 3,
  blocked: 5
};

/** Additive counters including the WAF sub-count. */
export const wafCounters: AuditCounters = {
  ...baseCounters,
  wafBlocked: 4
};

export const logCounters: LogImportCounters = {
  linesParsed: 1000,
  linesAccepted: 950,
  linesRejected: 50
};

export const correlationSummary: LogCorrelationSummary = {
  orphanCount: 7,
  budgetWasteCount: 3,
  uncrawledIndexableCount: 2
};

export const healthSnapshot: SystemHealthSnapshot = {
  timestamp: '2026-11-14T06:28:43Z',
  backendWorkingSetBytes: 512 * 1024 * 1024,
  managedHeapBytes: 256 * 1024 * 1024,
  gcGen0Count: 120,
  gcGen1Count: 40,
  gcGen2Count: 5,
  sqliteWalBytes: 8 * 1024 * 1024,
  auditId: 'audit-1'
};

/**
 * One valid payload per server event (20 entries). The mapped type forces
 * exhaustiveness: a new union member without a fixture fails typecheck.
 */
export const hubPayloadFixtures: { readonly [E in HubServerEventName]: HubServerPayloadOf<E> } = {
  'crawl-progress': {
    auditId: 'audit-1',
    counters: baseCounters,
    throughputPerSecond: 6.5,
    elapsedSeconds: 120,
    estimatedRemainderSeconds: 480,
    phase: 'crawl'
  },
  'url-batch': {
    auditId: 'audit-1',
    urls: [
      { urlId: 'url-1', canonicalUrl: 'https://target.test/', statusClass: '2xx' },
      { urlId: 'url-2', canonicalUrl: 'https://target.test/missing', statusClass: '4xx' }
    ]
  },
  'stage-changed': {
    auditId: 'audit-1',
    previousState: 'running',
    newState: 'paused'
  },
  'findings-updated': {
    auditId: 'audit-1',
    severityDeltas: { high: 2, low: -1 },
    categoryDeltas: { indexability: 3 }
  },
  'audit-completed': {
    auditId: 'audit-1',
    finalCounters: baseCounters,
    reportReady: true
  },
  'audit-failed': {
    auditId: 'audit-1',
    errorCode: 'fetch-pool-exhausted',
    message: 'The fetch pool could not continue.'
  },
  'backend-lifecycle': {
    kind: 'ready',
    message: null
  },
  'schedule-fired': {
    scheduleId: 'sched-1',
    auditId: 'audit-2',
    triggerType: 'scheduled'
  },
  'schedule-run-completed': {
    scheduleId: 'sched-1',
    auditId: 'audit-2',
    outcome: 'completed',
    completionActionResults: [{ kind: 'export', succeeded: true, detail: null }]
  },
  'export-delivery': {
    exportId: 'export-1',
    destinationId: 'dest-1',
    state: 'delivered'
  },
  'waf-blocks': {
    auditId: 'audit-1',
    blocks: [
      {
        urlId: 'url-3',
        canonicalUrl: 'https://target.test/challenge',
        provider: 'example-waf',
        challengeClass: 'js-challenge',
        confidence: 0.92
      }
    ]
  },
  'host-throttle': {
    auditId: 'audit-1',
    host: 'target.test',
    trigger: 'ttfb-spike',
    rateBefore: 8,
    rateAfter: 4,
    concurrencyBefore: 4,
    concurrencyAfter: 2,
    ttfbSampleMs: 1800,
    rollingBaselineRate: 7.5
  },
  'system-health': healthSnapshot,
  'matrix-row-validated': {
    auditId: 'audit-1',
    rows: [
      { urlId: 'url-4', sourceUrl: 'https://old.test/page', verdict: 'pass' }
    ]
  },
  'log-import-progress': {
    importId: 'import-1',
    counters: logCounters,
    phase: 'parsing'
  },
  'log-import-completed': {
    importId: 'import-1',
    finalCounters: logCounters,
    correlation: correlationSummary
  },
  'embedding-progress': {
    auditId: 'audit-1',
    embeddedCount: 120,
    totalEligible: 400,
    provider: 'onnx',
    phase: 'embedding'
  },
  'embedding-completed': {
    auditId: 'audit-1',
    finalCounts: { embedded: 400, totalEligible: 400, skipped: 0 },
    passes: { clusters: 6, linkOpportunities: 18, lowRelevanceLinks: 4 }
  },
  'local-notification': {
    title: 'Audit completed',
    message: 'Fixture audit finished: 400 URLs fetched.'
  },
  'export-progress': {
    exportId: 'export-1',
    auditId: 'audit-1',
    format: 'json',
    state: 'completed',
    filePath: 'C:\\exports\\fixture-audit.json',
    message: null
  }
};

/** Valid client channel messages. */
export const clientMessageFixtures: readonly HubClientMessage[] = [
  { event: 'subscribe', payload: { auditIds: ['audit-1', 'audit-2'] } },
  { event: 'unsubscribe', payload: { auditIds: ['audit-2'] } }
];

/** Uniform envelope fixtures. */
export const successEnvelope: ApiEnvelope<{ ready: boolean }> = {
  data: { ready: true }
};

export const paginatedEnvelope: ApiEnvelope<readonly string[]> = {
  data: ['row-1', 'row-2'],
  pagination: { offset: 0, limit: 2, total: 120 }
};

export const errorEnvelope: ApiEnvelope<null> = {
  data: null,
  error: {
    code: 'validation-failed',
    message: 'One or more fields are invalid.',
    fieldErrors: { name: ['must not be empty'], 'limits.maxUrls': ['must be positive'] }
  }
};

/** Empty normalized store state — every §8.8 slice present, no datasets. */
export const emptyCrawlForgeState: CrawlForgeState = {
  settings: { settings: null, loaded: false },
  projects: { entities: { byId: {}, allIds: [] }, listPage: null },
  audits: { entities: { byId: {}, allIds: [] }, activeAuditId: null, listPage: null },
  urlPages: { pages: {}, activeKey: null },
  findingPages: { pages: {}, activeKey: null },
  linkPages: { pages: {}, activeKey: null },
  ngramPages: { pages: {}, activeKey: null },
  hostThrottle: { auditId: null, byHostKey: {} },
  systemHealth: { latest: null, frontendJsHeapBytes: null, lastHeartbeatAt: null },
  plugins: { entities: { byId: {}, allIds: [] } },
  hub: {
    connectionState: 'Disconnected',
    subscribedAuditIds: [],
    recentActivity: {
      entries: [],
      capacity: DEFAULT_RECENT_ACTIVITY_CAPACITY,
      droppedCount: 0
    }
  }
};
