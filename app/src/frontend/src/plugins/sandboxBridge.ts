/**
 * Host side of the sandboxed plugin bridge (FS-CRAWL-002 §6.4, E5, #39).
 *
 * Dispatches frame capability requests against the accepted read-only store
 * bridge, declared UI contributions, and the network grant guard. There is
 * NO mutation surface: unknown capabilities are rejected, data access is
 * read-only, UI contributions must be declared in the manifest, and network
 * requests require an explicit grant restricted to user destination rules.
 * Every handler failure is isolated and surfaced, never thrown into the shell.
 *
 * @module plugins/sandboxBridge
 */

import type { FetchLike } from '../contracts/api/client';
import type { PluginBridge, PluginDataUpdate, PluginRelaySliceName } from '../contracts/plugins/bridge';
import type { PluginLifecycleHookName } from '../contracts/plugins/hooks';
import type { SpecPluginManifest } from './manifestSchema';
import { isNetworkAllowed, type DestinationRule, type NetworkGrant } from './networkGrantGuard';
import {
  capabilityPermission,
  isFrameToHostMessage,
  isKnownCapability,
  NETWORK_CAPABILITY,
  SANDBOX_PROTOCOL,
  type HostToFrameMessage
} from './sandboxProtocol';

/** A validated UI contribution event raised to the shell. */
export type PluginContributionEvent =
  | { readonly kind: 'tab'; readonly pluginId: string; readonly id: string; readonly label: string; readonly icon?: string }
  | { readonly kind: 'columnFormatter'; readonly pluginId: string; readonly target: string; readonly columnKey: string }
  | { readonly kind: 'filterPreset'; readonly pluginId: string; readonly name: string; readonly ruleTree: unknown };

/** Bridge host construction options. */
export interface SandboxBridgeOptions {
  readonly pluginId: string;
  readonly manifest: SpecPluginManifest;
  /** The accepted store-bus bridge (read-only, permission-gated). */
  readonly bridge: PluginBridge;
  readonly networkGrant: NetworkGrant;
  readonly destinations: readonly DestinationRule[];
  readonly postToFrame: (message: HostToFrameMessage) => void;
  readonly onContribution?: (event: PluginContributionEvent) => void;
  readonly onError?: (message: string) => void;
  /** Injectable fetch for granted destination requests (tests bind fakes). */
  readonly fetchImpl?: FetchLike;
}

const RELAY_SLICES: readonly PluginRelaySliceName[] = [
  'audits', 'findingPages', 'hostThrottle', 'linkPages', 'ngramPages',
  'plugins', 'projects', 'settings', 'systemHealth', 'urlPages'
];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Capability dispatcher for one sandboxed plugin frame. */
export class SandboxBridgeHost {
  private readonly options: SandboxBridgeOptions;
  private readonly subscriptions: (() => void)[] = [];
  private disposed = false;

  constructor(options: SandboxBridgeOptions) {
    this.options = options;
  }

  /**
   * Handles one untrusted frame message. Only messages from the plugin's own
   * content window are accepted; everything else is dropped silently.
   */
  handleMessage(message: unknown, source: Window | null, expected: Window | null): void {
    if (this.disposed) return;
    if (expected !== null && source !== expected) return;
    if (!isFrameToHostMessage(message)) return;

    if (message.type === 'error') {
      this.options.onError?.(message.message);
      return;
    }

    const requestId = message.requestId;
    void this.dispatch(requestId, message.capability, message.payload).catch((error: unknown) => {
      this.respondError(requestId, error instanceof Error ? error.message : String(error));
    });
  }

  /** Delivers one lifecycle hook context into the frame (bus-driven). */
  deliverHook(hook: PluginLifecycleHookName, context: unknown): void {
    if (this.disposed) return;
    this.options.postToFrame({ protocol: SANDBOX_PROTOCOL, type: 'hook', hook, context });
  }

  /** Detaches every data subscription (unload / tab deactivation). */
  dispose(): void {
    this.disposed = true;
    for (const unsubscribe of this.subscriptions.splice(0)) unsubscribe();
  }

