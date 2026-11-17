// @vitest-environment jsdom
/**
 * Plugin manager lifecycle tests (FS-CRAWL-002 §6.2/§6.5/§6.6): loads
 * enabled+trusted plugins into sandboxed frames, forwards the four bus hooks
 * into the frame, isolates failures (missing/throwing entry never crashes the
 * shell), and detaches everything on unload/disable.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { PluginManager } from '../../../src/frontend/src/plugins/pluginManager';
import { PluginRegistry } from '../../../src/frontend/src/plugins/pluginRegistry';
import { SANDBOX_PROTOCOL, type HostToFrameMessage } from '../../../src/frontend/src/plugins/sandboxProtocol';
import { createCrawlForgeStore } from '../../../src/frontend/src/store/crawlForgeStore';
import { createPluginHookBus, type CrawlForgePluginBus } from '../../../src/frontend/src/store/pluginBus';
import { candidate, fakeApiClient, manifestRaw, pluginRoutes, wireEntry } from './helpers';

let manager: PluginManager | null = null;
let bus: CrawlForgePluginBus | null = null;

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  manager?.dispose();
  manager = null;
  bus?.dispose();
  bus = null;
  document.body.replaceChildren();
});

function makeGraph(options: {
  entryCode?: string | null;
  entryThrows?: boolean;
  manifests?: Record<string, unknown>;
} = {}) {
  const { api } = fakeApiClient(pluginRoutes({
    entries: ['com.example.widget', 'com.example.ws', 'com.example.bad', 'com.example.good'].map((id) => wireEntry(id)),
    rescanAdded: 0,
    rescanRemoved: 0
  }));
  const registry = new PluginRegistry({
    client: api,
    appVersion: '1.0.0',
    now: () => '2026-11-14T12:00:00Z',
    manifestSource: async (directory) => options.manifests?.[directory] ?? null
  });
  const store = createCrawlForgeStore();
  bus = createPluginHookBus(store);
  const container = document.createElement('div');
  document.body.appendChild(container);
  manager = new PluginManager({
    registry,
    bus,
    doc: document,
    container,
    entrySource: async () => {
      if (options.entryThrows) throw new Error('entry read exploded');
      return options.entryCode === undefined ? 'window.cf.onHook(function () {});' : options.entryCode;
    }
  });
  return { registry, container };
}

/** Records messages posted into one plugin frame. */
function recordFrameMessages(frame: HTMLIFrameElement): HostToFrameMessage[] {
  const received: HostToFrameMessage[] = [];
  frame.contentWindow?.addEventListener('message', (event: MessageEvent) => {
    if (event.data && event.data.protocol === SANDBOX_PROTOCOL) received.push(event.data as HostToFrameMessage);
  });
  return received;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 10));

describe('loading', () => {
  it('loads enabled trusted plugins into sandboxed frames', async () => {
    const { registry, container } = makeGraph({ manifests: { '/plugins/com.example.widget': manifestRaw() } });
    await registry.register(candidate('com.example.widget'));
    await manager!.loadEnabled();
    expect(manager!.getLoadedIds()).toEqual(['com.example.widget']);
    const frame = container.querySelector('[data-testid="plugin-frame-com.example.widget"]');
    expect(frame).not.toBeNull();
    expect(frame!.getAttribute('sandbox')).toBe('allow-scripts');
    expect(registry.getSnapshot().entries[0].state).toBe('loaded');
  });

  it('skips untrusted workspace plugins until consent', async () => {
    const { registry } = makeGraph();
    await registry.register(candidate('com.example.ws', 'workspace', { directory: '/ws/plugins/com.example.ws' }));
    await manager!.loadEnabled();
    expect(manager!.getLoadedIds()).toEqual([]);
    registry.trustWorkspacePlugin('com.example.ws');
    await manager!.loadPlugin('com.example.ws');
    expect(manager!.getLoadedIds()).toEqual(['com.example.ws']);
  });

  it('skips disabled plugins', async () => {
    const { registry } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await registry.setEnabled('com.example.widget', false);
    await manager!.loadEnabled();
    expect(manager!.getLoadedIds()).toEqual([]);
  });

  it('is idempotent per plugin', async () => {
    const { registry, container } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await manager!.loadPlugin('com.example.widget');
    await manager!.loadPlugin('com.example.widget');
    expect(container.querySelectorAll('iframe')).toHaveLength(1);
  });
});

