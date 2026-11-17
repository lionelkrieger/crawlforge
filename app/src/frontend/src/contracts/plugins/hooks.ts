/**
 * Plugin lifecycle hook bus contract.
 *
 * Spec surface: FS-CRAWL-002 §6.5 (plugin lifecycle hooks), §8.8 (the
 * normalized store is the contract surface for plugin lifecycle hooks),
 * §7.4 (onDataUpdate relays are coalesced and dropped under backpressure;
 * listeners detach on tab deactivation).
 *
 * The four lifecycle hooks: onLoad, onTabActivate, onDataUpdate,
 * onCrawlComplete. Hooks receive read-only contexts only (E5).
 *
 * @module contracts/plugins/hooks
 */

import type { AuditCounters } from '../api/audits';
import type { PluginManifest } from '../api/plugins';
import type { PluginBridge, PluginDataUpdate } from './bridge';

/** The four plugin lifecycle hook names (contract lock). */
export const PLUGIN_LIFECYCLE_HOOKS = [
  'onLoad',
  'onTabActivate',
  'onDataUpdate',
  'onCrawlComplete'
] as const;

/** Union of the lifecycle hook names. */
export type PluginLifecycleHookName = (typeof PLUGIN_LIFECYCLE_HOOKS)[number];

/** Context delivered to onLoad: identity, manifest, and the read-only bridge. */
export interface PluginLoadContext {
  readonly pluginId: string;
  readonly manifest: PluginManifest;
  readonly bridge: PluginBridge;
}

/** Context delivered to onTabActivate when a plugin tab becomes active. */
export interface PluginTabContext {
  readonly pluginId: string;
  readonly tabId: string;
  /** Report surface identifier hosting the tab. */
  readonly surface: string;
  readonly activeAuditId: string | null;
}

/** Context delivered to onCrawlComplete (mirrors audit-completed). */
export interface PluginCrawlCompleteContext {
  readonly pluginId: string;
  readonly auditId: string;
  readonly finalCounters: AuditCounters;
  readonly reportReady: boolean;
}

/**
 * Plugin-side lifecycle hook implementations. All hooks are optional; the
 * bus invokes only what a plugin registers. Handlers must not mutate any
 * context object (E5: read-only data + UI contribution rights only).
 */
export interface PluginLifecycleHooks {
  /** Called once when the plugin is loaded and its bridge is ready. */
  onLoad?(context: PluginLoadContext): void | Promise<void>;
  /** Called when a tab contributed by the plugin becomes active. */
  onTabActivate?(context: PluginTabContext): void;
  /** Called with coalesced read-only slice/hub-event relays. */
  onDataUpdate?(update: PluginDataUpdate): void;
  /** Called once per audit completion the plugin is subscribed to. */
  onCrawlComplete?(context: PluginCrawlCompleteContext): void;
}

/**
 * Hook bus contract (store-side). Registration returns a disposer; the bus
 * detaches all of a plugin's listeners on unload or tab deactivation.
 */
export interface PluginHookBus {
  register(pluginId: string, hooks: PluginLifecycleHooks): () => void;
  unregister(pluginId: string): void;
  /** Hook names this bus relays (always the four contract hooks). */
  readonly supportedHooks: readonly PluginLifecycleHookName[];
}

/** Compile-time lock: the hook interface covers exactly the four hooks. */
export type AssertHookNamesExhaustive = [
  Exclude<keyof PluginLifecycleHooks, PluginLifecycleHookName>
] extends [never]
  ? [Exclude<PluginLifecycleHookName, keyof PluginLifecycleHooks>] extends [never]
    ? true
    : never
  : never;
