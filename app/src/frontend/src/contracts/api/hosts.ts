/**
 * Per-host AIMD state DTOs (additive, FS-CRAWL-002 §3.7).
 *
 * Routes: GET /api/audits/{id}/hosts, POST /api/audits/{id}/hosts/{host}/override.
 * Mirrors the `host-throttle` hub event fields so the store can reconcile
 * live adjustments with the polled state.
 *
 * @module contracts/api/hosts
 */

import type { IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/** One AIMD adjustment record (adjustment history). */
export interface HostAdjustment {
  readonly timestamp: IsoTimestamp;
  /** Adjustment trigger key (e.g. ttfb-spike, error-burst, manual). */
  readonly trigger: string;
  readonly rateBefore: number;
  readonly rateAfter: number;
  readonly concurrencyBefore: number;
  readonly concurrencyAfter: number;
  readonly ttfbSampleMs: number | null;
}

/** Manual host-rate pin; null fields mean "no pin". */
export interface HostRateOverride {
  readonly pinnedRate: number | null;
  readonly pinnedConcurrency: number | null;
}

/** Per-host AIMD state row. */
export interface HostState {
  readonly host: string;
  /** Current effective requests-per-second rate. */
  readonly effectiveRate: number;
  readonly concurrency: number;
  /** EWMA-smoothed TTFB in milliseconds. */
  readonly ttfbEwmaMs: number | null;
  /** Rolling baseline rate the controller adapts around. */
  readonly rollingBaselineRate: number;
  readonly throttled: boolean;
  readonly override: HostRateOverride | null;
  /** Present when includeHistory=true. */
  readonly history?: readonly HostAdjustment[];
}

/** GET /api/audits/{id}/hosts — optional adjustment history. */
export interface HostListQuery extends ListQuery {
  readonly includeHistory?: boolean;
}

/**
 * POST /api/audits/{id}/hosts/{host}/override — pin/clear manual host rate.
 * Null clears the pin and returns the host to AIMD control.
 */
export interface HostOverrideRequest {
  readonly rate: number | null;
  readonly concurrency?: number | null;
}
