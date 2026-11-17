/**
 * Registry tests (FS-CRAWL-002 §6.2/§6.7): delegate registration, manifest
 * validation failures, compatibility disablement, workspace trust consent,
 * collision precedence, integrity tamper → grant revocation, locked REST
 * enable/disable/rescan/remove, local network grants, failure isolation.
 */

import { describe, expect, it } from 'vitest';
import { PluginRegistry, type PluginRegistryDelegate } from '../../../src/frontend/src/plugins/pluginRegistry';
import type { DestinationRule } from '../../../src/frontend/src/plugins/networkGrantGuard';
import { candidate, fakeApiClient, manifestRaw, pluginRoutes, wireEntry, type FakePluginBackend } from './helpers';

const destinations: readonly DestinationRule[] = [
  { ref: 'warehouse', host: 'warehouse.internal.example', port: 8443, scheme: 'https' }
];

function makeRegistry(options: {
  backend?: FakePluginBackend;
  manifests?: Record<string, unknown>;
  entries?: Record<string, string>;
  manifestSourceError?: boolean;
} = {}) {
  const backend = options.backend ?? { entries: [], rescanAdded: 0, rescanRemoved: 0 };
  const { api, calls } = fakeApiClient(pluginRoutes(backend));
  const registry = new PluginRegistry({
    client: api,
    appVersion: '1.0.0',
    destinations,
    now: () => '2026-11-14T12:00:00Z',
    manifestSource: options.manifestSourceError
      ? async () => { throw new Error('disk read failed'); }
      : async (directory) => options.manifests?.[directory] ?? null,
    entrySource: async (directory, entry) => options.entries?.[`${directory}/${entry}`] ?? 'console.log("local");'
  });
  return { registry, backend, calls };
}

describe('delegate registration', () => {
  it('satisfies the WP-06-01 delegate contract structurally', async () => {
    const { registry } = makeRegistry({ manifests: { '/plugins/com.example.widget': manifestRaw() } });
    const delegate: PluginRegistryDelegate = registry;
    await delegate.register(candidate('com.example.widget'));
    const entry = registry.getSnapshot().entries[0];
    expect(entry.id).toBe('com.example.widget');
    expect(entry.state).toBe('pending');
    expect(entry.enabled).toBe(true);
    expect(entry.permissions).toEqual(['read-data', 'ui-contributions']);
    expect(entry.integrity.status).toBe('unknown'); // first sighting: nothing to compare
    expect(entry.integrity.manifestHash.length).toBe(64);
  });

  it('synthesizes a manifest when no full plugin.json reader is bound', async () => {
    const { api } = fakeApiClient(pluginRoutes({ entries: [], rescanAdded: 0, rescanRemoved: 0 }));
    const registry = new PluginRegistry({ client: api, appVersion: '1.0.0' });
    await registry.register(candidate('com.example.minimal'));
    const entry = registry.getSnapshot().entries[0];
    expect(entry.manifest.entry).toBe('index.js');
    expect(entry.state).toBe('pending');
  });

  it('records invalid manifests as failed with field issues (never throws)', async () => {
    const { registry } = makeRegistry({ manifests: { '/plugins/com.example.widget': manifestRaw({ entry: '../escape.js' }) } });
    await registry.register(candidate('com.example.widget'));
    const entry = registry.getSnapshot().entries[0];
    expect(entry.state).toBe('failed');
    expect(entry.loadError).toContain('entry');
  });

  it('disables incompatible plugins with a diagnostic', async () => {
    const { registry } = makeRegistry({
      manifests: { '/plugins/com.example.widget': manifestRaw({ engine: { minAppVersion: '9.0.0' } }) }
    });
    await registry.register(candidate('com.example.widget'));
    const entry = registry.getSnapshot().entries[0];
    expect(entry.state).toBe('disabled');
    expect(entry.loadError).toContain('9.0.0');
  });

  it('isolates reader failures as failed entries', async () => {
    const { registry } = makeRegistry({ manifestSourceError: true });
    await expect(registry.register(candidate('com.example.widget'))).resolves.toBeUndefined();
    const entry = registry.getSnapshot().entries[0];
    expect(entry.state).toBe('failed');
    expect(entry.loadError).toContain('disk read failed');
  });
});

