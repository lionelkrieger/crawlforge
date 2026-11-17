/**
 * Sandbox bridge host tests (FS-CRAWL-002 §6.4, E5, #39): read-only data
 * capabilities, permission gating, declared UI contributions only,
 * grant-restricted network, zero mutation surface, source-window identity,
 * and failure isolation (a throwing plugin/bridge never crashes the host).
 */

import { describe, expect, it } from 'vitest';
import { SandboxBridgeHost } from '../../../src/frontend/src/plugins/sandboxBridge';
import { SANDBOX_PROTOCOL, type HostToFrameMessage } from '../../../src/frontend/src/plugins/sandboxProtocol';
import { NO_NETWORK_GRANT, type DestinationRule, type NetworkGrant } from '../../../src/frontend/src/plugins/networkGrantGuard';
import { validatePluginManifest } from '../../../src/frontend/src/plugins/manifestValidator';
import { fakeBridge, manifestRaw } from './helpers';

const destinations: readonly DestinationRule[] = [
  { ref: 'warehouse', host: 'warehouse.internal.example', port: 8443, scheme: 'https' }
];

function makeHost(options: {
  permissions?: readonly ('read-data' | 'ui-contributions')[];
  networkGrant?: NetworkGrant;
  fetchImpl?: (url: string, init?: RequestInit) => Promise<Response>;
  raw?: Record<string, unknown>;
} = {}) {
  const validation = validatePluginManifest(options.raw ?? manifestRaw());
  if (!validation.ok) throw new Error('fixture manifest invalid');
  const bridge = fakeBridge(options.permissions ?? ['read-data', 'ui-contributions']);
  const posted: HostToFrameMessage[] = [];
  const contributions: unknown[] = [];
  const errors: string[] = [];
  const host = new SandboxBridgeHost({
    pluginId: 'com.example.widget',
    manifest: validation.manifest,
    bridge,
    networkGrant: options.networkGrant ?? NO_NETWORK_GRANT,
    destinations,
    postToFrame: (message) => posted.push(message),
    onContribution: (event) => contributions.push(event),
    onError: (message) => errors.push(message),
    ...(options.fetchImpl ? { fetchImpl: options.fetchImpl } : {})
  });
  const frameWindow = {} as Window;
  const send = (capability: string, payload?: unknown, requestId = 1) =>
    host.handleMessage({ protocol: SANDBOX_PROTOCOL, type: 'request', requestId, capability, payload }, frameWindow, frameWindow);
  return { host, bridge, posted, contributions, errors, frameWindow, send };
}

const responses = (posted: HostToFrameMessage[]) => posted.filter((message) => message.type === 'response');

describe('read-only data access', () => {
  it('serves getState and getSlice through the accepted bridge', () => {
    const { send, posted } = makeHost();
    send('data.getState');
    send('data.getSlice', { slice: 'settings' });
    const replies = responses(posted);
    expect(replies).toHaveLength(2);
    expect(replies[0].ok).toBe(true);
    expect(replies[1].ok).toBe(true);
    if (replies[1].ok) expect(replies[1].data).toEqual({ theme: 'dark' });
  });

  it('rejects unknown slices', () => {
    const { send, posted } = makeHost();
    send('data.getSlice', { slice: 'secrets' });
    const reply = responses(posted)[0];
    expect(reply.ok).toBe(false);
  });

  it('denies data capabilities without the read-data permission', () => {
    const { send, posted } = makeHost({ permissions: ['ui-contributions'] });
    send('data.getState');
    const reply = responses(posted)[0];
    expect(reply.ok).toBe(false);
    if (!reply.ok) expect(reply.error).toContain('read-data');
  });

  it('relays subscriptions as onDataUpdate hooks and detaches on dispose', () => {
    const { host, bridge, send, posted } = makeHost();
    send('data.subscribe');
    expect(bridge.listeners.size).toBe(1);
    bridge.emit({ kind: 'slice', slice: 'audits', revision: 2, droppedSinceLastRelay: 0 });
    const hooks = posted.filter((message) => message.type === 'hook');
    expect(hooks).toHaveLength(1);
    expect(hooks[0].hook).toBe('onDataUpdate');
    host.dispose();
    expect(bridge.listeners.size).toBe(0);
  });
});

describe('UI contributions must be declared', () => {
  it('accepts a declared tab and raises a contribution event', () => {
    const { send, posted, contributions } = makeHost();
    send('ui.registerTab', { id: 'widget-tab' });
    expect(responses(posted)[0].ok).toBe(true);
    expect(contributions).toEqual([
      { kind: 'tab', pluginId: 'com.example.widget', id: 'widget-tab', label: 'Widgets' }
    ]);
  });

  it('rejects undeclared tabs, formatters, and presets', () => {
    const { send, posted, contributions } = makeHost();
    send('ui.registerTab', { id: 'sneaky-tab' }, 1);
    send('ui.registerColumnFormatter', { target: 'urls', columnKey: 'custom.price.0' }, 2);
    send('ui.registerFilterPreset', { name: 'undeclared' }, 3);
    for (const reply of responses(posted)) expect(reply.ok).toBe(false);
    expect(contributions).toHaveLength(0);
  });

  it('accepts declared formatters and presets', () => {
    const raw = manifestRaw({
      contributes: {
        columnFormatters: [{ target: 'urls', columnKey: 'custom.price.0' }],
        filterPresets: [{ name: 'errors', ruleTree: { op: 'and', rules: [] } }]
      }
    });
    const { send, posted, contributions } = makeHost({ raw });
    send('ui.registerColumnFormatter', { target: 'urls', columnKey: 'custom.price.0' }, 1);
    send('ui.registerFilterPreset', { name: 'errors' }, 2);
    for (const reply of responses(posted)) expect(reply.ok).toBe(true);
    expect(contributions).toHaveLength(2);
  });

  it('denies UI capabilities without the ui-contributions permission', () => {
    const { send, posted } = makeHost({ permissions: ['read-data'] });
    send('ui.registerTab', { id: 'widget-tab' });
    expect(responses(posted)[0].ok).toBe(false);
  });
});

