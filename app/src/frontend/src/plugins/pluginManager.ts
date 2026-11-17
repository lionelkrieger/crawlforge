/**
 * Plugin lifecycle manager (FS-CRAWL-002 §6.2/§6.5/§6.6).
 *
 * Loads enabled, trusted, compatible plugins into sandboxed frames and wires
 * their lifecycle hooks through the ACCEPTED store bus (onLoad once,
 * onTabActivate, coalesced onDataUpdate, exactly-once onCrawlComplete). A
 * failing plugin is isolated: its frame is marked failed in the registry and
 * surfaced in the management UI — the shell and the audit never crash.
 * Disabling/unloading detaches the bus registration, every relay
 * subscription, and the frame itself.
 *
 * @module plugins/pluginManager
 */

import type { CrawlForgePluginBus } from '../store/pluginBus';
import type { PluginLifecycleHooks } from '../contracts/plugins/hooks';
import { toBridgeManifest } from './manifestValidator';
import { grantedConnectOrigins, type DestinationRule } from './networkGrantGuard';
import type { PluginRegistry, UiPluginEntry } from './pluginRegistry';
import { SandboxBridgeHost, type PluginContributionEvent } from './sandboxBridge';
import { createSandboxFrame } from './sandboxFrame';
import type { HostToFrameMessage } from './sandboxProtocol';

/** Manager construction options. */
export interface PluginManagerOptions {
  readonly registry: PluginRegistry;
  readonly bus: CrawlForgePluginBus;
  /** Document hosting the frames (defaults to the global document). */
  readonly doc?: Document;
  /** Container the frames attach to (defaults to doc.body). */
  readonly container?: HTMLElement;
  /** Local entry module text reader (shell binds fs; tests bind fakes). */
  readonly entrySource: (directory: string, entry: string) => Promise<string | null>;
  /** Optional local stylesheet text reader. */
  readonly stylesSource?: (directory: string, styles: string) => Promise<string | null>;
  readonly destinations?: readonly DestinationRule[];
  readonly onContribution?: (event: PluginContributionEvent) => void;
}

interface LoadedPlugin {
  readonly entry: UiPluginEntry;
  readonly frame: HTMLIFrameElement;
  readonly host: SandboxBridgeHost;
  readonly detachBus: () => void;
  readonly detachMessages: () => void;
}

/** Orchestrates sandboxed plugin loading, hooks, and isolation. */
export class PluginManager {
  private readonly options: PluginManagerOptions;
  private readonly doc: Document;
  private readonly container: HTMLElement;
  private readonly destinations: readonly DestinationRule[];
  private readonly loaded = new Map<string, LoadedPlugin>();
  private readonly unsubscribeRegistry: () => void;
  private disposed = false;

  constructor(options: PluginManagerOptions) {
    this.options = options;
    this.doc = options.doc ?? globalThis.document;
    this.container = options.container ?? this.doc.body;
    this.destinations = options.destinations ?? options.registry.destinationRules;
    this.unsubscribeRegistry = options.registry.subscribe(() => this.reconcile());
  }

  /** Ids currently loaded into frames (deterministic order). */
  getLoadedIds(): readonly string[] {
    return [...this.loaded.keys()].sort();
  }

  /** The frame for one loaded plugin (tests / management surface). */
  getFrame(pluginId: string): HTMLIFrameElement | null {
    return this.loaded.get(pluginId)?.frame ?? null;
  }

  /** Loads every enabled, trusted, loadable plugin; failures are isolated. */
  async loadEnabled(): Promise<void> {
    for (const entry of this.options.registry.getSnapshot().entries) {
      if (this.isLoadable(entry)) {
        await this.loadPlugin(entry.id);
      }
    }
  }

  private isLoadable(entry: UiPluginEntry): boolean {
    return entry.enabled && entry.trusted && entry.state !== 'disabled' && entry.state !== 'failed';
  }

