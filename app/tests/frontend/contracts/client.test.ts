/**
 * Typed loopback client surface tests: a fully-typed test double proves the
 * LoopbackApiClient / TypedApiFetch contracts are implementable without
 * casts, and compile-time negatives prove required params/bodies and the
 * query-vs-body method dispatch are enforced.
 */

import { describe, expect, it } from 'vitest';
import { isEnvelopeError, isSuccessEnvelope } from '../../../src/frontend/src/contracts';
import type {
  ApiCallArgs,
  ApiCallResult,
  ApiRouteKey,
  LoopbackApiClient,
  Project,
  TypedApiFetch
} from '../../../src/frontend/src/contracts';
import { Equal, Expect } from './helpers';

const projectFixture: Project = {
  id: 'project-1',
  name: 'Demo project',
  description: null,
  tags: ['demo'],
  scopeSummary: null,
  createdAt: '2026-11-14T06:28:43Z',
  updatedAt: '2026-11-14T06:28:43Z'
};

/**
 * Per-route fixture table. Indexing this mapped type with a generic route
 * key yields `ApiCallResult<K> | undefined`, so the double below needs no
 * type assertions at all.
 */
const responseTable: { [R in ApiRouteKey]?: ApiCallResult<R> } = {
  'health.get': { data: { ready: true, uptimeSeconds: 42 } },
  'metrics.get': '# HELP crawlforge_local Local technical metrics.\n',
  'exports.download': new Blob(['spreadsheet-bytes'], { type: 'application/octet-stream' }),
  'audits.start': { data: { auditId: 'audit-1', state: 'queued' } },
  'projects.get': { data: projectFixture },
  'audits.get': { data: null, error: { code: 'not-found', message: 'Unknown audit.' } }
};

const client: LoopbackApiClient = {
  baseUrl: 'http://127.0.0.1:49152',
  request<K extends ApiRouteKey>(route: K, ..._args: ApiCallArgs<K>): Promise<ApiCallResult<K>> {
    const result = responseTable[route];
    if (result === undefined) {
      return Promise.reject(new Error(`No fixture for route ${route}`));
    }
    return Promise.resolve(result);
  }
};

describe('typed loopback client contract', () => {
  it('is bound to a loopback origin', () => {
    expect(client.baseUrl.startsWith('http://127.0.0.1')).toBe(true);
  });

  it('returns the uniform envelope for JSON routes', async () => {
    const result = await client.request('health.get');
    expect(isSuccessEnvelope(result)).toBe(true);
    if (isSuccessEnvelope(result)) {
      expect(result.data.ready).toBe(true);
      expect(result.data.uptimeSeconds).toBe(42);
    }
  });

  it('returns raw text for /metrics and a Blob for the export file', async () => {
    const metrics = await client.request('metrics.get');
    expect(typeof metrics).toBe('string');
    expect(metrics.startsWith('# HELP')).toBe(true);

    const file = await client.request('exports.download', { params: { id: 'export-1' } });
    expect(file).toBeInstanceOf(Blob);

    type Checks = [
      Expect<Equal<typeof metrics, string>>,
      Expect<Equal<typeof file, Blob>>
    ];
    const checks: Checks = [true, true];
    expect(checks.every(Boolean)).toBe(true);
  });

  it('carries POST bodies and path params through the typed surface', async () => {
    const started = await client.request('audits.start', {
      body: { configurationId: 'config-1', urlList: ['https://target.test/'], listMode: true }
    });
    expect(isSuccessEnvelope(started)).toBe(true);
    if (isSuccessEnvelope(started)) {
      expect(started.data.state).toBe('queued');
    }

    const project = await client.request('projects.get', { params: { id: 'project-1' } });
    expect(isSuccessEnvelope(project)).toBe(true);
    if (isSuccessEnvelope(project)) {
      expect(project.data.name).toBe('Demo project');
    }
  });

  it('surfaces error envelopes without throwing', async () => {
    const result = await client.request('audits.get', { params: { id: 'missing' } });
    expect(isEnvelopeError(result)).toBe(true);
    if (isEnvelopeError(result)) {
      expect(result.error.code).toBe('not-found');
      expect(result.data).toBeNull();
    }
  });

  it('is assignable to the standalone TypedApiFetch contract', () => {
    const fetcher: TypedApiFetch = client.request;
    expect(typeof fetcher).toBe('function');
  });

  it('enforces required init members (compile-time negatives)', () => {
    // Compile-time negatives only: never invoked, so no runtime calls run.
    const negativeCalls = async (): Promise<void> => {
      // @ts-expect-error — projects.get requires path params.
      await client.request('projects.get');
      // @ts-expect-error — params must not be empty for an {id} route.
      await client.request('projects.get', {});
      // @ts-expect-error — audits.getUrl requires both id and urlId.
      await client.request('audits.getUrl', { params: { id: 'audit-1' } });
      // @ts-expect-error — audits.start requires a body.
      await client.request('audits.start');
      // @ts-expect-error — POST routes take a body, not a query.
      await client.request('audits.start', { query: { configurationId: 'config-1' } });
      // @ts-expect-error — GET routes take a query, not a body.
      await client.request('health.get', { body: {} });
      // @ts-expect-error — unknown route keys are rejected.
      await client.request('not.a.route');
    };
    expect(typeof negativeCalls).toBe('function');
  });
});
