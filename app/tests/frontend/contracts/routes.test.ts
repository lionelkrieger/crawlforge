/**
 * Route catalog contract tests: the frozen catalog must mirror the REST
 * spec exactly — 89 route groups (51 baseline rows of FS-CRAWL-001 §10.2 +
 * 38 additive rows of FS-CRAWL-002 Appendix B), expanding to 109
 * method+path operations. Expected paths below are transcribed verbatim
 * from the spec tables. WP-10-04 (UI-21 amendment) added settings.browser.
 */

import { describe, expect, it } from 'vitest';
import {
  API_ROUTES,
  isApiEnvelope
} from '../../../src/frontend/src/contracts';
import type {
  ApiCallResult,
  ApiEnvelope,
  ApiRouteKey,
  AssertAllRoutesCovered,
  AssertNoOrphanContracts,
  AuditStartRequest,
  HasRouteParams,
  HealthResponse,
  IsRouteInitRequired,
  IsRoutePayloadRequired,
  ResolveApiPath
} from '../../../src/frontend/src/contracts';
import { Equal, Expect } from './helpers';

/** Baseline method+path pairs — FS-CRAWL-001 §10.2 (51 rows, 71 operations). */
const BASELINE_OPERATIONS: readonly string[] = [
  'GET /api/health',
  'GET /api/version',
  'GET /metrics',
  'GET /api/settings',
  'PUT /api/settings',
  'POST /api/settings/database/test',
  'GET /api/diagnostics/artifacts',
  'DELETE /api/diagnostics/artifacts',
  'GET /api/projects',
  'POST /api/projects',
  'GET /api/projects/{id}',
  'PUT /api/projects/{id}',
  'DELETE /api/projects/{id}',
  'GET /api/projects/{id}/configurations',
  'POST /api/projects/{id}/configurations',
  'GET /api/configurations/{id}',
  'PUT /api/configurations/{id}',
  'DELETE /api/configurations/{id}',
  'POST /api/audits',
  'GET /api/audits',
  'GET /api/audits/{id}',
  'POST /api/audits/{id}/pause',
  'POST /api/audits/{id}/resume',
  'POST /api/audits/{id}/cancel',
  'POST /api/audits/{id}/reaudit',
  'GET /api/audits/{id}/urls',
  'GET /api/audits/{id}/urls/{urlId}',
  'GET /api/audits/{id}/links',
  'GET /api/audits/{id}/findings',
  'GET /api/audits/{id}/summary',
  'GET /api/audits/{id}/charts/{metric}',
  'GET /api/audits/{id}/graph',
  'GET /api/audits/compare',
  'POST /api/analysis/single-page',
  'POST /api/exports',
  'GET /api/exports/{id}/file',
  'GET /api/projects/{id}/schedules',
  'POST /api/projects/{id}/schedules',
  'GET /api/schedules/{id}',
  'PUT /api/schedules/{id}',
  'DELETE /api/schedules/{id}',
  'POST /api/schedules/{id}/enable',
  'POST /api/schedules/{id}/disable',
  'POST /api/schedules/{id}/run-now',
  'GET /api/schedules/{id}/runs',
  'GET /api/destinations',
  'POST /api/destinations',
  'PUT /api/destinations/{id}',
  'DELETE /api/destinations/{id}',
  'POST /api/destinations/{id}/test',
  'GET /api/dictionary',
  'POST /api/dictionary',
  'DELETE /api/dictionary',
  'GET /api/audits/compare/content',
  'GET /api/audits/{id}/extracted-content',
  'GET /api/audits/{id}/extracted-content/summary',
  'GET /api/audits/{id}/search-results',
  'GET /api/audits/{id}/snippet-results',
  'GET /api/audits/{id}/technologies',
  'GET /api/audits/{id}/scores',
  'POST /api/projects/{id}/suppressions',
  'DELETE /api/projects/{id}/suppressions/{key}',
  'POST /api/tools/robots-test',
  'POST /api/tools/url-check',
  'POST /api/tools/structured-data-check',
  'POST /api/tools/render-test',
  'POST /api/tools/sitemap-generate',
  'GET /api/queries',
  'POST /api/queries',
  'PUT /api/queries',
  'DELETE /api/queries'
];

