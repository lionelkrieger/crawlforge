/**
 * Shared primitive contract types for the CrawlForge loopback API / hub surface.
 *
 * Spec surface: FS-CRAWL-001 §10.1 (REST conventions), FS-CRAWL-002 §8 (UI stack).
 * Clean-room: no identity, account, license, or entitlement concepts exist in
 * any contract type (exclusions #1–#8, E6).
 *
 * @module contracts/common/primitives
 */

/** HTTP methods used by the loopback REST contract (FS-CRAWL-001 §10.2). */
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

/**
 * Opaque backend-assigned entity identifier. Identifiers are local database
 * keys only — they never encode machine, user, or account identity.
 */
export type EntityId = string;

/** ISO-8601 UTC timestamp string, e.g. `2026-11-14T06:28:43Z`. */
export type IsoTimestamp = string;

/** JSON scalar values permitted in dynamic payloads. */
export type JsonPrimitive = string | number | boolean | null;

/** Arbitrary JSON value (used for dynamic extraction columns and evidence). */
export type JsonValue = JsonPrimitive | JsonValue[] | { readonly [key: string]: JsonValue };

/**
 * Request payload for routes that accept no input fields.
 * Kept as an empty object (not `undefined`) so every route contract has a
 * uniform `{ params, request, response }` shape.
 */
export type EmptyRequest = Readonly<Record<string, never>>;

/**
 * Recursive partial: models "any subset of settings fields" updates
 * (PUT /api/settings, FS-CRAWL-001 §10.2) without weakening read shapes.
 */
export type DeepPartial<T> = T extends JsonPrimitive
  ? T
  : T extends readonly (infer U)[]
    ? readonly DeepPartial<U>[]
    : T extends object
      ? { [K in keyof T]?: DeepPartial<T[K]> }
      : T;

/**
 * Deep readonly view. Plugin relays and selector snapshots expose state as
 * `DeepReadonly<T>` so plugins can never mutate store slices (E5).
 */
export type DeepReadonly<T> = T extends JsonPrimitive
  ? T
  : T extends readonly (infer U)[]
    ? readonly DeepReadonly<U>[]
    : T extends object
      ? { readonly [K in keyof T]: DeepReadonly<T[K]> }
      : T;
