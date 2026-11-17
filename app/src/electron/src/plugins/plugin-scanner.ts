/**
 * Plugin scan insertion point (WP-06-01, plugin spec §6.2): AFTER main-window
 * hydration, the shell scans the per-user plugin directory and the workspace
 * plugins directory, validates manifests locally, asks for trust consent per
 * plugin, and hands approved candidates to the plugin registry.
 *
 * The registry contract itself belongs to WP-06-06 — this module defines only
 * the local insertion point and the minimal delegate interface. No plugin
 * code is executed here; scanning is manifest-only and fully offline. No
 * runtime downloads (exclusion E2): candidates come from local directories.
 */
import { joinPath } from '../handshake/port-discovery';
import { FileSystemPort, ShellLogger } from '../types';

/** Minimal plugin manifest shape validated at the insertion point. */
export interface PluginManifest {
  readonly id: string;
  readonly name: string;
  readonly version: string;
  readonly entry: string;
  readonly apiVersion: number;
  readonly description?: string;
}

/** Where a candidate was discovered. */
export type PluginSource = 'user' | 'workspace';

/** One validated plugin candidate awaiting trust consent. */
export interface PluginCandidate {
  readonly manifest: PluginManifest;
  readonly directory: string;
  readonly manifestPath: string;
  readonly source: PluginSource;
}

/** A scan problem (local diagnostics only). */
export interface PluginScanError {
  readonly path: string;
  readonly reason: string;
}

/** Outcome of one post-hydration scan pass. */
export interface PluginScanResult {
  readonly registered: readonly PluginCandidate[];
  readonly declined: readonly PluginCandidate[];
  readonly errors: readonly PluginScanError[];
}

/** Trust consent hook (Electron main binds a native dialog; tests bind fakes). */
export type TrustPrompt = (candidate: PluginCandidate) => Promise<boolean>;

/**
 * Minimal registry delegate implemented by WP-06-06. The insertion point only
 * requires registration of trusted candidates; the full registry contract
 * (loading, sandboxing, lifecycle) is that workpiece's scope.
 */
export interface PluginRegistryDelegate {
  register(candidate: PluginCandidate): Promise<void>;
}

/** Supported manifest API version at this insertion point. */
export const SUPPORTED_PLUGIN_API_VERSION = 1;

/** Manifest file name inside each plugin directory. */
export const PLUGIN_MANIFEST_FILE = 'plugin.json';

/** Scans local plugin directories and registers trusted candidates. */
export class PluginScanner {
  private readonly fs: FileSystemPort;
  private readonly logger: ShellLogger;
  private readonly trustPrompt: TrustPrompt;
  private readonly registry: PluginRegistryDelegate | null;

  constructor(
    fs: FileSystemPort,
    logger: ShellLogger,
    trustPrompt: TrustPrompt,
    registry: PluginRegistryDelegate | null = null
  ) {
    this.fs = fs;
    this.logger = logger;
    this.trustPrompt = trustPrompt;
    this.registry = registry;
  }

  /**
   * Runs the §6.2 insertion pass. MUST be called only after main-window
   * hydration; calling earlier throws so the ordering is enforced, not
   * merely documented.
   */
  async scanAfterHydration(
    dataDirectory: string,
    workspaceDirectory: string | null,
    hydrationComplete: boolean
  ): Promise<PluginScanResult> {
    if (!hydrationComplete) {
      throw new Error('Plugin scan must run after main-window hydration (§6.2).');
    }

    const registered: PluginCandidate[] = [];
    const declined: PluginCandidate[] = [];
    const errors: PluginScanError[] = [];

    const roots: { readonly path: string; readonly source: PluginSource }[] = [
      { path: joinPath(dataDirectory, 'plugins'), source: 'user' }
    ];
    if (workspaceDirectory !== null) {
      roots.push({ path: joinPath(workspaceDirectory, 'plugins'), source: 'workspace' });
    }

    for (const root of roots) {
      let entries: readonly string[];
      try {
        entries = await this.fs.listDirectory(root.path);
      } catch {
        continue; // Directory absent — nothing to scan.
      }

      for (const entry of entries) {
        const directory = joinPath(root.path, entry);
        const manifestPath = joinPath(directory, PLUGIN_MANIFEST_FILE);
        const candidate = await this.readCandidate(manifestPath, directory, root.source, errors);
        if (candidate === null) {
          continue;
        }

        let trusted = false;
        try {
          trusted = await this.trustPrompt(candidate);
        } catch (error) {
          this.logger.error(`Trust prompt failed for ${candidate.manifest.id}.`, error);
        }

        if (!trusted) {
          declined.push(candidate);
          continue;
        }

        if (this.registry !== null) {
          try {
            await this.registry.register(candidate);
            registered.push(candidate);
          } catch (error) {
            errors.push({
              path: manifestPath,
              reason: `registry registration failed: ${error instanceof Error ? error.message : String(error)}`
            });
          }
        } else {
          // WP-06-06 registry not yet bound: consented candidates are reported
          // but not loaded. The insertion point stays inert by design.
          registered.push(candidate);
        }
      }
    }

    return { registered, declined, errors };
  }

  private async readCandidate(
    manifestPath: string,
    directory: string,
    source: PluginSource,
    errors: PluginScanError[]
  ): Promise<PluginCandidate | null> {
    let text: string | null;
    try {
      text = await this.fs.readTextFile(manifestPath);
    } catch {
      return null;
    }

    if (text === null) {
      return null; // Not a plugin directory.
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      errors.push({ path: manifestPath, reason: 'manifest is not valid JSON' });
      return null;
    }

    const validation = validateManifest(parsed);
    if (!validation.ok) {
      errors.push({ path: manifestPath, reason: validation.reason });
      return null;
    }

    return { manifest: validation.manifest, directory, manifestPath, source };
  }
}

/** Manifest validation result. */
export type ManifestValidation =
  | { readonly ok: true; readonly manifest: PluginManifest }
  | { readonly ok: false; readonly reason: string };

/** Validates the manifest shape (no code execution, local only). */
export function validateManifest(raw: unknown): ManifestValidation {
  if (typeof raw !== 'object' || raw === null) {
    return { ok: false, reason: 'manifest must be an object' };
  }

  const record = raw as Record<string, unknown>;
  const id = record.id;
  if (typeof id !== 'string' || !/^[a-z0-9][a-z0-9-]*[a-z0-9]$|^[a-z0-9]$/.test(id)) {
    return { ok: false, reason: 'id must be a lowercase kebab-case identifier' };
  }

  if (typeof record.name !== 'string' || record.name.length === 0) {
    return { ok: false, reason: 'name is required' };
  }

  if (typeof record.version !== 'string' || !/^\d+\.\d+\.\d+/.test(record.version)) {
    return { ok: false, reason: 'version must be semver-like' };
  }

  if (typeof record.entry !== 'string' || record.entry.length === 0) {
    return { ok: false, reason: 'entry is required' };
  }

  if (record.entry.includes('..') || record.entry.startsWith('/') || /^[a-zA-Z]:/.test(record.entry)) {
    return { ok: false, reason: 'entry must be a relative path inside the plugin directory' };
  }

  if (record.apiVersion !== SUPPORTED_PLUGIN_API_VERSION) {
    return { ok: false, reason: `apiVersion must be ${SUPPORTED_PLUGIN_API_VERSION}` };
  }

  const manifest: PluginManifest = {
    id,
    name: record.name,
    version: record.version,
    entry: record.entry,
    apiVersion: SUPPORTED_PLUGIN_API_VERSION,
    description: typeof record.description === 'string' ? record.description : undefined
  };
  return { ok: true, manifest };
}
