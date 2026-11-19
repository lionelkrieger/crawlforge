/**
 * Shared fixtures + harness for the report suites (WP-09-08).
 *
 * Every fixture is the ACTUAL REST wire JSON the backend produces
 * (camelCase, lowercase enum names, uniform envelope): finding rows carry
 * the wave-9A evidence shapes (structured-data.invalid, hreflang
 * reciprocity, content.duplicate-title, canonical.header-html-conflict) as
 * `evidence.details` — the generic key/value drill-down consumes them
 * without special-casing. The findings route responder honours the locked
 * severity/category/ruleKey/urlId filters + offset/limit pagination so the
 * filter and virtualization proofs run against real server-windowed shapes.
 */

import type {
  Finding,
  AuditDashboardSummary,
  AuditScores,
  AuditDetail
} from '../../../src/frontend/src/contracts/api/audits';
import type { SuppressionState } from '../../../src/frontend/src/contracts/api/projects';
import type { HostState } from '../../../src/frontend/src/contracts/api/hosts';
import type { WafBlocksSummary } from '../../../src/frontend/src/contracts/api/waf';
import { createLoopbackApiClient } from '../../../src/frontend/src/store/apiClient';
import { ok, err, type FetchCall } from '../shell/helpers';

export {
  ok, err, fakeFetch, fakeApiClient, render, flush, byTestId, fire, click, type,
  installDomStubs, type FetchCall, type Mounted
} from '../shell/helpers';
export { installGridStubs, primeGrid, setScrollGeometry, triggerResizeObservers } from '../grid/helpers';
export { createMockChartFactory } from '../charts/helpers';

export const AUDIT = 'audit-1';
export const PROJECT = 'project-1';

// ── Finding wire fixtures (wave-9A evidence shapes) ────────────────────────

/** structured-data.invalid — Product missing required fields (WP-09-04). */
export const structuredDataInvalid: Finding = {
  id: 'f-sd-1',
  ruleKey: 'structured-data.invalid',
  severity: 'error',
  category: 'structureddata',
  urlId: '7',
  message: 'Product block is missing required properties',
  evidence: {
    snippet: '<script type="application/ld+json">{"@type":"Product"}</script>',
    selector: 'head > script[type="application/ld+json"]',
    details: {
      url: 'http://127.0.0.1:47311/pages/structured-data.html',
      format: 'json-ld',
      ordinal: 0,
      type: 'Product',
      errors: [
        "missing-required-field: Product requires 'name'",
        "missing-required-field: Product requires 'image'",
        "missing-required-field: Product requires 'offers'",
        'rich-result-missing-required: name, image, offers'
      ],
      missingRequired: ['name', 'image', 'offers'],
      nestedEntityTypes: [],
      richResult: 'missing-required-field'
    }
  }
};

/** i18n.hreflang-missing-reciprocity — both-page evidence (WP-09-04). */
export const hreflangNoReturn: Finding = {
  id: 'f-hl-1',
  ruleKey: 'i18n.hreflang-missing-reciprocity',
  severity: 'warning',
  category: 'structureddata',
  urlId: '21',
  message: 'hreflang target does not return the annotation',
  evidence: {
    details: {
      auditId: 1,
      pageUrl: 'http://127.0.0.1:47311/pages/hreflang-en.html',
      pageUrlId: '21',
      targetUrl: 'http://127.0.0.1:47311/pages/hreflang-fr.html',
      targetUrlId: '22',
      hreflang: 'fr',
      targetAnnotations: [],
      reason: 'The target page declares no hreflang return annotation.'
    }
  }
};

/** content.duplicate-title (WP-09-05). */
export const duplicateTitle: Finding = {
  id: 'f-dt-1',
  ruleKey: 'content.duplicate-title',
  severity: 'warning',
  category: 'content',
  urlId: '3',
  message: 'Title duplicates another page',
  evidence: {
    details: {
      title: 'Welcome — Example',
      duplicateOfUrl: 'http://127.0.0.1:47311/pages/home.html',
      duplicateOfUrlId: '1',
      occurrences: 2
    }
  }
};

/** canonical.header-html-conflict (indexability). */
export const canonicalConflict: Finding = {
  id: 'f-cc-1',
  ruleKey: 'canonical.header-html-conflict',
  severity: 'error',
  category: 'indexability',
  urlId: '4',
  message: 'Link header canonical conflicts with the HTML canonical',
  evidence: {
    details: {
      headerValue: 'https://example.com/a',
      htmlValue: 'https://example.com/b',
      diff: { header: 'https://example.com/a', html: 'https://example.com/b' }
    }
  }
};

/** A pass finding with no evidence payload. */
export const cleanPass: Finding = {
  id: 'f-pass-1',
  ruleKey: 'structured-data.valid',
  severity: 'pass',
  category: 'structureddata',
  urlId: '9',
  message: 'Structured data block is valid',
  evidence: { details: { url: 'https://example.com/ok', format: 'json-ld', ordinal: 0, type: 'Article', richResult: 'eligible', nestedEntityTypes: [] } }
};