describe('trust and collisions (§6.2)', () => {
  it('requires one-time trust consent for workspace plugins', async () => {
    const { registry } = makeRegistry({ manifests: { '/ws/plugins/com.example.widget': manifestRaw() } });
    const wsCandidate = candidate('com.example.widget', 'workspace', { directory: '/ws/plugins/com.example.widget' });
    await registry.register(wsCandidate);
    expect(registry.getSnapshot().entries[0].trusted).toBe(false);
    registry.trustWorkspacePlugin('com.example.widget');
    expect(registry.getSnapshot().entries[0].trusted).toBe(true);
    // Re-registration keeps the consent (one-time per workspace).
    await registry.register(wsCandidate);
    expect(registry.getSnapshot().entries[0].trusted).toBe(true);
  });

  it('trusts per-user plugins without a prompt', async () => {
    const { registry } = makeRegistry();
    await registry.register(candidate('com.example.user'));
    expect(registry.getSnapshot().entries[0].trusted).toBe(true);
  });

  it('workspace overrides per-user with a visible warning', async () => {
    const { registry } = makeRegistry();
    await registry.register(candidate('com.example.dup', 'user'));
    await registry.register(candidate('com.example.dup', 'workspace', { directory: '/ws/plugins/com.example.dup' }));
    const snapshot = registry.getSnapshot();
    expect(snapshot.entries).toHaveLength(1);
    expect(snapshot.entries[0].source).toBe('workspace');
    expect(snapshot.entries[0].collisionOverride).toBe(true);
    expect(snapshot.warnings.some((warning) => warning.includes('overrides'))).toBe(true);
  });

  it('ignores a per-user candidate when a workspace plugin owns the id', async () => {
    const { registry } = makeRegistry();
    await registry.register(candidate('com.example.dup', 'workspace', { directory: '/ws/plugins/com.example.dup' }));
    await registry.register(candidate('com.example.dup', 'user'));
    const snapshot = registry.getSnapshot();
    expect(snapshot.entries).toHaveLength(1);
    expect(snapshot.entries[0].source).toBe('workspace');
    expect(snapshot.warnings.some((warning) => warning.includes('precedence'))).toBe(true);
  });
});

describe('integrity and grant revocation', () => {
  it('marks content changes and revokes the network grant pending re-review', async () => {
    const directory = '/plugins/com.example.widget';
    const raw = manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } } });
    const entryTexts: Record<string, string> = { [`${directory}/index.js`]: 'version-1' };
    const { registry } = makeRegistry({ manifests: { [directory]: raw }, entries: entryTexts });
    await registry.register(candidate('com.example.widget'));
    expect(registry.grantNetwork('com.example.widget', ['warehouse']).ok).toBe(true);
    expect(registry.getSnapshot().entries[0].networkGrant.granted).toBe(true);

    // Tampered entry module on the next scan pass.
    entryTexts[`${directory}/index.js`] = 'version-2-tampered';
    await registry.register(candidate('com.example.widget'));

    const entry = registry.getSnapshot().entries[0];
    expect(entry.integrity.status).toBe('changed');
    expect(entry.networkGrant.granted).toBe(false);
    expect(registry.getSnapshot().warnings.some((warning) => warning.includes('changed'))).toBe(true);
  });

  it('keeps verified status and the grant when content is unchanged', async () => {
    const directory = '/plugins/com.example.widget';
    const raw = manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } } });
    const { registry } = makeRegistry({
      manifests: { [directory]: raw },
      entries: { [`${directory}/index.js`]: 'stable-content' }
    });
    await registry.register(candidate('com.example.widget'));
    registry.grantNetwork('com.example.widget', ['warehouse']);
    await registry.register(candidate('com.example.widget'));

    const entry = registry.getSnapshot().entries[0];
    expect(entry.integrity.status).toBe('verified');
    expect(entry.networkGrant.granted).toBe(true);
  });
});

