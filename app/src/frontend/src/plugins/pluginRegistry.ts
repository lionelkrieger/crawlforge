/**
 * ui_plugins registry (FS-CRAWL-002 §6.2/§6.7, criterion #39).
 *
 * Implements the WP-06-01 PluginRegistryDelegate insertion point: trusted
 * candidates from the post-hydration local scan register here. Holds grants
 * (read-only data / UI contributions via the locked REST routes; network
 * destination grants as LOCAL state per the ratified ruling), SHA-256
 * integrity records, workspace trust consent, and collision precedence
 * (workspace overrides per-user with a visible warning).
 *
 * Failure isolation: registration, validation, and REST errors are recorded
 * on entries/diagnostics and NEVER thrown into the shell. No remote registry,
 * marketplace, download, or auto-update channel exists (E1); no licensing or
 * feature gating (E6).
 *
 * @module plugins/pluginRegistry
 */

import type { LoopbackApiClient } from '../contracts/api/client';
import type { ApiError } from '../contracts/common/envelope';
import type {
  PluginLoadState,
  PluginPermission,
  PluginRegistryEntry
} from '../contracts/api/plugins';
import type { SpecPluginManifest } from './manifestSchema';
import { checkCompatibility, toBridgeManifest, validatePluginManifest } from './manifestValidator';
import {
  compareIntegrity,
  computeIntegrityHashes,
  UNKNOWN_INTEGRITY,
  type IntegrityRecord,
  type IntegrityStatus
} from './integrity';
import { NO_NETWORK_GRANT, type DestinationRule, type NetworkGrant } from './networkGrantGuard';

/** Where a candidate was discovered (mirrors the WP-06-01 insertion point). */
export type PluginSource = 'user' | 'workspace';

/**
 * Candidate shape handed over by the shell scanner (structurally compatible
 * with WP-06-01's PluginCandidate; the frontend never imports electron code).
 */
export interface PluginCandidate {
  readonly manifest: {
    readonly id: string;
    readonly name: string;
    readonly version: string;
    readonly entry: string;
    readonly apiVersion: number;
    readonly description?: string;
  };
  readonly directory: string;
  readonly manifestPath: string;
  readonly source: PluginSource;
}

/** The WP-06-01 delegate contract this registry satisfies. */
export interface PluginRegistryDelegate {
  register(candidate: PluginCandidate): Promise<void>;
}

/** One registered plugin with grants, integrity, and trust state. */
export interface UiPluginEntry {
  readonly id: string;
  readonly manifest: SpecPluginManifest;
  readonly directory: string;
  readonly source: PluginSource;
  readonly state: PluginLoadState;
  readonly enabled: boolean;
  readonly permissions: readonly PluginPermission[];
  readonly loadError: string | null;
  readonly discoveredAt: string;
  readonly networkGrant: NetworkGrant;
  readonly integrity: IntegrityRecord;
  /** Workspace plugin awaiting one-time local trust consent. */
  readonly trusted: boolean;
  /** True when this workspace entry overrode a per-user plugin of the same id. */
  readonly collisionOverride: boolean;
}

/** Registry snapshot for the management surface (stable between changes). */
export interface PluginRegistrySnapshot {
  readonly entries: readonly UiPluginEntry[];
  readonly warnings: readonly string[];
}

/** Result of a management operation; errors are surfaced, never thrown. */
export type RegistryOperationResult =
  | { readonly ok: true }
  | { readonly ok: false; readonly error: string; readonly fieldErrors?: Readonly<Record<string, readonly string[]>> };

/** Injectable ports and configuration. */
export interface PluginRegistryOptions {
  readonly client: LoopbackApiClient;
  readonly appVersion: string;
  /** Full plugin.json reader (shell binds fs; tests bind fakes). */
  readonly manifestSource?: (directory: string) => Promise<unknown | null>;
  /** Entry module text reader for integrity hashing. */
  readonly entrySource?: (directory: string, entry: string) => Promise<string | null>;
  /** User-configured destination rules the network grants may reference. */
  readonly destinations?: readonly DestinationRule[];
  readonly now?: () => string;
}

const COMMANDS_PERMISSION: PluginPermission = 'commands';

function synthesizeManifest(candidate: PluginCandidate): SpecPluginManifest {
  return {
    id: candidate.manifest.id,
    name: candidate.manifest.name,
    version: candidate.manifest.version,
    description: candidate.manifest.description ?? candidate.manifest.name,
    engine: { minAppVersion: '0.0.0' },
    entry: candidate.manifest.entry
  };
}