export const sampleFindings: readonly Finding[] = [
  structuredDataInvalid,
  hreflangNoReturn,
  duplicateTitle,
  canonicalConflict,
  cleanPass
];

/** Deterministic large finding dataset for the virtualization proof. */
export function seedFindings(count: number): Finding[] {
  const severities = ['error', 'warning', 'notice', 'pass'];
  const categories = ['indexability', 'content', 'linking', 'structureddata', 'accessibility'];
  const rules = ['title-length', 'broken-link', 'meta-description', 'structured-data.invalid', 'hreflang-missing-reciprocity'];
  const rows: Finding[] = [];
  for (let i = 0; i < count; i += 1) {
    rows.push({
      id: `f-${i}`,
      ruleKey: rules[i % rules.length],
      severity: severities[i % severities.length],
      category: categories[i % categories.length],
      urlId: String(i % 500),
      message: `Finding ${i} message`,
      evidence: { details: { ordinal: i, url: `https://example.com/page/${i % 500}` } }
    });
  }
  return rows;
}

// ── Dashboard aggregate wire fixtures ──────────────────────────────────────

export const summaryFixture: AuditDashboardSummary = {
  countsByStatus: { '2xx': 110, '3xx': 4, '4xx': 4, '5xx': 2 },
  countsBySeverity: { error: 5, warning: 9, notice: 3, pass: 40 },
  countsByCategory: { structureddata: 6, content: 4, indexability: 5, linking: 2 },
  crawlHealth: { totalUrls: 120, fetched: 115, errorCount: 5, blockedCount: 3, averageTtfbMs: 142.6 },
  waf: { totalBlocked: 2, blockRate: 0.017, byProvider: { cloudflare: 2 }, byChallengeClass: { 'js-challenge': 2 } },
  throttle: { throttledHostCount: 1, meanEffectiveRate: 4.5, adjustmentCount: 3 }
};

export const scoresBefore: AuditScores = {
  overall: 72,
  categories: [
    { category: 'structureddata', score: 60, findingCount: 6 },
    { category: 'content', score: 80, findingCount: 4 },
    { category: 'indexability', score: 70, findingCount: 5 }
  ]
};

/** Scores after suppressing the structured-data error (backend recompute). */
export const scoresAfter: AuditScores = {
  overall: 80,
  categories: [
    { category: 'structureddata', score: 75, findingCount: 5 },
    { category: 'content', score: 80, findingCount: 4 },
    { category: 'indexability', score: 70, findingCount: 5 }
  ]
};

export const auditDetailFixture: AuditDetail = {
  id: AUDIT,
  projectId: PROJECT,
  configurationId: 'config-1',
  state: 'completed',
  phase: 'done',
  startedAt: '2026-11-18T10:00:00Z',
  completedAt: '2026-11-18T10:05:00Z',
  counters: { queued: 0, fetched: 115, error: 5, blocked: 3, wafBlocked: 2 },
  elapsedSeconds: 300,
  throughputPerSecond: 0.38,
  estimatedRemainderSeconds: null
};

export const hostFixture: HostState = {
  host: '127.0.0.1',
  effectiveRate: 4.5,
  concurrency: 2,
  ttfbEwmaMs: 142,
  rollingBaselineRate: 8,
  throttled: true,
  override: null,
  history: [
    {
      timestamp: '2026-11-18T10:03:00Z',
      trigger: 'ttfb-spike',
      rateBefore: 6,
      rateAfter: 4.5,
      concurrencyBefore: 3,
      concurrencyAfter: 2,
      ttfbSampleMs: 210
    }
  ]
};

export const wafSummaryFixture: WafBlocksSummary = {
  totalBlocked: 2,
  blockRate: 0.017,
  byProvider: { cloudflare: 2 },
  byChallengeClass: { 'js-challenge': 2 }
};

// ── Suppression wire fixtures ──────────────────────────────────────────────

/** Finding-scoped suppression as the REST layer projects it. */
export const suppressionFixture: SuppressionState = {
  key: 'rule=structured-data.invalid;url=7;scope=*',
  projectId: PROJECT,
  ruleKey: 'structured-data.invalid',
  scope: { urlId: '7' },
  suppressed: true,
  createdAt: '2026-11-18T11:00:00Z'
};

/** Rule-scoped suppression (all URLs). */
export const ruleSuppressionFixture: SuppressionState = {
  key: 'rule=content.duplicate-title;url=*;scope=*',
  projectId: PROJECT,
  ruleKey: 'content.duplicate-title',
  scope: null,
  suppressed: true,
  createdAt: '2026-11-18T11:05:00Z'
};

// ── Route builders ─────────────────────────────────────────────────────────