/** Additive method+path pairs — FS-CRAWL-002 Appendix B (38 rows). */
const ADDITIVE_OPERATIONS: readonly string[] = [
  'GET /api/audits/{id}/waf-blocks',
  'GET /api/audits/{id}/waf-blocks/summary',
  'GET /api/waf-signatures',
  'POST /api/waf-signatures',
  'PUT /api/waf-signatures/{key}',
  'DELETE /api/waf-signatures/{key}',
  'GET /api/audits/{id}/hosts',
  'POST /api/audits/{id}/hosts/{host}/override',
  'GET /api/audits/{id}/ngrams',
  'GET /api/audits/{id}/ngrams/cannibalization',
  'GET /api/audits/{id}/ngrams/gaps',
  'POST /api/tools/scope-preview',
  'GET /api/plugins',
  'PUT /api/plugins/{id}',
  'POST /api/plugins/rescan',
  'DELETE /api/plugins/{id}',
  'GET /api/system/health',
  'POST /api/system/compact',
  'POST /api/audits/{id}/redirect-matrix',
  'GET /api/audits/{id}/redirect-matrix/results',
  'POST /api/tools/rewrite-preview',
  'POST /api/projects/{id}/logs/import',
  'GET /api/projects/{id}/logs',
  'DELETE /api/logs/{id}',
  'GET /api/audits/{id}/logs/correlation',
  'POST /api/logs/{id}/verify-dns',
  'POST /api/audits/{id}/semantics/run',
  'GET /api/audits/{id}/semantics/similar',
  'GET /api/audits/{id}/semantics/link-opportunities',
  'GET /api/audits/{id}/semantics/clusters',
  'POST /api/settings/embedding/test',
  'GET /api/settings/browser',
  'GET /api/audits/{id}/geo/llms-txt',
  'GET /api/audits/{id}/geo/governance',
  'POST /api/tools/llms-generate',
  'POST /api/tools/llms-validate',
  'GET /api/audits/{id}/analytics',
  'GET /api/audits/{id}/analytics/coverage'
];

const routeKeys = Object.keys(API_ROUTES) as ApiRouteKey[];
const operationOf = (key: ApiRouteKey): string => `${API_ROUTES[key].method} ${API_ROUTES[key].path}`;

describe('route catalog coverage (89 routes: 51 baseline + 38 additive)', () => {
  it('expands to exactly 109 unique method+path operations', () => {
    expect(routeKeys).toHaveLength(109);
    const operations = routeKeys.map(operationOf);
    expect(new Set(operations).size).toBe(109);
  });

  it('mirrors every baseline operation verbatim', () => {
    const baseline = routeKeys.filter((key) => !API_ROUTES[key].additive).map(operationOf);
    expect(baseline).toHaveLength(71);
    expect(new Set(baseline)).toEqual(new Set(BASELINE_OPERATIONS));
  });

  it('mirrors every additive operation verbatim', () => {
    const additive = routeKeys.filter((key) => API_ROUTES[key].additive).map(operationOf);
    expect(additive).toHaveLength(38);
    expect(new Set(additive)).toEqual(new Set(ADDITIVE_OPERATIONS));
  });

  it('groups operations into exactly 89 spec rows (51 baseline + 38 additive)', () => {
    const baselineGroups = new Set(
      routeKeys.filter((key) => !API_ROUTES[key].additive).map((key) => API_ROUTES[key].group)
    );
    const additiveGroups = new Set(
      routeKeys.filter((key) => API_ROUTES[key].additive).map((key) => API_ROUTES[key].group)
    );
    expect(baselineGroups.size).toBe(51);
    expect(additiveGroups.size).toBe(38);
    // No group mixes baseline and additive operations.
    for (const group of additiveGroups) {
      expect(baselineGroups.has(group)).toBe(false);
    }
    expect(baselineGroups.size + additiveGroups.size).toBe(89);
  });

  it('marks exactly the two non-JSON routes as non-enveloped', () => {
    const raw = routeKeys.filter((key) => !API_ROUTES[key].enveloped).map(operationOf);
    expect(new Set(raw)).toEqual(new Set(['GET /metrics', 'GET /api/exports/{id}/file']));
  });

  it('uses well-formed loopback path templates', () => {
    for (const key of routeKeys) {
      const { path } = API_ROUTES[key];
      expect(path.startsWith('/api/') || path === '/metrics').toBe(true);
      expect(path).toMatch(/^(\/[a-z0-9-]+|\/\{[a-zA-Z][a-zA-Z0-9]*\})+$/);
      expect(path.includes('//')).toBe(false);
    }
  });

  it('binds every catalog key to a DTO contract (compile-time lock)', () => {
    type Checks = [
      Expect<Equal<AssertAllRoutesCovered, true>>,
      Expect<Equal<AssertNoOrphanContracts, true>>
    ];
    const checks: Checks = [true, true];
    expect(checks.every(Boolean)).toBe(true);
  });
});

