/**
 * Plugin registry DTOs (additive, FS-CRAWL-002 §6.7).
 *
 * Routes: GET /api/plugins, PUT /api/plugins/{id}, POST /api/plugins/rescan,
 * DELETE /api/plugins/{id}.
 *
 * Clean-room (E1, E5, E6): plugins are local user-authored artifacts only —
 * no remote registry/marketplace, no auto-update, no remote code loading.
 * Permissions grant read-only data + UI contribution rights only; never
 * crawl lifecycle or configuration mutations. No plugin licensing/accounts.
 *
 * @module contracts/api/plugins
 */

import type { EntityId, IsoTimestamp } from '../common/primitives';

/**
 * Plugin permission keys. Read-only data access, UI contributions, and
 * command registration are the only grantable rights (E5).
 */
export type PluginPermission = 'read-data' | 'ui-contributions' | 'commands';

/** Plugin load state in the local registry. */
export type PluginLoadState = 'pending' | 'loaded' | 'failed' | 'disabled';

/**
 * Localized label maps from the plugin manifest (FS-CRAWL-002 §6.3):
 * locale -> label key -> localized text.
 */
export type PluginLabelMaps = Readonly<Record<string, Readonly<Record<string, string>>>>;

/** Local plugin manifest (user-authored artifact). */
export interface PluginManifest {
  readonly id: EntityId;
  readonly name: string;
  readonly version: string;
  readonly description?: string | null;
  readonly permissions: readonly PluginPermission[];
  readonly labels?: PluginLabelMaps;
}

/** One plugin registry entry with load state. */
export interface PluginRegistryEntry {
  readonly id: EntityId;
  readonly name: string;
  readonly version: string;
  readonly description: string | null;
  /** Local directory the plugin was discovered in. */
  readonly directory: string;
  readonly state: PluginLoadState;
  readonly enabled: boolean;
  readonly permissions: readonly PluginPermission[];
  readonly loadError: string | null;
  readonly discoveredAt: IsoTimestamp;
}

/** PUT /api/plugins/{id} — enable/disable; grant/revoke permissions. */
export interface PluginUpdateRequest {
  readonly enabled?: boolean;
  readonly grantPermissions?: readonly PluginPermission[];
  readonly revokePermissions?: readonly PluginPermission[];
}

/** POST /api/plugins/rescan — rescan plugin directories (local only). */
export interface PluginRescanResponse {
  readonly added: number;
  readonly removed: number;
  readonly plugins: readonly PluginRegistryEntry[];
}