function fromWireEntry(entry: PluginRegistryEntry): UiPluginEntry {
  const manifest: SpecPluginManifest = {
    id: entry.id,
    name: entry.name,
    version: entry.version,
    description: entry.description ?? entry.name,
    engine: { minAppVersion: '0.0.0' },
    entry: 'index.js'
  };
  return {
    id: entry.id,
    manifest,
    directory: entry.directory,
    source: 'user',
    state: entry.state,
    enabled: entry.enabled,
    permissions: entry.permissions,
    loadError: entry.loadError,
    discoveredAt: entry.discoveredAt,
    networkGrant: NO_NETWORK_GRANT,
    integrity: UNKNOWN_INTEGRITY,
    trusted: true,
    collisionOverride: false
  };
}

/** The local plugin registry with grants, integrity, and trust consent. */
export class PluginRegistry implements PluginRegistryDelegate {
  private readonly client: LoopbackApiClient;
  private readonly appVersion: string;
  private readonly manifestSource?: (directory: string) => Promise<unknown | null>;
  private readonly entrySource?: (directory: string, entry: string) => Promise<string | null>;
  private readonly destinations: readonly DestinationRule[];
  private readonly now: () => string;

  private readonly entries = new Map<string, UiPluginEntry>();
  private readonly warnings: string[] = [];
  private readonly listeners = new Set<() => void>();
  private snapshot: PluginRegistrySnapshot = { entries: [], warnings: [] };

  constructor(options: PluginRegistryOptions) {
    this.client = options.client;
    this.appVersion = options.appVersion;
    this.manifestSource = options.manifestSource;
    this.entrySource = options.entrySource;
    this.destinations = options.destinations ?? [];
    this.now = options.now ?? (() => new Date().toISOString());
  }

  /** User destination rules available for network grant review. */
  get destinationRules(): readonly DestinationRule[] {
    return this.destinations;
  }

  /** Stable snapshot for useSyncExternalStore. */
  getSnapshot = (): PluginRegistrySnapshot => this.snapshot;