  /** Loads one plugin into a sandboxed frame; never throws into the shell. */
  async loadPlugin(pluginId: string): Promise<void> {
    if (this.disposed || this.loaded.has(pluginId)) return;
    const entry = this.options.registry.getSnapshot().entries.find((candidate) => candidate.id === pluginId);
    if (!entry || !this.isLoadable(entry)) return;

    try {
      const entryCode = await this.options.entrySource(entry.directory, entry.manifest.entry);
      if (entryCode === null) {
        this.options.registry.markFailed(pluginId, `entry module not found: ${entry.manifest.entry}`);
        return;
      }
      let styles: string | undefined;
      if (entry.manifest.styles && this.options.stylesSource) {
        styles = (await this.options.stylesSource(entry.directory, entry.manifest.styles)) ?? undefined;
      }

      const origins = grantedConnectOrigins(entry.networkGrant, this.destinations);
      const frame = createSandboxFrame(this.doc, {
        pluginId,
        entryCode,
        ...(styles !== undefined ? { styles } : {}),
        networkOrigins: origins
      });
      this.container.appendChild(frame);

      const bridge = this.options.bus.getBridge(pluginId, entry.permissions);
      const postToFrame = (message: HostToFrameMessage): void => {
        frame.contentWindow?.postMessage(message, '*');
      };
      const host = new SandboxBridgeHost({
        pluginId,
        manifest: entry.manifest,
        bridge,
        networkGrant: entry.networkGrant,
        destinations: this.destinations,
        postToFrame,
        ...(this.options.onContribution ? { onContribution: this.options.onContribution } : {}),
        onError: (message) => this.options.registry.markFailed(pluginId, message)
      });

      const hooks: PluginLifecycleHooks = {
        onLoad: (context) => host.deliverHook('onLoad', { pluginId: context.pluginId, manifest: context.manifest }),
        onTabActivate: (context) => host.deliverHook('onTabActivate', context),
        onDataUpdate: (update) => host.deliverHook('onDataUpdate', update),
        onCrawlComplete: (context) => host.deliverHook('onCrawlComplete', context)
      };
      const detachBus = this.options.bus.loadPlugin(pluginId, toBridgeManifest(entry.manifest), hooks);

      const onMessage = (event: MessageEvent): void => {
        host.handleMessage(event.data, event.source as Window | null, frame.contentWindow);
      };
      this.doc.defaultView?.addEventListener('message', onMessage);
      const detachMessages = () => this.doc.defaultView?.removeEventListener('message', onMessage);

      this.loaded.set(pluginId, { entry, frame, host, detachBus, detachMessages });
      this.options.registry.markLoaded(pluginId);
    } catch (error) {
      this.options.registry.markFailed(pluginId, error instanceof Error ? error.message : String(error));
    }
  }

  /** Unloads one plugin: bus, relays, message listener, and frame all detach. */
  unload(pluginId: string): void {
    const record = this.loaded.get(pluginId);
    if (!record) return;
    this.loaded.delete(pluginId);
    record.detachBus();
    record.host.dispose();
    record.detachMessages();
    record.frame.remove();
  }

  /** Routes one window message to the matching plugin host (test entry). */
  handleFrameMessage(pluginId: string, message: unknown, source: Window | null): void {
    const record = this.loaded.get(pluginId);
    if (!record) return;
    record.host.handleMessage(message, source, record.frame.contentWindow);
  }

  private reconcile(): void {
    if (this.disposed) return;
    const entries = this.options.registry.getSnapshot().entries;
    for (const pluginId of [...this.loaded.keys()]) {
      const entry = entries.find((candidate) => candidate.id === pluginId);
      if (!entry || !this.isLoadable(entry)) {
        this.unload(pluginId);
      }
    }
  }

  /** Unloads everything and detaches the registry subscription. */
  dispose(): void {
    this.disposed = true;
    for (const pluginId of [...this.loaded.keys()]) {
      this.unload(pluginId);
    }
    this.unsubscribeRegistry();
  }
}