/**
 * Uniform envelope + pagination contract tests (FS-CRAWL-001 §10.1, §10.4).
 * Shape/guard tests and pagination invariants.
 */

import { describe, expect, it } from 'vitest';
import {
  isApiEnvelope,
  isApiError,
  isEnvelopeError,
  isPaginationMeta,
  isSuccessEnvelope
} from '../../../src/frontend/src/contracts';
import type {
  ApiEnvelope,
  ApiErrorEnvelope,
  ApiSuccessEnvelope,
  PaginationMeta
} from '../../../src/frontend/src/contracts';
import { Equal, Expect, errorEnvelope, paginatedEnvelope, successEnvelope } from './helpers';

describe('uniform response envelope', () => {
  it('accepts success, paginated, and error envelopes', () => {
    expect(isApiEnvelope(successEnvelope)).toBe(true);
    expect(isApiEnvelope(paginatedEnvelope)).toBe(true);
    expect(isApiEnvelope(errorEnvelope)).toBe(true);
  });

  it('accepts a null data payload (failure shape)', () => {
    expect(isApiEnvelope({ data: null })).toBe(true);
  });

  it('rejects values without the required data member', () => {
    expect(isApiEnvelope({})).toBe(false);
    expect(isApiEnvelope({ pagination: { offset: 0, limit: 10, total: 0 } })).toBe(false);
    expect(isApiEnvelope(null)).toBe(false);
    expect(isApiEnvelope(undefined)).toBe(false);
    expect(isApiEnvelope('data')).toBe(false);
    expect(isApiEnvelope([])).toBe(false);
  });

  it('rejects malformed error objects', () => {
    expect(isApiEnvelope({ data: null, error: { code: 42, message: 'x' } })).toBe(false);
    expect(isApiEnvelope({ data: null, error: { code: 'x' } })).toBe(false);
    expect(isApiEnvelope({ data: null, error: 'boom' })).toBe(false);
  });

  it('rejects malformed pagination metadata', () => {
    expect(isApiEnvelope({ data: [], pagination: { offset: 0, limit: 10 } })).toBe(false);
    expect(isApiEnvelope({ data: [], pagination: 'page-1' })).toBe(false);
  });

  it('narrows success vs error envelopes', () => {
    const envelopes: ApiEnvelope<unknown>[] = [successEnvelope, errorEnvelope, paginatedEnvelope];
    const successes = envelopes.filter(isSuccessEnvelope);
    const failures = envelopes.filter(isEnvelopeError);
    expect(successes).toHaveLength(2);
    expect(failures).toHaveLength(1);
    expect(isSuccessEnvelope(errorEnvelope)).toBe(false);
    expect(isEnvelopeError(successEnvelope)).toBe(false);
  });

  it('narrows types at compile time', () => {
    const envelope: ApiEnvelope<{ ready: boolean }> = successEnvelope;
    if (isSuccessEnvelope(envelope)) {
      type Checks = [
        Expect<Equal<typeof envelope, ApiSuccessEnvelope<{ ready: boolean }>>>,
        Expect<Equal<typeof envelope.data, { ready: boolean }>>
      ];
      const checks: Checks = [true, true];
      expect(checks.every(Boolean)).toBe(true);
      expect(envelope.data.ready).toBe(true);
    } else {
      expect.unreachable('fixture must be a success envelope');
    }
    const failure: ApiEnvelope<null> = errorEnvelope;
    if (isEnvelopeError(failure)) {
      type Checks = [Expect<Equal<typeof failure, ApiErrorEnvelope>>];
      const checks: Checks = [true];
      expect(checks.every(Boolean)).toBe(true);
      expect(failure.error.code).toBe('validation-failed');
    } else {
      expect.unreachable('fixture must be an error envelope');
    }
  });
});

describe('ApiError guard (field-level validation reasons, §10.4)', () => {
  it('accepts code + message with optional fieldErrors', () => {
    expect(isApiError({ code: 'not-found', message: 'Missing.' })).toBe(true);
    expect(isApiError(errorEnvelope.error)).toBe(true);
  });

  it('rejects missing or non-string code/message', () => {
    expect(isApiError({ message: 'Missing.' })).toBe(false);
    expect(isApiError({ code: 'x' })).toBe(false);
    expect(isApiError({ code: 1, message: 2 })).toBe(false);
    expect(isApiError(null)).toBe(false);
  });

  it('rejects malformed fieldErrors', () => {
    expect(isApiError({ code: 'x', message: 'y', fieldErrors: { name: 'empty' } })).toBe(false);
    expect(isApiError({ code: 'x', message: 'y', fieldErrors: { name: [1, 2] } })).toBe(false);
    expect(isApiError({ code: 'x', message: 'y', fieldErrors: [] })).toBe(false);
  });
});

describe('pagination invariants (offset/limit, §10.1)', () => {
  it('accepts valid metadata', () => {
    expect(isPaginationMeta({ offset: 0, limit: 50, total: 500_000 })).toBe(true);
    expect(isPaginationMeta({ offset: 100, limit: 1, total: 100 })).toBe(true);
  });

  it('rejects negative or fractional values', () => {
    expect(isPaginationMeta({ offset: -1, limit: 10, total: 0 })).toBe(false);
    expect(isPaginationMeta({ offset: 0, limit: 10, total: -5 })).toBe(false);
    expect(isPaginationMeta({ offset: 0.5, limit: 10, total: 0 })).toBe(false);
    expect(isPaginationMeta({ offset: 0, limit: 2.5, total: 0 })).toBe(false);
  });

  it('rejects a zero limit (a window must hold at least one row)', () => {
    expect(isPaginationMeta({ offset: 0, limit: 0, total: 0 })).toBe(false);
  });

  it('rejects non-numeric or missing members', () => {
    expect(isPaginationMeta({ offset: '0', limit: 10, total: 0 })).toBe(false);
    expect(isPaginationMeta({ offset: 0, limit: 10 })).toBe(false);
    expect(isPaginationMeta({ offset: 0, limit: Number.NaN, total: 0 })).toBe(false);
    expect(isPaginationMeta({ offset: 0, limit: Number.POSITIVE_INFINITY, total: 0 })).toBe(false);
  });

  it('keeps the wire shape exact at compile time', () => {
    type Checks = [
      Expect<Equal<keyof PaginationMeta, 'offset' | 'limit' | 'total'>>,
      Expect<Equal<PaginationMeta['offset'], number>>,
      Expect<Equal<ApiEnvelope<unknown>['data'], unknown>>
    ];
    const checks: Checks = [true, true, true];
    expect(checks.every(Boolean)).toBe(true);
  });
});
