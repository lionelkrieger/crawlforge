/**
 * Runtime type guards for the uniform envelope and pagination contracts.
 *
 * Runtime code in the contracts package is limited to type guards (and
 * frozen declarative catalogs); these guards narrow `unknown` wire values
 * without any `any`-typed escape hatches.
 *
 * @module contracts/common/guards
 */

import type { ApiEnvelope, ApiError, ApiErrorEnvelope, ApiSuccessEnvelope } from './envelope';
import type { PaginationMeta } from './pagination';

/** Narrows an unknown value to a non-null, non-array object record. */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Narrows an unknown value to a string. */
export function isString(value: unknown): value is string {
  return typeof value === 'string';
}

/** Narrows an unknown value to a finite number. */
export function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** Narrows an unknown value to a boolean. */
export function isBoolean(value: unknown): value is boolean {
  return typeof value === 'boolean';
}

/** Narrows an unknown value to a non-negative integer. */
export function isNonNegativeInteger(value: unknown): value is number {
  return isNumber(value) && Number.isInteger(value) && value >= 0;
}

/** Narrows an unknown value to an array whose items satisfy `isItem`. */
export function isArrayOf<T>(value: unknown, isItem: (item: unknown) => item is T): value is T[] {
  return Array.isArray(value) && value.every(isItem);
}

/** Narrows an unknown value to a record of strings (e.g. HTTP headers). */
export function isStringRecord(value: unknown): value is Record<string, string> {
  return isRecord(value) && Object.values(value).every(isString);
}

/** Narrows an unknown value to a record of finite numbers (count maps). */
export function isNumberRecord(value: unknown): value is Record<string, number> {
  return isRecord(value) && Object.values(value).every(isNumber);
}

/** True when the value is absent (undefined or null). */
export function isAbsent(value: unknown): value is null | undefined {
  return value === null || value === undefined;
}

/**
 * Narrows an unknown value to an {@link ApiError}: machine-readable code,
 * human-readable message, and optional field-level validation reasons.
 */
export function isApiError(value: unknown): value is ApiError {
  if (!isRecord(value) || !isString(value.code) || !isString(value.message)) {
    return false;
  }
  if (value.fieldErrors === undefined) {
    return true;
  }
  return (
    isRecord(value.fieldErrors) &&
    Object.values(value.fieldErrors).every((reasons) => isArrayOf(reasons, isString))
  );
}

/**
 * Narrows an unknown value to {@link PaginationMeta}, enforcing the
 * pagination invariants: integer offset >= 0, integer limit >= 1,
 * integer total >= 0.
 */
export function isPaginationMeta(value: unknown): value is PaginationMeta {
  return (
    isRecord(value) &&
    isNonNegativeInteger(value.offset) &&
    isNonNegativeInteger(value.limit) &&
    value.limit >= 1 &&
    isNonNegativeInteger(value.total)
  );
}

/**
 * Narrows an unknown wire value to the uniform {@link ApiEnvelope}.
 * The `data` key must be present (it may be null); pagination and error
 * members are validated when present. The payload itself stays `unknown` —
 * route-specific narrowing is the caller's responsibility.
 */
export function isApiEnvelope(value: unknown): value is ApiEnvelope<unknown> {
  return (
    isRecord(value) &&
    'data' in value &&
    (value.pagination === undefined || isPaginationMeta(value.pagination)) &&
    (value.error === undefined || isApiError(value.error))
  );
}

/** Narrows an envelope to a failure envelope (error object present). */
export function isEnvelopeError(envelope: ApiEnvelope<unknown>): envelope is ApiErrorEnvelope {
  return envelope.error !== undefined;
}

/**
 * Narrows an envelope to a success envelope: no error object and a non-null
 * payload. An absent payload without an error (empty success) is not narrowed.
 */
export function isSuccessEnvelope<TData>(
  envelope: ApiEnvelope<TData>,
): envelope is ApiSuccessEnvelope<TData> {
  return envelope.error === undefined && envelope.data !== null;
}
