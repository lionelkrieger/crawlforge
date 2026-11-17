/**
 * plugin.json manifest validator (FS-CRAWL-002 §6.3).
 *
 * Implements the bundled JSON schema (assets/plugin-schema/plugin.schema.json)
 * as dependency-free structural validation: field shapes, lowercase
 * reverse-DNS ids, semver versions, local-only relative entry/styles paths
 * (no traversal, no absolute paths, no remote URLs — E1), network permission
 * shape, contribution declarations, hook subset, and label maps.
 *
 * Incompatible plugins (engine.minAppVersion above the product version) are
 * disabled with a diagnostic, never silently loaded.
 *
 * @module plugins/manifestValidator
 */

import type { PluginManifest, PluginPermission } from '../contracts/api/plugins';
import type { PluginLifecycleHookName } from '../contracts/plugins/hooks';
import {
  MANIFEST_HOOK_NAMES,
  type PluginContributions,
  type PluginNetworkPermission,
  type SpecPluginManifest
} from './manifestSchema';

/** One validation problem (field path + reason). */
export interface ManifestValidationIssue {
  readonly field: string;
  readonly reason: string;
}

/** Validation outcome: a validated manifest or a non-empty issue list. */
export type ManifestValidation =
  | { readonly ok: true; readonly manifest: SpecPluginManifest }
  | { readonly ok: false; readonly issues: readonly ManifestValidationIssue[] };

const ID_PATTERN = /^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$/;
const SEMVER_PATTERN = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$/;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

/** True when a relative path stays inside the plugin directory (local only). */
export function isLocalRelativePath(value: string): boolean {
  if (value.length === 0) return false;
  if (value.includes('..')) return false;
  if (value.startsWith('/') || value.startsWith('\\')) return false;
  if (/^[A-Za-z]:/.test(value)) return false;
  if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(value)) return false; // any scheme (http:, file:, ...)
  return true;
}

function validateNetwork(value: unknown, issues: ManifestValidationIssue[]): PluginNetworkPermission | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value)) {
    issues.push({ field: 'permissions.network', reason: 'must be an object' });
    return undefined;
  }
  const mode = value.mode;
  if (mode !== 'none' && mode !== 'destinations') {
    issues.push({ field: 'permissions.network.mode', reason: 'must be "none" or "destinations"' });
    return undefined;
  }
  const refsRaw = value.destinationRefs ?? [];
  if (!Array.isArray(refsRaw) || refsRaw.some((ref) => !isNonEmptyString(ref))) {
    issues.push({ field: 'permissions.network.destinationRefs', reason: 'must be an array of non-empty strings' });
    return undefined;
  }
  const refs = refsRaw as string[];
  if (new Set(refs).size !== refs.length) {
    issues.push({ field: 'permissions.network.destinationRefs', reason: 'must be unique' });
    return undefined;
  }
  if (mode === 'destinations' && refs.length === 0) {
    issues.push({ field: 'permissions.network.destinationRefs', reason: 'destinations mode requires at least one reference' });
    return undefined;
  }
  return { mode, destinationRefs: refs };
}

function validateContributions(value: unknown, issues: ManifestValidationIssue[]): PluginContributions | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value)) {
    issues.push({ field: 'contributes', reason: 'must be an object' });
    return undefined;
  }
  const result: {
    tabs?: { id: string; label: string; icon?: string }[];
    columnFormatters?: { target: string; columnKey: string }[];
    filterPresets?: { name: string; ruleTree: unknown }[];
  } = {};

  if (value.tabs !== undefined) {
    if (!Array.isArray(value.tabs)) {
      issues.push({ field: 'contributes.tabs', reason: 'must be an array' });
    } else {
      const tabs: { id: string; label: string; icon?: string }[] = [];
      value.tabs.forEach((tab, index) => {
        if (!isRecord(tab) || !isNonEmptyString(tab.id) || !isNonEmptyString(tab.label)) {
          issues.push({ field: `contributes.tabs[${index}]`, reason: 'requires non-empty id and label' });
          return;
        }
        if (tab.icon !== undefined && !isNonEmptyString(tab.icon)) {
          issues.push({ field: `contributes.tabs[${index}].icon`, reason: 'must be a non-empty string' });
          return;
        }
        tabs.push({ id: tab.id, label: tab.label, ...(tab.icon !== undefined ? { icon: tab.icon } : {}) });
      });
      result.tabs = tabs;
    }
  }

  if (value.columnFormatters !== undefined) {
    if (!Array.isArray(value.columnFormatters)) {
      issues.push({ field: 'contributes.columnFormatters', reason: 'must be an array' });
    } else {
      const formatters: { target: string; columnKey: string }[] = [];
      value.columnFormatters.forEach((formatter, index) => {
        if (!isRecord(formatter) || !isNonEmptyString(formatter.target) || !isNonEmptyString(formatter.columnKey)) {
          issues.push({ field: `contributes.columnFormatters[${index}]`, reason: 'requires non-empty target and columnKey' });
          return;
        }
        formatters.push({ target: formatter.target, columnKey: formatter.columnKey });
      });
      result.columnFormatters = formatters;
    }
  }

  if (value.filterPresets !== undefined) {
    if (!Array.isArray(value.filterPresets)) {
      issues.push({ field: 'contributes.filterPresets', reason: 'must be an array' });
    } else {
      const presets: { name: string; ruleTree: unknown }[] = [];
      value.filterPresets.forEach((preset, index) => {
        if (!isRecord(preset) || !isNonEmptyString(preset.name) || !('ruleTree' in preset)) {
          issues.push({ field: `contributes.filterPresets[${index}]`, reason: 'requires non-empty name and a ruleTree' });
          return;
        }
        presets.push({ name: preset.name, ruleTree: preset.ruleTree });
      });
      result.filterPresets = presets;
    }
  }

  return result;
}