  private async dispatch(requestId: number, capability: string, payload: unknown): Promise<void> {
    if (!isKnownCapability(capability)) {
      this.respondError(requestId, `unknown capability: ${capability}`);
      return;
    }

    const required = capabilityPermission(capability);
    if (required !== null && !this.options.bridge.permissions.includes(required)) {
      this.respondError(requestId, `capability ${capability} requires the ${required} permission`);
      return;
    }

    switch (capability) {
      case 'data.getState':
        this.respondOk(requestId, this.options.bridge.getState());
        return;
      case 'data.getSlice': {
        const slice = isRecord(payload) ? payload.slice : undefined;
        if (typeof slice !== 'string' || !RELAY_SLICES.includes(slice as PluginRelaySliceName)) {
          this.respondError(requestId, 'data.getSlice requires a known slice name');
          return;
        }
        this.respondOk(requestId, this.options.bridge.getSlice(slice as PluginRelaySliceName));
        return;
      }
      case 'data.subscribe': {
        const unsubscribe = this.options.bridge.subscribe((update: PluginDataUpdate) => {
          this.deliverHook('onDataUpdate', update);
        });
        this.subscriptions.push(unsubscribe);
        this.respondOk(requestId, { subscribed: true });
        return;
      }
      case 'ui.registerTab':
        this.handleTabContribution(requestId, payload);
        return;
      case 'ui.registerColumnFormatter':
        this.handleFormatterContribution(requestId, payload);
        return;
      case 'ui.registerFilterPreset':
        this.handlePresetContribution(requestId, payload);
        return;
      case NETWORK_CAPABILITY:
        await this.handleNetworkFetch(requestId, payload);
        return;
      default:
        this.respondError(requestId, `unknown capability: ${capability}`);
    }
  }

  private handleTabContribution(requestId: number, payload: unknown): void {
    if (!isRecord(payload) || typeof payload.id !== 'string') {
      this.respondError(requestId, 'ui.registerTab requires a tab id');
      return;
    }
    const declared = this.options.manifest.contributes?.tabs?.find((tab) => tab.id === payload.id);
    if (!declared) {
      this.respondError(requestId, `tab "${payload.id}" is not declared in the manifest`);
      return;
    }
    this.options.onContribution?.({
      kind: 'tab',
      pluginId: this.options.pluginId,
      id: declared.id,
      label: declared.label,
      ...(declared.icon !== undefined ? { icon: declared.icon } : {})
    });
    this.respondOk(requestId, { registered: true });
  }

  private handleFormatterContribution(requestId: number, payload: unknown): void {
    if (!isRecord(payload) || typeof payload.target !== 'string' || typeof payload.columnKey !== 'string') {
      this.respondError(requestId, 'ui.registerColumnFormatter requires target and columnKey');
      return;
    }
    const declared = this.options.manifest.contributes?.columnFormatters?.find(
      (formatter) => formatter.target === payload.target && formatter.columnKey === payload.columnKey
    );
    if (!declared) {
      this.respondError(requestId, 'column formatter is not declared in the manifest');
      return;
    }
    this.options.onContribution?.({
      kind: 'columnFormatter',
      pluginId: this.options.pluginId,
      target: declared.target,
      columnKey: declared.columnKey
    });
    this.respondOk(requestId, { registered: true });
  }

  private handlePresetContribution(requestId: number, payload: unknown): void {
    if (!isRecord(payload) || typeof payload.name !== 'string') {
      this.respondError(requestId, 'ui.registerFilterPreset requires a preset name');
      return;
    }
    const declared = this.options.manifest.contributes?.filterPresets?.find((preset) => preset.name === payload.name);
    if (!declared) {
      this.respondError(requestId, `filter preset "${payload.name}" is not declared in the manifest`);
      return;
    }
    this.options.onContribution?.({
      kind: 'filterPreset',
      pluginId: this.options.pluginId,
      name: declared.name,
      ruleTree: declared.ruleTree
    });
    this.respondOk(requestId, { registered: true });
  }

  private async handleNetworkFetch(requestId: number, payload: unknown): Promise<void> {
    if (!isRecord(payload) || typeof payload.url !== 'string') {
      this.respondError(requestId, 'network.fetch requires a url');
      return;
    }
    const decision = isNetworkAllowed(payload.url, this.options.networkGrant, this.options.destinations);
    if (!decision.allowed) {
      this.respondError(requestId, decision.reason);
      return;
    }
    const method = typeof payload.method === 'string' ? payload.method.toUpperCase() : 'GET';
    if (method !== 'GET' && method !== 'POST') {
      this.respondError(requestId, 'only GET and POST destination requests are permitted');
      return;
    }
    const fetchImpl: FetchLike = this.options.fetchImpl ?? ((input, init) => globalThis.fetch(input, init));
    try {
      const init: RequestInit = { method };
      if (method === 'POST' && typeof payload.body === 'string') init.body = payload.body;
      const response = await fetchImpl(payload.url, init);
      const body = await response.text();
      this.respondOk(requestId, { status: response.status, body });
    } catch (error) {
      this.respondError(requestId, error instanceof Error ? error.message : String(error));
    }
  }

  private respondOk(requestId: number, data: unknown): void {
    this.options.postToFrame({ protocol: SANDBOX_PROTOCOL, type: 'response', requestId, ok: true, data });
  }

  private respondError(requestId: number, error: string): void {
    // Capability rejections are normal RPC responses to the plugin, not shell
    // failures; only uncaught frame errors (type 'error') reach onError.
    this.options.postToFrame({ protocol: SANDBOX_PROTOCOL, type: 'response', requestId, ok: false, error });
  }
}