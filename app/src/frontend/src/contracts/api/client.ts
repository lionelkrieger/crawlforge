/**
 * Typed loopback API client surface — the contract every frontend workpiece
 * codes against. Types only: the fetch wrapper implementation arrives with
 * the phase-06 shell workpiece; runtime code here is limited to type guards
 * (see contracts/common/guards and contracts/hub/guards).
 *
 * The client is loopback-only by construction: `baseUrl` is the local
 * backend origin discovered through the Electron preload bridge. No
 * authentication headers are required for the shell-facing API
 * (FS-CRAWL-001 §10.1).
 *
 * @module contracts/api/client
 */

import type { ApiEnvelope } from '../common/envelope';
import type { ApiRouteKey, ApiRouteMethod, RawRouteKey } from './routes';
import type { ApiRouteParams, ApiRouteRequest, ApiRouteResponse } from './contracts-map';

/**
 * Minimal fetch dependency (DOM fetch compatible) so implementations and
 * test doubles can be injected without coupling to a global.
 */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * True when the route template has at least one path parameter.
 * A plain `Record<string, never>` (no params) has key type `string`; a
 * concrete params record has a narrow literal key union.
 */
export type HasRouteParams<K extends ApiRouteKey> = string extends keyof ApiRouteParams<K>
  ? false
  : true;

/** True when the request payload has at least one required field. */
export type IsRoutePayloadRequired<K extends ApiRouteKey> = {} extends ApiRouteRequest<K>
  ? false
  : true;

/** Path parameters: required exactly when the route template has parameters. */
export type RouteParamsOption<K extends ApiRouteKey> = HasRouteParams<K> extends true
  ? { readonly params: ApiRouteParams<K> }
  : { readonly params?: undefined };

/** Query payload for GET/DELETE routes. */
export type RouteQueryOption<K extends ApiRouteKey> = IsRoutePayloadRequired<K> extends true
  ? { readonly query: ApiRouteRequest<K> }
  : { readonly query?: ApiRouteRequest<K> };

/** Body payload for POST/PUT routes. */
export type RouteBodyOption<K extends ApiRouteKey> = IsRoutePayloadRequired<K> extends true
  ? { readonly body: ApiRouteRequest<K> }
  : { readonly body?: ApiRouteRequest<K> };

/** Method-dispatched payload option (query vs body). */
export type RoutePayloadOption<K extends ApiRouteKey> = ApiRouteMethod<K> extends 'GET' | 'DELETE'
  ? RouteQueryOption<K>
  : RouteBodyOption<K>;

/**
 * Per-route request options: path params, query or body (method-dispatched),
 * plus cancellation and extra headers. Required members are enforced by the
 * type system — a POST with a required body cannot be called without it.
 */
export type ApiRequestInit<K extends ApiRouteKey> = {
  readonly signal?: AbortSignal;
  readonly headers?: Readonly<Record<string, string>>;
} & RouteParamsOption<K> &
  RoutePayloadOption<K>;

/** True when the call must receive an init argument. */
export type IsRouteInitRequired<K extends ApiRouteKey> = HasRouteParams<K> extends true
  ? true
  : IsRoutePayloadRequired<K>;

/**
 * Call result: the uniform JSON envelope for enveloped routes; the raw
 * payload (text for /metrics, Blob for the export file) for the two
 * non-enveloped routes.
 */
export type ApiCallResult<K extends ApiRouteKey> = K extends RawRouteKey
  ? ApiRouteResponse<K>
  : ApiEnvelope<ApiRouteResponse<K>>;

/** Rest-tuple encoding of the optional/required init argument. */
export type ApiCallArgs<K extends ApiRouteKey> = IsRouteInitRequired<K> extends true
  ? [init: ApiRequestInit<K>]
  : [init?: ApiRequestInit<K>];

/**
 * Typed fetch wrapper contract: one generic call surface over the frozen
 * route catalog. Implementations resolve the path template, serialize the
 * query/body, and parse the envelope (or raw payload for non-enveloped
 * routes).
 */
export type TypedApiFetch = <K extends ApiRouteKey>(
  route: K,
  ...args: ApiCallArgs<K>
) => Promise<ApiCallResult<K>>;

/**
 * Loopback API client contract consumed by all frontend workpieces.
 * `baseUrl` is the loopback origin (e.g. http://127.0.0.1:{port}) discovered
 * via the preload bridge; it never points at a non-loopback host.
 */
export interface LoopbackApiClient {
  readonly baseUrl: string;
  /** Perform a typed route call; rejects only on transport failure. */
  request<K extends ApiRouteKey>(route: K, ...args: ApiCallArgs<K>): Promise<ApiCallResult<K>>;
}

/**
 * Type-level path resolution: substitutes `{name}` segments of a route
 * template with the supplied parameters. Used by implementations and tests
 * to build exact URLs without runtime string logic in the contracts.
 */
export type ResolveApiPath<
  Path extends string,
  P extends Record<string, string>
> = Path extends `${infer Head}{${infer Param}}${infer Tail}`
  ? Param extends keyof P
    ? `${Head}${P[Param]}${ResolveApiPath<Tail, P>}`
    : never
  : Path;

/** Resolved URL path for a route key with concrete parameters. */
export type ResolvedRoutePath<K extends ApiRouteKey> = ResolveApiPath<
  import('./routes').ApiRoutePath<K>,
  ApiRouteParams<K>
>;