function validateLabels(value: unknown, issues: ManifestValidationIssue[]): SpecPluginManifest['labels'] | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value)) {
    issues.push({ field: 'labels', reason: 'must be an object of locale label maps' });
    return undefined;
  }
  const labels: Record<string, Record<string, string>> = {};
  for (const [locale, map] of Object.entries(value)) {
    if (!isRecord(map) || Object.values(map).some((text) => typeof text !== 'string')) {
      issues.push({ field: `labels.${locale}`, reason: 'must map label keys to strings' });
      continue;
    }
    labels[locale] = map as Record<string, string>;
  }
  return labels;
}

/** Validates a raw plugin.json payload against the §6.3 manifest contract. */
export function validatePluginManifest(raw: unknown): ManifestValidation {
  const issues: ManifestValidationIssue[] = [];
  if (!isRecord(raw)) {
    return { ok: false, issues: [{ field: '$', reason: 'manifest must be a JSON object' }] };
  }

  const id = raw.id;
  if (!isNonEmptyString(id) || !ID_PATTERN.test(id)) {
    issues.push({ field: 'id', reason: 'must be a lowercase reverse-DNS-style identifier (at least two segments)' });
  }
  const name = raw.name;
  if (!isNonEmptyString(name)) issues.push({ field: 'name', reason: 'required non-empty string' });
  const version = raw.version;
  if (!isNonEmptyString(version) || !SEMVER_PATTERN.test(version)) {
    issues.push({ field: 'version', reason: 'must be a semantic version' });
  }
  const description = raw.description;
  if (!isNonEmptyString(description)) issues.push({ field: 'description', reason: 'required non-empty string' });
  if (raw.author !== undefined && typeof raw.author !== 'string') {
    issues.push({ field: 'author', reason: 'must be a string when present' });
  }

  let minAppVersion: string | null = null;
  if (!isRecord(raw.engine) || !isNonEmptyString(raw.engine.minAppVersion) || !SEMVER_PATTERN.test(raw.engine.minAppVersion)) {
    issues.push({ field: 'engine.minAppVersion', reason: 'required semantic version' });
  } else {
    minAppVersion = raw.engine.minAppVersion;
  }

  const entry = raw.entry;
  if (!isNonEmptyString(entry) || !isLocalRelativePath(entry)) {
    issues.push({ field: 'entry', reason: 'required relative local path inside the plugin directory' });
  }
  let styles: string | undefined;
  if (raw.styles !== undefined) {
    if (!isNonEmptyString(raw.styles) || !isLocalRelativePath(raw.styles)) {
      issues.push({ field: 'styles', reason: 'must be a relative local path when present' });
    } else {
      styles = raw.styles;
    }
  }

  let network: PluginNetworkPermission | undefined;
  if (raw.permissions !== undefined) {
    if (!isRecord(raw.permissions)) {
      issues.push({ field: 'permissions', reason: 'must be an object' });
    } else {
      network = validateNetwork(raw.permissions.network, issues);
    }
  }

  const contributes = validateContributions(raw.contributes, issues);
  const labels = validateLabels(raw.labels, issues);

  let hooks: PluginLifecycleHookName[] | undefined;
  if (raw.hooks !== undefined) {
    if (!Array.isArray(raw.hooks) || raw.hooks.some((hook) => !MANIFEST_HOOK_NAMES.includes(hook as PluginLifecycleHookName))) {
      issues.push({ field: 'hooks', reason: 'must be a subset of onLoad/onTabActivate/onDataUpdate/onCrawlComplete' });
    } else if (new Set(raw.hooks).size !== raw.hooks.length) {
      issues.push({ field: 'hooks', reason: 'must be unique' });
    } else {
      hooks = raw.hooks as PluginLifecycleHookName[];
    }
  }

  if (issues.length > 0 || minAppVersion === null) {
    return { ok: false, issues };
  }

  const manifest: SpecPluginManifest = {
    id: id as string,
    name: name as string,
    version: version as string,
    description: description as string,
    ...(typeof raw.author === 'string' ? { author: raw.author } : {}),
    engine: { minAppVersion },
    entry: entry as string,
    ...(styles !== undefined ? { styles } : {}),
    ...(network !== undefined ? { permissions: { network } } : {}),
    ...(contributes !== undefined ? { contributes } : {}),
    ...(hooks !== undefined ? { hooks } : {}),
    ...(labels !== undefined ? { labels } : {})
  };
  return { ok: true, manifest };
}

