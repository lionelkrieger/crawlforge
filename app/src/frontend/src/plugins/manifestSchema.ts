/**
 * Plugin manifest contract types + the bundled JSON schema asset.
 *
 * FS-CRAWL-002 §6.3: the manifest is a local user-authored artifact. The
 * canonical declarative schema ships at assets/plugin-schema/plugin.schema.json
 * and is imported here as the single source of truth; the structural validator
 * (manifestValidator.ts) implements its semantics without extra dependencies.
 *
 * Clean-room (E1/E6): local-only discovery, no remote channel, no licensing or
 * feature gating of any kind.
 *
 * @module plugins/manifestSchema
 */

import pluginManifestSchema from '../../../../assets/plugin-schema/plugin.schema.json';
import type { PluginLifecycleHookName } from '../contracts/plugins/hooks';
import type { PluginLabelMaps } from '../contracts/api/plugins';

/** The bundled JSON schema asset (draft 2020-12). */
export const PLUGIN_MANIFEST_SCHEMA = pluginManifestSchema;

/** Network permission declaration (§6.3): default mode is none. */
export interface PluginNetworkPermission {
  readonly mode: 'none' | 'destinations';
  readonly destinationRefs: readonly string[];
}

/** One declared report tab contribution. */
export interface PluginTabContribution {
  readonly id: string;
  readonly label: string;
  readonly icon?: string;
}

/** One declared column formatter contribution (dynamic columns included). */
export interface PluginColumnFormatterContribution {
  readonly target: string;
  readonly columnKey: string;
}

/** One declared filter preset contribution (query-builder rule tree). */
export interface PluginFilterPresetContribution {
  readonly name: string;
  readonly ruleTree: unknown;
}

/** UI contribution declarations (§6.3 contributes). */
export interface PluginContributions {
  readonly tabs?: readonly PluginTabContribution[];
  readonly columnFormatters?: readonly PluginColumnFormatterContribution[];
  readonly filterPresets?: readonly PluginFilterPresetContribution[];
}

/** Minimum product version requirement. */
export interface PluginEngineRequirement {
  readonly minAppVersion: string;
}

/**
 * Validated spec manifest (FS-CRAWL-002 §6.3). Distinct from the locked wire
 * DTO {@link import('../contracts/api/plugins').PluginManifest}: this is the
 * full on-disk contract; the wire DTO is the registry projection.
 */
export interface SpecPluginManifest {
  readonly id: string;
  readonly name: string;
  readonly version: string;
  readonly description: string;
  readonly author?: string;
  readonly engine: PluginEngineRequirement;
  readonly entry: string;
  readonly styles?: string;
  readonly permissions?: { readonly network?: PluginNetworkPermission };
  readonly contributes?: PluginContributions;
  readonly hooks?: readonly PluginLifecycleHookName[];
  readonly labels?: PluginLabelMaps;
}

/** The four contract hook names accepted in manifest `hooks`. */
export const MANIFEST_HOOK_NAMES: readonly PluginLifecycleHookName[] = [
  'onLoad',
  'onTabActivate',
  'onDataUpdate',
  'onCrawlComplete'
];