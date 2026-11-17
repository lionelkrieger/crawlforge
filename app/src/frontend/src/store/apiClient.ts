/**
 * Typed loopback API client implementation (contracts/api/client).
 *
 * One generic call surface over the frozen route catalog: resolves path
 * templates, serializes query/body per method, and parses the uniform
 * envelope (raw text/blob for the two non-enveloped routes). Loopback-only
 * by construction; rejects only on transport failure (envelope errors are
 * returned, per the contract).
 *
 * Session convention (WP-06-01, ratified): the Host enforces
 * `X-CrawlForge-Session` on /api/* (except /api/health). The token source is
 * INJECTABLE — the Electron preload bridge supplies it in production; tests
 * inject a constant. The renderer never reads the handshake file itself.
 *
 * Wire bridge (WP-05-01 handoff): pagination.total, fieldErrors field→reasons
 * map, null pagination/error omitted, enums lowercase-no-hyphen (only
 * catch-up special) — the client passes payloads through unchanged.
 *
 * @module store/apiClient
 */

import type { ApiEnvelope } from '../contracts/common/envelope';
import type { FetchLike, LoopbackApiClient, TypedApiFetch } from '../contracts/api/client';
import { API_ROUTES } from '../contracts/api/routes';

/** Session header enforced by the Host on /api/* + hub. */
export const SESSION_HEADER = 'X-CrawlForge-Session';

/** The one session-exempt route (readiness probe). */
const SESSION_EXEMPT_PATH = '/api/health';

/** Injectable session-token source (preload bridge in production). */
export type SessionTokenProvider = () => string | null | undefined;

/** Client construction options. */
export interface LoopbackApiClientOptions {
  /** Loopback origin discovered via the preload bridge (never non-loopback). */
  readonly baseUrl: string;
  /** Injectable fetch (defaults to the global DOM fetch). */
  readonly fetch?: FetchLike;
  /** Injectable session-token provider; absent means no session header. */
  readonly getSessionToken?: SessionTokenProvider;
}

const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '[::1]']);

/** Validates that a base URL is loopback-only (FS-CRAWL-001 §10.1). */
export function assertLoopbackBaseUrl(baseUrl: string): void {
  let parsed: URL;
  try {
    parsed = new URL(baseUrl);
  } catch {
    throw new Error(`Invalid API base URL: ${baseUrl}`);
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error(`API base URL must be http(s): ${baseUrl}`);
  }
  if (!LOOPBACK_HOSTS.has(parsed.hostname)) {
    throw new Error(`API base URL must be loopback-only, got host: ${parsed.hostname}`);
  }
}

/** Substitutes `{name}` path segments with URI-encoded parameter values. */
export function resolvePath(template: string, params: Record<string, unknown> | undefined): string {
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = params?.[name];
    if (value === undefined || value === null) {
      throw new Error(`Missing path parameter "${name}" for route ${template}`);
    }
    return encodeURIComponent(String(value));
  });
}

/** Serializes a query record: scalars stringified, arrays repeated, null/undefined skipped. */
export function serializeQuery(query: Record<string, unknown> | undefined): string {
  if (!query) return '';
  const parts: string[] = [];
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null) continue;
    if (Array.isArray(value)) {
      for (const item of value) {
        if (item !== undefined && item !== null) {
          parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(item))}`);
        }
      }
    } else {
      parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
    }
  }
  return parts.length > 0 ? `?${parts.join('&')}` : '';
}

/** True when the route path requires the session header. */
export function requiresSessionHeader(path: string): boolean {
  return path.startsWith('/api/') && path !== SESSION_EXEMPT_PATH;
}

/**
 * Creates the typed loopback client. The generic `request` surface is the
 * locked {@link TypedApiFetch}; runtime dispatch uses the frozen catalog.
 */
export function createLoopbackApiClient(options: LoopbackApiClientOptions): LoopbackApiClient {
  assertLoopbackBaseUrl(options.baseUrl);
  const baseUrl = options.baseUrl.replace(/\/+$/, '');
  const doFetch: FetchLike = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
  const getToken = options.getSessionToken;

  const request: TypedApiFetch = async (route, ...args) => {
    const definition = API_ROUTES[route];
    const init = (args[0] ?? {}) as {
      params?: Record<string, unknown>;
      query?: Record<string, unknown>;
      body?: unknown;
      signal?: AbortSignal;
      headers?: Record<string, string>;
    };

    const path = resolvePath(definition.path, init.params);
    const isQueryMethod = definition.method === 'GET' || definition.method === 'DELETE';
    const url = baseUrl + path + (isQueryMethod ? serializeQuery(init.query) : '');

    const headers: Record<string, string> = { Accept: 'application/json', ...init.headers };
    if (requiresSessionHeader(path)) {
      const token = getToken?.();
      if (token) headers[SESSION_HEADER] = token;
    }

    const requestInit: RequestInit = { method: definition.method, headers, signal: init.signal };
    if (!isQueryMethod && init.body !== undefined) {
      headers['Content-Type'] = 'application/json';
      requestInit.body = JSON.stringify(init.body);
    }

    const response = await doFetch(url, requestInit);

    // Non-enveloped routes: raw text (/metrics) or binary (export file).
    if (!definition.enveloped) {
      if (!response.ok) {
        throw new Error(`Request failed: ${definition.method} ${path} → HTTP ${response.status}`);
      }
      const raw = route === 'metrics.get' ? await response.text() : await response.blob();
      return raw as never;
    }

    // Enveloped routes: parse the uniform envelope; synthesize one only when
    // the transport returned a non-JSON error body (host-level failure).
    let envelope: ApiEnvelope<unknown>;
    try {
      envelope = (await response.json()) as ApiEnvelope<unknown>;
    } catch {
      envelope = {
        data: null,
        error: {
          code: 'internal',
          message: `Request failed: ${definition.method} ${path} → HTTP ${response.status}`
        }
      };
    }
    return envelope as never;
  };

  return { baseUrl, request };
}