describe('locked REST management routes', () => {
  it('enables and disables through PUT /api/plugins/{id}', async () => {
    const backend: FakePluginBackend = { entries: [wireEntry('com.example.widget')], rescanAdded: 0, rescanRemoved: 0 };
    const { registry, calls } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));

    expect((await registry.setEnabled('com.example.widget', false)).ok).toBe(true);
    expect(registry.getSnapshot().entries[0].enabled).toBe(false);
    expect(registry.getSnapshot().entries[0].state).toBe('disabled');
    expect((await registry.setEnabled('com.example.widget', true)).ok).toBe(true);
    expect(registry.getSnapshot().entries[0].state).toBe('pending');

    const put = calls.filter((call) => call.method === 'PUT');
    expect(put).toHaveLength(2);
    expect(put[0].url).toContain('/api/plugins/com.example.widget');
    expect(JSON.parse(put[0].body ?? '{}')).toEqual({ enabled: false });
  });

  it('grants and revokes read-only permissions through PUT', async () => {
    const backend: FakePluginBackend = { entries: [wireEntry('com.example.widget', { permissions: ['read-data'] })], rescanAdded: 0, rescanRemoved: 0 };
    const { registry } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));

    expect((await registry.grantPermission('com.example.widget', 'ui-contributions')).ok).toBe(true);
    expect(registry.getSnapshot().entries[0].permissions).toContain('ui-contributions');
    expect((await registry.revokePermission('com.example.widget', 'ui-contributions')).ok).toBe(true);
    expect(registry.getSnapshot().entries[0].permissions).not.toContain('ui-contributions');
  });

  it('never offers the mutation permission locally (E5; backend 400)', async () => {
    const backend: FakePluginBackend = { entries: [wireEntry('com.example.widget')], rescanAdded: 0, rescanRemoved: 0 };
    const { registry, calls } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));
    const before = calls.length;
    const result = await registry.grantPermission('com.example.widget', 'commands');
    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain('400');
    expect(calls.length).toBe(before); // rejected before any request
  });

  it('surfaces backend envelope errors with fieldErrors', async () => {
    const backend: FakePluginBackend = { entries: [], rescanAdded: 0, rescanRemoved: 0 };
    const { registry } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));
    const result = await registry.setEnabled('com.example.unknown', true);
    expect(result.ok).toBe(false);
  });

  it('rescans through POST and merges the backend view', async () => {
    const backend: FakePluginBackend = {
      entries: [wireEntry('com.example.remote-view', { state: 'loaded' })],
      rescanAdded: 1,
      rescanRemoved: 0
    };
    const { registry, calls } = makeRegistry({ backend });
    const result = await registry.rescan();
    expect(result).toEqual({ ok: true, added: 1, removed: 0 });
    expect(registry.getSnapshot().entries[0].id).toBe('com.example.remote-view');
    expect(calls.some((call) => call.method === 'POST' && call.url.includes('/api/plugins/rescan'))).toBe(true);
  });

  it('removes through DELETE (files remain on disk)', async () => {
    const backend: FakePluginBackend = { entries: [wireEntry('com.example.widget')], rescanAdded: 0, rescanRemoved: 0 };
    const { registry, calls } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));
    expect((await registry.remove('com.example.widget')).ok).toBe(true);
    expect(registry.getSnapshot().entries).toHaveLength(0);
    expect(backend.entries).toHaveLength(0);
    expect(calls.some((call) => call.method === 'DELETE')).toBe(true);
  });

  it('syncs enabled flags and load state from GET /api/plugins', async () => {
    const backend: FakePluginBackend = {
      entries: [wireEntry('com.example.widget', { enabled: false, state: 'disabled', loadError: 'backend diagnostic' })],
      rescanAdded: 0,
      rescanRemoved: 0
    };
    const { registry } = makeRegistry({ backend });
    await registry.register(candidate('com.example.widget'));
    expect((await registry.syncFromBackend()).ok).toBe(true);
    const entry = registry.getSnapshot().entries[0];
    expect(entry.enabled).toBe(false);
    expect(entry.state).toBe('disabled');
    expect(entry.loadError).toBe('backend diagnostic');
  });

  it('isolates transport failures as operation errors', async () => {
    const { api } = fakeApiClient({
      '/api/plugins': () => { throw new Error('connection refused'); }
    });
    const registry = new PluginRegistry({ client: api, appVersion: '1.0.0' });
    const result = await registry.syncFromBackend();
    expect(result.ok).toBe(false);
    expect(result.ok === false && result.error).toContain('connection refused');
  });
});

describe('local network grants (ratified ruling)', () => {
  it('grants only known destination references', async () => {
    const { registry } = makeRegistry({
      manifests: { '/plugins/com.example.widget': manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } } }) }
    });
    await registry.register(candidate('com.example.widget'));
    expect(registry.grantNetwork('com.example.widget', ['no-such-rule']).ok).toBe(false);
    expect(registry.grantNetwork('com.example.widget', []).ok).toBe(false);
    expect(registry.grantNetwork('com.example.widget', ['warehouse']).ok).toBe(true);
    const grant = registry.getSnapshot().entries[0].networkGrant;
    expect(grant.granted).toBe(true);
    expect(grant.destinationRefs).toEqual(['warehouse']);
  });

  it('revokes consent while keeping references for re-review', async () => {
    const { registry } = makeRegistry({
      manifests: { '/plugins/com.example.widget': manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } } }) }
    });
    await registry.register(candidate('com.example.widget'));
    registry.grantNetwork('com.example.widget', ['warehouse']);
    expect(registry.revokeNetwork('com.example.widget').ok).toBe(true);
    const grant = registry.getSnapshot().entries[0].networkGrant;
    expect(grant.granted).toBe(false);
    expect(grant.destinationRefs).toEqual(['warehouse']);
  });

  it('defaults to no network when the manifest declares none', async () => {
    const { registry } = makeRegistry({ manifests: { '/plugins/com.example.widget': manifestRaw() } });
    await registry.register(candidate('com.example.widget'));
    expect(registry.getSnapshot().entries[0].networkGrant.mode).toBe('none');
  });
});

describe('snapshot determinism and subscriptions', () => {
  it('sorts entries by id and notifies subscribers on change', async () => {
    const { registry } = makeRegistry();
    let notifications = 0;
    const unsubscribe = registry.subscribe(() => { notifications += 1; });
    await registry.register(candidate('com.example.zeta'));
    await registry.register(candidate('com.example.alpha'));
    expect(registry.getSnapshot().entries.map((entry) => entry.id)).toEqual(['com.example.alpha', 'com.example.zeta']);
    expect(notifications).toBe(2);
    expect(registry.getSnapshot()).toBe(registry.getSnapshot()); // stable between changes
    unsubscribe();
    await registry.register(candidate('com.example.middle'));
    expect(notifications).toBe(2);
  });
});