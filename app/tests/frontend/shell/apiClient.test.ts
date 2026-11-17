/**
 * Typed loopback API client tests: path resolution, query serialization,
 * session-header convention (X-CrawlForge-Session; /api/health exempt),
 * envelope bridging (pagination.total, fieldErrors map, omitted nulls),
 * raw routes, and the loopback-only guard.
 */

import { describe, expect, it } from 'vitest';
import {
  createLoopbackApiClient,
  assertLoopbackBaseUrl,
  resolvePath,
  serializeQuery,
  requiresSessionHeader,
  SESSION_HEADER
} from '../../../src/frontend/src/store/apiClient';
import { ok, err, fakeApiClient, fakeFetch } from './helpers';

describe('loopback guard', () => {
  it('accepts loopback origins', () => {
    expect(() => assertLoopbackBaseUrl('http://127.0.0.1:5000')).not.toThrow();
    expect(() => assertLoopbackBaseUrl('http://localhost:5173')).not.toThrow();
    expect(() => assertLoopbackBaseUrl('http://[::1]:8080')).not.toThrow();
  });

  it('rejects non-loopback and invalid origins', () => {
    expect(() => assertLoopbackBaseUrl('https://evil.example.com')).toThrow(/loopback/);
    expect(() => assertLoopbackBaseUrl('http://192.168.1.10')).toThrow(/loopback/);
    expect(() => assertLoopbackBaseUrl('not a url')).toThrow(/Invalid/);
    expect(() => assertLoopbackBaseUrl('ftp://127.0.0.1')).toThrow(/http/);
    expect(() => createLoopbackApiClient({ baseUrl: 'https://remote.test' })).toThrow(/loopback/);
  });
});

describe('path and query serialization', () => {
  it('resolves and encodes path parameters', () => {
    expect(resolvePath('/api/audits/{id}/hosts/{host}/override', { id: '7', host: 'a b.com' })).toBe(
      '/api/audits/7/hosts/a%20b.com/override'
    );
    expect(() => resolvePath('/api/audits/{id}', {})).toThrow(/Missing path parameter/);
  });

  it('serializes scalars, booleans, arrays; skips null/undefined', () => {
    expect(serializeQuery({ offset: 0, limit: 25, sort: 'url', order: 'desc' })).toBe(
      '?offset=0&limit=25&sort=url&order=desc'
    );
    expect(serializeQuery({ indexable: false, state: undefined, q: null, tags: ['a', 'b'] })).toBe(
      '?indexable=false&tags=a&tags=b'
    );
    expect(serializeQuery(undefined)).toBe('');
  });

  it('applies the session-header convention', () => {
    expect(requiresSessionHeader('/api/projects')).toBe(true);
    expect(requiresSessionHeader('/api/system/compact')).toBe(true);
    expect(requiresSessionHeader('/api/health')).toBe(false);
    expect(requiresSessionHeader('/metrics')).toBe(false);
  });
});

describe('typed requests', () => {
  it('sends the session header on /api/* and parses the envelope', async () => {
    const { api, calls } = fakeApiClient({ '/api/projects': () => ok([{ id: '1' }], { offset: 0, limit: 50, total: 1 }) });
    const envelope = await api.request('projects.list', { query: { limit: 50 } });
    expect(envelope.data).toEqual([{ id: '1' }]);
    expect(envelope.pagination?.total).toBe(1);
    expect(calls[0].url).toBe('http://127.0.0.1:5000/api/projects?limit=50');
    expect(calls[0].headers[SESSION_HEADER]).toBe('test-session-token');
  });

  it('omits the session header on /api/health', async () => {
    const { api, calls } = fakeApiClient({ '/api/health': () => ok({ ready: true, uptimeSeconds: 5 }) });
    await api.request('health.get');
    expect(calls[0].headers[SESSION_HEADER]).toBeUndefined();
  });

  it('resolves path params and JSON bodies for POST routes', async () => {
    const { api, calls } = fakeApiClient({
      '/api/audits/7/pause': () => ok({ auditId: '7', state: 'paused' })
    });
    const envelope = await api.request('audits.pause', { params: { id: '7' } });
    expect(envelope.data?.state).toBe('paused');
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toBe('http://127.0.0.1:5000/api/audits/7/pause');
  });

  it('passes fieldErrors through as a field→reasons map', async () => {
    const { api } = fakeApiClient({
      '/api/projects': () => err('validation', 'Invalid project', { baseUrl: ['must be absolute'] })
    });
    const envelope = await api.request('projects.create', { body: { name: 'x' } });
    expect(envelope.data).toBeNull();
    expect(envelope.error?.code).toBe('validation');
    expect(envelope.error?.fieldErrors?.baseUrl).toEqual(['must be absolute']);
  });

  it('returns raw text for /metrics without envelope parsing', async () => {
    const exposition = '# HELP crawl_urls_total URLs\n# TYPE crawl_urls_total counter\ncrawl_urls_total 7\n';
    const { fetchLike } = fakeFetch({});
    const api = createLoopbackApiClient({
      baseUrl: 'http://127.0.0.1:5000',
      fetch: async () => new Response(exposition, { status: 200, headers: { 'Content-Type': 'text/plain' } })
    });
    void fetchLike;
    const text = await api.request('metrics.get');
    expect(text).toBe(exposition);
  });

  it('synthesizes an internal error envelope for non-JSON failures', async () => {
    const api = createLoopbackApiClient({
      baseUrl: 'http://127.0.0.1:5000',
      fetch: async () => new Response('<html>502</html>', { status: 502 })
    });
    const envelope = await api.request('projects.list');
    expect(envelope.data).toBeNull();
    expect(envelope.error?.code).toBe('internal');
    expect(envelope.error?.message).toContain('502');
  });

  it('rejects on transport failure only', async () => {
    const api = createLoopbackApiClient({
      baseUrl: 'http://127.0.0.1:5000',
      fetch: async () => {
        throw new TypeError('fetch failed');
      }
    });
    await expect(api.request('projects.list')).rejects.toThrow('fetch failed');
  });

  it('works without a session-token provider (header absent)', async () => {
    const { fetchLike, calls } = fakeFetch({ '/api/projects': () => ok([]) });
    const api = createLoopbackApiClient({ baseUrl: 'http://localhost:5000', fetch: fetchLike });
    await api.request('projects.list');
    expect(calls[0].headers[SESSION_HEADER]).toBeUndefined();
    expect(api.baseUrl).toBe('http://localhost:5000');
  });
});
