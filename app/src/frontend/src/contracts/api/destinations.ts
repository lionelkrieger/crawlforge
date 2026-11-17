/**
 * Export destination DTOs.
 *
 * Routes: GET/POST /api/destinations, PUT/DELETE /api/destinations/{id},
 * POST /api/destinations/{id}/test.
 *
 * Clean-room: destinations are user-owned (local/network/encrypted targets
 * configured by the user). Credentials are never returned in clear
 * (FS-CRAWL-001 §10.2); they are used solely for the destination connection.
 *
 * @module contracts/api/destinations
 */

import type { EntityId, IsoTimestamp } from '../common/primitives';
import type { ListQuery } from '../common/pagination';

/**
 * Destination summary. `kind` is a local destination-type key (e.g. local
 * folder, network share, encrypted remote); `secretMask` indicates a stored
 * credential without revealing it.
 *
 * WP-06-A3 (additive): `enabled` exposes the domain IsEnabled flag the
 * delivery engine already honors (disabled destinations are skipped);
 * naming matches the schedules/plugins/waf wire convention.
 */
export interface Destination {
  readonly id: EntityId;
  readonly name: string;
  readonly kind: string;
  /** Target path/URL of the user-configured destination. */
  readonly target: string;
  readonly username: string | null;
  /** Masked credential indicator; never the clear secret. */
  readonly secretMask: string | null;
  /** Whether delivery to this destination is enabled (WP-06-A3). */
  readonly enabled: boolean;
  readonly createdAt: IsoTimestamp;
  readonly updatedAt: IsoTimestamp;
}

/** GET /api/destinations — pagination. */
export type DestinationListQuery = ListQuery;

/** POST /api/destinations — destination fields (section 8.5). */
export interface DestinationCreateRequest {
  readonly name: string;
  readonly kind: string;
  readonly target: string;
  readonly username?: string;
  /** Accepted in clear on write; never echoed back. */
  readonly secret?: string;
}

/**
 * PUT /api/destinations/{id} — destination fields (subset merge: absent
 * fields keep their stored values). WP-06-A3 (additive): `enabled` toggles
 * delivery; absent keeps the current state.
 */
export interface DestinationUpdateRequest {
  readonly name?: string;
  readonly kind?: string;
  readonly target?: string;
  readonly username?: string | null;
  readonly secret?: string;
  /** Toggle delivery enablement; absent = keep (WP-06-A3). */
  readonly enabled?: boolean;
}

/** POST /api/destinations/{id}/test — reachability and credential verdict. */
export interface DestinationTestResponse {
  readonly reachable: boolean;
  readonly credentialsValid: boolean;
  readonly message: string | null;
}