describe('lifecycle hooks via the accepted bus', () => {
  it('forwards onLoad, onTabActivate, and exactly-once onCrawlComplete', async () => {
    const { registry } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await manager!.loadPlugin('com.example.widget');
    const frame = manager!.getFrame('com.example.widget')!;
    const received = recordFrameMessages(frame);

    bus!.notifyTabActivate({ pluginId: 'com.example.widget', tabId: 'widget-tab', surface: 'report', activeAuditId: '7' });
    bus!.notifyCrawlComplete('7', { queued: 0, fetched: 10, error: 0, blocked: 0 }, true);
    bus!.notifyCrawlComplete('7', { queued: 0, fetched: 10, error: 0, blocked: 0 }, true);
    await flush();

    const hooks = received.filter((message) => message.type === 'hook');
    const names = hooks.map((message) => message.hook);
    expect(names).toContain('onLoad');
    expect(names).toContain('onTabActivate');
    expect(names.filter((name) => name === 'onCrawlComplete')).toHaveLength(1);
  });
});

describe('failure isolation', () => {
  it('marks a missing entry module failed without a frame or throw', async () => {
    const { registry, container } = makeGraph({ entryCode: null });
    await registry.register(candidate('com.example.widget'));
    await expect(manager!.loadPlugin('com.example.widget')).resolves.toBeUndefined();
    expect(manager!.getLoadedIds()).toEqual([]);
    expect(container.querySelectorAll('iframe')).toHaveLength(0);
    const entry = registry.getSnapshot().entries[0];
    expect(entry.state).toBe('failed');
    expect(entry.loadError).toContain('entry module not found');
  });

  it('isolates a throwing entry reader', async () => {
    const { registry } = makeGraph({ entryThrows: true });
    await registry.register(candidate('com.example.widget'));
    await expect(manager!.loadPlugin('com.example.widget')).resolves.toBeUndefined();
    expect(registry.getSnapshot().entries[0].state).toBe('failed');
    expect(registry.getSnapshot().entries[0].loadError).toContain('entry read exploded');
  });

  it('a failing plugin does not block other plugins', async () => {
    const { api } = fakeApiClient(pluginRoutes({ entries: [], rescanAdded: 0, rescanRemoved: 0 }));
    const registry = new PluginRegistry({ client: api, appVersion: '1.0.0' });
    const store = createCrawlForgeStore();
    bus = createPluginHookBus(store);
    manager = new PluginManager({
      registry,
      bus,
      doc: document,
      entrySource: async (directory) => (directory.includes('bad') ? null : 'code')
    });
    await registry.register(candidate('com.example.bad'));
    await registry.register(candidate('com.example.good'));
    await manager.loadEnabled();
    expect(manager.getLoadedIds()).toEqual(['com.example.good']);
    expect(registry.getSnapshot().entries.find((entry) => entry.id === 'com.example.bad')?.state).toBe('failed');
  });
});

describe('unload and disable', () => {
  it('detaches frame and bus registration on unload', async () => {
    const { registry, container } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await manager!.loadPlugin('com.example.widget');
    manager!.unload('com.example.widget');
    expect(manager!.getLoadedIds()).toEqual([]);
    expect(container.querySelectorAll('iframe')).toHaveLength(0);
  });

  it('auto-unloads when the registry disables the plugin', async () => {
    const { registry } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await manager!.loadPlugin('com.example.widget');
    expect(manager!.getLoadedIds()).toEqual(['com.example.widget']);
    await registry.setEnabled('com.example.widget', false);
    expect(manager!.getLoadedIds()).toEqual([]);
  });

  it('dispose unloads everything', async () => {
    const { registry, container } = makeGraph();
    await registry.register(candidate('com.example.widget'));
    await manager!.loadPlugin('com.example.widget');
    manager!.dispose();
    expect(container.querySelectorAll('iframe')).toHaveLength(0);
    manager = null;
  });
});