describe('zero mutation surface (E5)', () => {
  it('rejects unknown and mutation-shaped capabilities without dispatch', () => {
    const { send, posted, contributions } = makeHost();
    for (const capability of ['crawl.start', 'crawl.stop', 'settings.update', 'data.setState', 'api.request', 'backend.write', '']) {
      send(capability, {}, 1);
    }
    for (const reply of responses(posted)) {
      expect(reply.ok).toBe(false);
      if (!reply.ok) expect(reply.error).toContain('unknown capability');
    }
    expect(contributions).toHaveLength(0);
  });
});

describe('grant-restricted network (#39)', () => {
  const granted: NetworkGrant = { mode: 'destinations', destinationRefs: ['warehouse'], granted: true };

  it('denies every request without a grant', async () => {
    let fetched = 0;
    const { send, posted } = makeHost({ fetchImpl: async () => { fetched += 1; return new Response('ok'); } });
    send('network.fetch', { url: 'https://warehouse.internal.example:8443/push' });
    await Promise.resolve();
    const reply = responses(posted)[0];
    expect(reply.ok).toBe(false);
    expect(fetched).toBe(0);
  });

  it('allows granted destination requests only', async () => {
    const urls: string[] = [];
    const { send, posted } = makeHost({
      networkGrant: granted,
      fetchImpl: async (url) => { urls.push(url); return new Response('accepted', { status: 202 }); }
    });
    send('network.fetch', { url: 'https://warehouse.internal.example:8443/push', method: 'POST', body: '{}' }, 1);
    send('network.fetch', { url: 'https://evil.test/steal' }, 2);
    await new Promise((resolve) => setTimeout(resolve, 5));
    const replies = responses(posted);
    const grantedReply = replies.find((reply) => reply.requestId === 1);
    const deniedReply = replies.find((reply) => reply.requestId === 2);
    expect(grantedReply?.ok).toBe(true);
    if (grantedReply?.ok) expect(grantedReply.data).toEqual({ status: 202, body: 'accepted' });
    expect(deniedReply?.ok).toBe(false);
    expect(urls).toEqual(['https://warehouse.internal.example:8443/push']);
  });

  it('restricts methods to GET and POST', async () => {
    const { send, posted } = makeHost({ networkGrant: granted, fetchImpl: async () => new Response('ok') });
    send('network.fetch', { url: 'https://warehouse.internal.example:8443/', method: 'DELETE' });
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(responses(posted)[0].ok).toBe(false);
  });
});

describe('message hygiene and isolation', () => {
  it('ignores messages from other windows', () => {
    const { host, posted, frameWindow } = makeHost();
    host.handleMessage(
      { protocol: SANDBOX_PROTOCOL, type: 'request', requestId: 1, capability: 'data.getState' },
      {} as Window,
      frameWindow
    );
    expect(posted).toHaveLength(0);
  });

  it('ignores malformed and foreign-protocol messages', () => {
    const { host, posted, frameWindow } = makeHost();
    host.handleMessage(null, frameWindow, frameWindow);
    host.handleMessage({ type: 'request', requestId: 1, capability: 'data.getState' }, frameWindow, frameWindow);
    host.handleMessage({ protocol: 'other', type: 'request', requestId: 1, capability: 'data.getState' }, frameWindow, frameWindow);
    expect(posted).toHaveLength(0);
  });

  it('surfaces uncaught frame errors without crashing', () => {
    const { host, errors, frameWindow } = makeHost();
    host.handleMessage({ protocol: SANDBOX_PROTOCOL, type: 'error', message: 'plugin exploded' }, frameWindow, frameWindow);
    expect(errors).toEqual(['plugin exploded']);
  });

  it('isolates a throwing bridge as an error response', async () => {
    const { host, bridge, posted, frameWindow } = makeHost();
    bridge.getState = () => { throw new Error('store unavailable'); };
    host.handleMessage({ protocol: SANDBOX_PROTOCOL, type: 'request', requestId: 9, capability: 'data.getState' }, frameWindow, frameWindow);
    await new Promise((resolve) => setTimeout(resolve, 5));
    const reply = responses(posted)[0];
    expect(reply.ok).toBe(false);
    if (!reply.ok) expect(reply.error).toContain('store unavailable');
  });

  it('delivers lifecycle hooks into the frame', () => {
    const { host, posted } = makeHost();
    host.deliverHook('onCrawlComplete', { auditId: '7', reportReady: true });
    expect(posted).toEqual([
      { protocol: SANDBOX_PROTOCOL, type: 'hook', hook: 'onCrawlComplete', context: { auditId: '7', reportReady: true } }
    ]);
  });

  it('drops everything after dispose', () => {
    const { host, posted, frameWindow } = makeHost();
    host.dispose();
    host.handleMessage({ protocol: SANDBOX_PROTOCOL, type: 'request', requestId: 1, capability: 'data.getState' }, frameWindow, frameWindow);
    host.deliverHook('onLoad', {});
    expect(posted).toHaveLength(0);
  });
});