/** Parses the locked findings filters out of a request URL. */
function findingsFilter(call: FetchCall): (row: Finding) => boolean {
  const query = new URL(call.url, 'http://127.0.0.1').searchParams;
  const severity = query.get('severity');
  const category = query.get('category');
  const ruleKey = query.get('ruleKey');
  const urlId = query.get('urlId');
  return (row) =>
    (!severity || row.severity === severity) &&
    (!category || row.category === category) &&
    (!ruleKey || row.ruleKey === ruleKey) &&
    (!urlId || row.urlId === urlId);
}

/** Options for the report route table. */
export interface ReportRoutesOptions {
  readonly findings?: readonly Finding[];
  /** Mutable scores snapshot (suppression tests flip it on POST/DELETE). */
  readonly scores?: { current: AuditScores };
  readonly summary?: AuditDashboardSummary | null;
  readonly detail?: AuditDetail | null;
  /** Fail the findings route with an envelope error. */
  readonly findingsError?: string;
  /** Artificial findings latency (ms) for loading-state proofs. */
  readonly findingsDelayMs?: number;
  /** Suppression create/delete responder override. */
  readonly onSuppression?: (call: FetchCall) => unknown;
}

/**
 * The report route table. Order matters: specific fragments precede the
 * generic audit-detail fragment (fakeFetch matches by substring inclusion).
 */
export function reportRoutes(options: ReportRoutesOptions = {}) {
  const findings = options.findings ?? sampleFindings;
  const scores = options.scores ?? { current: scoresBefore };
  const summary = options.summary === undefined ? summaryFixture : options.summary;
  const detail = options.detail === undefined ? auditDetailFixture : options.detail;
  return {
    '/waf-blocks/summary': () => ok(wafSummaryFixture),
    '/findings': async (call: FetchCall) => {
      if (options.findingsDelayMs) {
        await new Promise((resolve) => setTimeout(resolve, options.findingsDelayMs));
      }
      if (options.findingsError) {
        return err('internal', options.findingsError);
      }
      const query = new URL(call.url, 'http://127.0.0.1').searchParams;
      const offset = Number(query.get('offset') ?? '0');
      const limit = Number(query.get('limit') ?? '200');
      const matched = findings.filter(findingsFilter(call));
      return ok(matched.slice(offset, offset + limit), { offset, limit, total: matched.length });
    },
    '/summary': () => (summary ? ok(summary) : err('not-found', 'No summary')),
    '/scores': () => ok(scores.current),
    '/charts/': () => ok({ metric: 'score-trend', points: [{ x: '2026-11-18', y: 72 }] }),
    '/hosts': () => ok([hostFixture]),
    '/suppressions': (call: FetchCall) => {
      if (options.onSuppression) return options.onSuppression(call);
      if (call.method === 'POST') {
        scores.current = scoresAfter;
        return ok(suppressionFixture);
      }
      scores.current = scoresBefore;
      return ok({ ...suppressionFixture, suppressed: false });
    },
    [`/audits/${AUDIT}`]: () => (detail ? ok(detail) : err('not-found', 'No audit'))
  };
}

/**
 * Await-capable fake fetch (the shell fakeFetch does not await responders;
 * the report routes use async responders for latency/error proofs). Same
 * substring routing + call recording contract as the shell harness.
 */
export function reportFetch(routes: Record<string, (call: FetchCall) => unknown>) {
  const calls: FetchCall[] = [];
  const fetchLike = async (input: string, init?: RequestInit): Promise<Response> => {
    const headers: Record<string, string> = {};
    const rawHeaders = init?.headers as Record<string, string> | undefined;
    if (rawHeaders) Object.assign(headers, rawHeaders);
    const call: FetchCall = {
      url: input,
      method: init?.method ?? 'GET',
      headers,
      body: typeof init?.body === 'string' ? init.body : null
    };
    calls.push(call);
    for (const [fragment, responder] of Object.entries(routes)) {
      if (input.includes(fragment)) {
        const payload = await responder(call);
        if (payload instanceof Response) return payload;
        return new Response(JSON.stringify(payload), {
          status: 200,
          headers: { 'Content-Type': 'application/json' }
        });
      }
    }
    return new Response(JSON.stringify(err('not-found', `No fake route for ${input}`)), {
      status: 404,
      headers: { 'Content-Type': 'application/json' }
    });
  };
  return { fetchLike, calls };
}

/** Typed fake client over the report routes. */
export function reportClient(options: ReportRoutesOptions = {}) {
  const { fetchLike, calls } = reportFetch(reportRoutes(options));
  const api = createLoopbackApiClient({ baseUrl: 'http://127.0.0.1:5000', fetch: fetchLike });
  return { api, calls };
}

/** Finds recorded calls whose URL contains a fragment. */
export function callsTo(calls: readonly FetchCall[], fragment: string): FetchCall[] {
  return calls.filter((call) => call.url.includes(fragment));
}

/** Selects an option on a native select under React's value tracker. */
export function selectOption(select: HTMLSelectElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value')?.set;
  setter?.call(select, value);
  select.dispatchEvent(new Event('change', { bubbles: true }));
}
