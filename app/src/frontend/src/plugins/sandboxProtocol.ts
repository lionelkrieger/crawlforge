/**
 * Sandboxed plugin bridge protocol (FS-CRAWL-002 §6.4, E5, criterion #39).
 *
 * The ONLY channel between a plugin frame and the shell. Capabilities are
 * read-only data access, declared UI contributions, and granted-destination
 * network requests. There is no mutation capability of any kind: unknown or
 * mutation-shaped requests are rejected, never dispatched.
 *
 * @module plugins/sandboxProtocol
 */

import type { PluginLifecycleHookName } from '../contracts/plugins/hooks';
import type { PluginPermission } from '../contracts/api/plugins';

/** Wire marker shared by both directions (frames are opaque-origin). */
export const SANDBOX_PROTOCOL = 'crawlforge-plugin-v1';

/** Host → frame messages. */
export type HostToFrameMessage =
  | { readonly protocol: typeof SANDBOX_PROTOCOL; readonly type: 'hook'; readonly hook: PluginLifecycleHookName; readonly context: unknown }
  | { readonly protocol: typeof SANDBOX_PROTOCOL; readonly type: 'response'; readonly requestId: number; readonly ok: true; readonly data: unknown }
  | { readonly protocol: typeof SANDBOX_PROTOCOL; readonly type: 'response'; readonly requestId: number; readonly ok: false; readonly error: string };

/** Frame → host messages. */
export type FrameToHostMessage =
  | { readonly protocol: typeof SANDBOX_PROTOCOL; readonly type: 'request'; readonly requestId: number; readonly capability: string; readonly payload?: unknown }
  | { readonly protocol: typeof SANDBOX_PROTOCOL; readonly type: 'error'; readonly message: string };

/** Read-only data capabilities (require the read-data permission). */
export const READ_CAPABILITIES = ['data.getState', 'data.getSlice', 'data.subscribe'] as const;

/** UI contribution capabilities (require ui-contributions + a declaration). */
export const UI_CAPABILITIES = ['ui.registerTab', 'ui.registerColumnFormatter', 'ui.registerFilterPreset'] as const;

/** The single network capability (requires an explicit destination grant). */
export const NETWORK_CAPABILITY = 'network.fetch';

/** Every capability the bridge knows; anything else is rejected. */
export const KNOWN_CAPABILITIES: readonly string[] = [...READ_CAPABILITIES, ...UI_CAPABILITIES, NETWORK_CAPABILITY];

/** True when the capability exists in the contract. */
export function isKnownCapability(capability: string): boolean {
  return KNOWN_CAPABILITIES.includes(capability);
}

/** The permission a capability requires, or null for the grant-gated network path. */
export function capabilityPermission(capability: string): PluginPermission | null {
  if ((READ_CAPABILITIES as readonly string[]).includes(capability)) return 'read-data';
  if ((UI_CAPABILITIES as readonly string[]).includes(capability)) return 'ui-contributions';
  return null;
}

/** Type guard for incoming frame messages (untrusted input). */
export function isFrameToHostMessage(value: unknown): value is FrameToHostMessage {
  if (typeof value !== 'object' || value === null) return false;
  const record = value as Record<string, unknown>;
  if (record.protocol !== SANDBOX_PROTOCOL) return false;
  if (record.type === 'request') {
    return typeof record.requestId === 'number' && typeof record.capability === 'string';
  }
  if (record.type === 'error') {
    return typeof record.message === 'string';
  }
  return false;
}