/** Compares two semantic versions: -1, 0, or 1 (build metadata ignored). */
export function compareSemver(a: string, b: string): number {
  const parse = (value: string) => {
    const match = SEMVER_PATTERN.exec(value);
    if (!match) return null;
    return {
      core: [Number(match[1]), Number(match[2]), Number(match[3])] as const,
      pre: match[4] ?? null
    };
  };
  const left = parse(a);
  const right = parse(b);
  if (!left || !right) return 0;
  for (let i = 0; i < 3; i += 1) {
    if (left.core[i] !== right.core[i]) return left.core[i] < right.core[i] ? -1 : 1;
  }
  if (left.pre === null && right.pre === null) return 0;
  if (left.pre === null) return 1;
  if (right.pre === null) return -1;
  const leftParts = left.pre.slice(1).split('.');
  const rightParts = right.pre.slice(1).split('.');
  const length = Math.max(leftParts.length, rightParts.length);
  for (let i = 0; i < length; i += 1) {
    const l = leftParts[i];
    const r = rightParts[i];
    if (l === undefined) return -1;
    if (r === undefined) return 1;
    const lNum = /^[0-9]+$/.test(l) ? Number(l) : null;
    const rNum = /^[0-9]+$/.test(r) ? Number(r) : null;
    if (lNum !== null && rNum !== null) {
      if (lNum !== rNum) return lNum < rNum ? -1 : 1;
    } else if (lNum !== null) {
      return -1;
    } else if (rNum !== null) {
      return 1;
    } else if (l !== r) {
      return l < r ? -1 : 1;
    }
  }
  return 0;
}

/** Compatibility outcome against the running product version. */
export interface CompatibilityResult {
  readonly compatible: boolean;
  readonly reason: string | null;
}

/** Checks engine.minAppVersion against the product version (§6.3). */
export function checkCompatibility(manifest: SpecPluginManifest, appVersion: string): CompatibilityResult {
  if (compareSemver(manifest.engine.minAppVersion, appVersion) > 0) {
    return {
      compatible: false,
      reason: `requires app version ${manifest.engine.minAppVersion} or newer (running ${appVersion})`
    };
  }
  return { compatible: true, reason: null };
}

/**
 * Projects the spec manifest onto the locked wire DTO for the store bus.
 * Read-only data access is the baseline right; UI contribution rights follow
 * declared contributions. The mutation-oriented permission is never granted
 * (E5; the backend rejects it with 400).
 */
export function toBridgeManifest(manifest: SpecPluginManifest): PluginManifest {
  const permissions: PluginPermission[] = ['read-data'];
  const contributes = manifest.contributes;
  const hasContributions =
    (contributes?.tabs?.length ?? 0) > 0 ||
    (contributes?.columnFormatters?.length ?? 0) > 0 ||
    (contributes?.filterPresets?.length ?? 0) > 0;
  if (hasContributions) permissions.push('ui-contributions');
  return {
    id: manifest.id,
    name: manifest.name,
    version: manifest.version,
    description: manifest.description,
    permissions,
    ...(manifest.labels !== undefined ? { labels: manifest.labels } : {})
  };
}