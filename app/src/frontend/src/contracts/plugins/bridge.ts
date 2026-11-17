/**
 * Plugin bridge contract — coalesced read-only relays of store slices and
 * hub events (FS-CRAWL-002 §6.4, §8.8).
 *
 * Clean-room (E5): plugins receive read-only data + UI contribution rights
 * only. The bridge exposes no mutation surface — no setState, no API client,
 * no crawl lifecycle or configuration access. Relays are coalesced and
 * dropped under backpressure (§7.4); listeners detach on tab deactivation.
 *
 * @module contracts/plugins/bridge
 */

import type { DeepReadonly } from '../common/primitives';
import type { CrawlForgeState } from '../store/state';
import type { HubServerEvent } from '../hub/events';
import type { PluginPermission } from '../api/plugins';

/**
 * Store slices relayable to plugins. The hub connection slice is not
 * relayed as a slice; hub events flow through the 'hub-event' update kind.
 */
export type PluginRelaySliceName =
  | 'settings'
  | 'projects'
  | 'audits'
  | 'urlPages'
  | 'findingPages'
  | 'linkPages'
  | 'ngramPages'
  | 'hostThrottle'
  | 'systemHealth'
  | 'plugins';

/** Deeply readonly projection of the relayable slices (E5: read-only). */
export type PluginReadableState = DeepReadonly<Pick<CrawlForgeState, PluginRelaySliceName>>;

/**
 * One coalesced relay update. `droppedSinceLastRelay` / `coalescedCount`
 * expose backpressure accounting: under load, intermediate updates are
 * dropped and only the latest state is delivered (§7.4).
 */
export type PluginDataUpdate =
  | {
      readonly kind: 'slice';
      readonly slice: PluginRelaySliceName;
      /** Monotonic store revision the snapshot was taken at. */
      readonly revision: number;
      readonly droppedSinceLastRelay: number;
    }
  | {
      readonly kind: 'hub-event';
      readonly event: HubServerEvent;
      /** Number of events coalesced into this relay tick. */
      readonly coalescedCount: number;
    };

/**
 * Read-only plugin bridge. Implementations enforce the plugin's granted
 * permissions; ungranted slices are never exposed.
 */
export interface PluginBridge {
  readonly pluginId: string;
  readonly permissions: readonly PluginPermission[];
  /** Snapshot of all relayable slices the plugin may read. */
  getState(): PluginReadableState;
  /** Snapshot of one relayable slice. */
  getSlice<K extends PluginRelaySliceName>(slice: K): PluginReadableState[K];
  /**
   * Subscribe to coalesced updates. The returned disposer detaches the
   * listener (called on tab deactivation, §7.4).
   */
  subscribe(listener: (update: PluginDataUpdate) => void): () => void;
}