describe('typed client surface', () => {
  it('resolves path templates at the type level', () => {
    type Checks = [
      Expect<Equal<ResolveApiPath<'/api/health', Record<string, never>>, '/api/health'>>,
      Expect<
        Equal<
          ResolveApiPath<'/api/audits/{id}/urls/{urlId}', { id: 'a1'; urlId: 'u1' }>,
          '/api/audits/a1/urls/u1'
        >
      >,
      Expect<
        Equal<
          ResolveApiPath<'/api/audits/{id}/hosts/{host}/override', { id: 'a1'; host: 'h' }>,
          '/api/audits/a1/hosts/h/override'
        >
      >
    ];
    const checks: Checks = [true, true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('enforces params/body required-ness per route', () => {
    type Checks = [
      Expect<Equal<HasRouteParams<'health.get'>, false>>,
      Expect<Equal<HasRouteParams<'projects.get'>, true>>,
      Expect<Equal<HasRouteParams<'audits.getUrl'>, true>>,
      Expect<Equal<HasRouteParams<'hosts.override'>, true>>,
      Expect<Equal<IsRoutePayloadRequired<'audits.start'>, true>>,
      Expect<Equal<IsRoutePayloadRequired<'audits.pause'>, false>>,
      Expect<Equal<IsRoutePayloadRequired<'diagnostics.purgeArtifacts'>, false>>,
      Expect<Equal<IsRoutePayloadRequired<'dictionary.remove'>, true>>,
      Expect<Equal<IsRouteInitRequired<'health.get'>, false>>,
      Expect<Equal<IsRouteInitRequired<'projects.get'>, true>>,
      Expect<Equal<IsRouteInitRequired<'audits.start'>, true>>
    ];
    const checks: Checks = [true, true, true, true, true, true, true, true, true, true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('wraps enveloped routes and leaves raw routes unwrapped', () => {
    type Checks = [
      Expect<Equal<ApiCallResult<'health.get'>, ApiEnvelope<HealthResponse>>>,
      Expect<Equal<ApiCallResult<'metrics.get'>, string>>,
      Expect<Equal<ApiCallResult<'exports.download'>, Blob>>
    ];
    const checks: Checks = [true, true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('keeps the audit-start request shape (configuration reference + additive list-mode)', () => {
    const request: AuditStartRequest = {
      configurationId: 'config-1',
      limitOverrides: { maxUrls: 1000 },
      urlList: ['https://target.test/a'],
      listMode: true
    };
    expect(request.configurationId).toBe('config-1');
    expect(request.listMode).toBe(true);
    // The envelope guard accepts a well-formed start response.
    expect(isApiEnvelope({ data: { auditId: 'audit-1', state: 'queued' } })).toBe(true);
  });
});