  /** Subscribes to registry changes; returns a disposer. */
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  private publish(): void {
    this.snapshot = {
      entries: [...this.entries.values()].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)),
      warnings: [...this.warnings]
    };
    for (const listener of [...this.listeners]) listener();
  }

  private replace(entry: UiPluginEntry): void {
    this.entries.set(entry.id, entry);
    this.publish();
  }

  /**
   * Delegate entry point: registers one trusted local candidate. Validation,
   * compatibility, trust, collision, and integrity problems are recorded —
   * this method never rejects into the shell.
   */
  async register(candidate: PluginCandidate): Promise<void> {
    try {
      await this.registerCore(candidate);
    } catch (error) {
      this.recordFailure(candidate.manifest.id, candidate, error);
    }
  }

  private async registerCore(candidate: PluginCandidate): Promise<void> {
    const previous = this.entries.get(candidate.manifest.id);

    let raw: unknown = null;
    if (this.manifestSource) {
      raw = await this.manifestSource(candidate.directory);
    }
    const validation = raw === null
      ? { ok: true as const, manifest: synthesizeManifest(candidate) }
      : validatePluginManifest(raw);

    if (!validation.ok) {
      const message = validation.issues.map((issue) => `${issue.field}: ${issue.reason}`).join('; ');
      this.replace(this.buildEntry(candidate, synthesizeManifest(candidate), {
        state: 'failed',
        loadError: `invalid manifest — ${message}`,
        previous
      }));
      return;
    }

    const manifest = validation.manifest;
    const compatibility = checkCompatibility(manifest, this.appVersion);
    if (!compatibility.compatible) {
      this.replace(this.buildEntry(candidate, manifest, {
        state: 'disabled',
        loadError: `incompatible — ${compatibility.reason}`,
        previous
      }));
      return;
    }

    // Collision precedence: workspace-local overrides per-user (§6.2).
    let collisionOverride = false;
    if (previous && previous.source !== candidate.source) {
      if (candidate.source === 'workspace') {
        collisionOverride = true;
        this.warnings.push(`Workspace plugin "${manifest.id}" overrides the per-user plugin of the same id.`);
      } else {
        // Per-user candidate loses to an existing workspace entry.
        this.warnings.push(`Per-user plugin "${manifest.id}" ignored: a workspace plugin with the same id takes precedence.`);
        this.publish();
        return;
      }
    }

    // Integrity: hash manifest + entry; a content change revokes network grants.
    let entryContent: string | null = null;
    if (this.entrySource) {
      entryContent = await this.entrySource(candidate.directory, manifest.entry);
    }
    const hashes = await computeIntegrityHashes(raw ?? manifest, entryContent);
    const status = compareIntegrity(previous?.integrity ?? null, hashes);
    const integrity: IntegrityRecord = hashes ? { ...hashes, status } : UNKNOWN_INTEGRITY;
    if (status === 'changed') {
      this.warnings.push(`Plugin "${manifest.id}" content changed since the last review; network grant revoked pending re-review.`);
    }

    const networkGrant = this.resolveNetworkGrant(manifest, previous, status);
    const trusted = candidate.source === 'workspace' ? (previous?.trusted ?? false) : true;

    this.replace(this.buildEntry(candidate, manifest, {
      state: previous?.state === 'failed' ? 'pending' : (previous?.state ?? 'pending'),
      loadError: null,
      previous,
      collisionOverride,
      trusted,
      integrity,
      networkGrant
    }));
  }

  private resolveNetworkGrant(
    manifest: SpecPluginManifest,
    previous: UiPluginEntry | undefined,
    status: IntegrityStatus
  ): NetworkGrant {
    const declared = manifest.permissions?.network;
    if (!declared || declared.mode === 'none') return NO_NETWORK_GRANT;
    if (status === 'changed') {
      return { mode: 'destinations', destinationRefs: declared.destinationRefs, granted: false };
    }
    if (previous && previous.networkGrant.mode === 'destinations') {
      return { ...previous.networkGrant, destinationRefs: declared.destinationRefs };
    }
    return { mode: 'destinations', destinationRefs: declared.destinationRefs, granted: false };
  }

  private buildEntry(
    candidate: PluginCandidate,
    manifest: SpecPluginManifest,
    overrides: {
      state: PluginLoadState;
      loadError: string | null;
      previous?: UiPluginEntry;
      collisionOverride?: boolean;
      trusted?: boolean;
      integrity?: IntegrityRecord;
      networkGrant?: NetworkGrant;
    }
  ): UiPluginEntry {
    const previous = overrides.previous;
    return {
      id: manifest.id,
      manifest,
      directory: candidate.directory,
      source: candidate.source,
      state: overrides.state,
      enabled: previous?.enabled ?? true,
      permissions: previous?.permissions ?? toBridgeManifest(manifest).permissions,
      loadError: overrides.loadError,
      discoveredAt: previous?.discoveredAt ?? this.now(),
      networkGrant: overrides.networkGrant ?? previous?.networkGrant ?? NO_NETWORK_GRANT,
      integrity: overrides.integrity ?? previous?.integrity ?? UNKNOWN_INTEGRITY,
      trusted: overrides.trusted ?? (candidate.source === 'user'),
      collisionOverride: overrides.collisionOverride ?? false
    };
  }

  private recordFailure(id: string, candidate: PluginCandidate, error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    this.replace(this.buildEntry(candidate, synthesizeManifest(candidate), {
      state: 'failed',
      loadError: message,
      previous: this.entries.get(id)
    }));
  }

  /** One-time local trust consent for a workspace plugin (§6.2). */
  trustWorkspacePlugin(id: string): void {
    const entry = this.entries.get(id);
    if (!entry || entry.source !== 'workspace' || entry.trusted) return;
    this.replace({ ...entry, trusted: true });
  }

  /** Marks a plugin loaded (manager callback after successful frame setup). */
  markLoaded(id: string): void {
    const entry = this.entries.get(id);
    if (!entry) return;
    this.replace({ ...entry, state: 'loaded', loadError: null });
  }

  /** Records a plugin failure; the shell and the audit are never affected. */
  markFailed(id: string, error: string): void {
    const entry = this.entries.get(id);
    if (!entry) return;
    this.replace({ ...entry, state: 'failed', loadError: error });
  }

  /** Enable/disable through the locked PUT route. */
  async setEnabled(id: string, enabled: boolean): Promise<RegistryOperationResult> {
    const entry = this.entries.get(id);
    if (!entry) return { ok: false, error: `unknown plugin: ${id}` };
    try {
      const envelope = await this.client.request('plugins.update', { params: { id }, body: { enabled } });
      if (envelope.error) return this.envelopeError(envelope.error);
      this.replace({ ...entry, enabled, state: enabled ? 'pending' : 'disabled' });
      return { ok: true };
    } catch (error) {
      return { ok: false, error: this.transportError(error) };
    }
  }

  /** Grants a read-only/UI permission through the locked PUT route. */
  async grantPermission(id: string, permission: PluginPermission): Promise<RegistryOperationResult> {
    if (permission === COMMANDS_PERMISSION) {
      return { ok: false, error: 'mutation-oriented permission is not grantable (E5); the backend rejects it with 400' };
    }
    return this.updatePermissions(id, { grantPermissions: [permission] });
  }

  /** Revokes a permission through the locked PUT route. */
  async revokePermission(id: string, permission: PluginPermission): Promise<RegistryOperationResult> {
    return this.updatePermissions(id, { revokePermissions: [permission] });
  }

  private async updatePermissions(
    id: string,
    body: { grantPermissions?: PluginPermission[]; revokePermissions?: PluginPermission[] }
  ): Promise<RegistryOperationResult> {
    const entry = this.entries.get(id);
    if (!entry) return { ok: false, error: `unknown plugin: ${id}` };
    try {
      const envelope = await this.client.request('plugins.update', { params: { id }, body });
      if (envelope.error) return this.envelopeError(envelope.error);
      let permissions = [...entry.permissions];
      for (const permission of body.grantPermissions ?? []) {
        if (!permissions.includes(permission)) permissions.push(permission);
      }
      for (const permission of body.revokePermissions ?? []) {
        permissions = permissions.filter((existing) => existing !== permission);
      }
      this.replace({ ...entry, permissions });
      return { ok: true };
    } catch (error) {
      return { ok: false, error: this.transportError(error) };
    }
  }

  /**
   * Grants network access to user destination rules (LOCAL registry state per
   * the ratified ruling — the locked PUT contract carries no network fields).
   */
  grantNetwork(id: string, destinationRefs: readonly string[]): RegistryOperationResult {
    const entry = this.entries.get(id);
    if (!entry) return { ok: false, error: `unknown plugin: ${id}` };
    if (destinationRefs.length === 0) {
      return { ok: false, error: 'at least one destination reference is required' };
    }
    const unknownRefs = destinationRefs.filter((ref) => !this.destinations.some((rule) => rule.ref === ref));
    if (unknownRefs.length > 0) {
      return { ok: false, error: `unknown destination references: ${unknownRefs.join(', ')}` };
    }
    this.replace({
      ...entry,
      networkGrant: { mode: 'destinations', destinationRefs: [...destinationRefs], granted: true }
    });
    return { ok: true };
  }

  /** Revokes the network grant (references stay visible for re-review). */
  revokeNetwork(id: string): RegistryOperationResult {
    const entry = this.entries.get(id);
    if (!entry) return { ok: false, error: `unknown plugin: ${id}` };
    this.replace({ ...entry, networkGrant: { ...entry.networkGrant, granted: false } });
    return { ok: true };
  }

  /** Rescan through the locked POST route; merges the backend registry view. */
  async rescan(): Promise<{ ok: true; added: number; removed: number } | { ok: false; error: string }> {
    try {
      const envelope = await this.client.request('plugins.rescan', { body: {} });
      if (envelope.error) return { ok: false, error: envelope.error.message };
      const response = envelope.data;
      if (!response) return { ok: false, error: 'empty rescan response' };
      this.mergeWireEntries(response.plugins);
      return { ok: true, added: response.added, removed: response.removed };
    } catch (error) {
      return { ok: false, error: this.transportError(error) };
    }
  }

  /** Removes a registry entry through the locked DELETE route (files remain). */
  async remove(id: string): Promise<RegistryOperationResult> {
    if (!this.entries.has(id)) return { ok: false, error: `unknown plugin: ${id}` };
    try {
      const envelope = await this.client.request('plugins.delete', { params: { id } });
      if (envelope.error) return this.envelopeError(envelope.error);
      this.entries.delete(id);
      this.publish();
      return { ok: true };
    } catch (error) {
      return { ok: false, error: this.transportError(error) };
    }
  }

  /** Pulls the backend registry view (enabled flags, load state). */
  async syncFromBackend(): Promise<RegistryOperationResult> {
    try {
      const envelope = await this.client.request('plugins.list', {});
      if (envelope.error) return this.envelopeError(envelope.error);
      if (!envelope.data) return { ok: false, error: 'empty registry response' };
      this.mergeWireEntries(envelope.data);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: this.transportError(error) };
    }
  }

  private mergeWireEntries(wireEntries: readonly PluginRegistryEntry[]): void {
    for (const wire of wireEntries) {
      const local = this.entries.get(wire.id);
      if (local) {
        this.entries.set(wire.id, {
          ...local,
          enabled: wire.enabled,
          state: wire.state,
          loadError: wire.loadError,
          permissions: wire.permissions
        });
      } else {
        this.entries.set(wire.id, fromWireEntry(wire));
      }
    }
    this.publish();
  }

  private envelopeError(error: ApiError): RegistryOperationResult {
    return {
      ok: false,
      error: `${error.code}: ${error.message}`,
      ...(error.fieldErrors ? { fieldErrors: error.fieldErrors } : {})
    };
  }

  private transportError(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
  }
}