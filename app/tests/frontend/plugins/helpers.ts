/**
 * Shared fixtures for the plugin suites: manifest/candidate factories, fake
 * REST routes over the shell's fake-fetch pattern, and a fake read-only
 * bridge. Re-exports the shell render harness (read-only consumption).
 */

import type { PluginBridge, PluginDataUpdate, PluginReadableState, PluginRelaySliceName } from '../../../src/frontend/src/contracts/plugins/bridge';
import type { PluginPermission, PluginRegistryEntry } from '../../../src/frontend/src/contracts/api/plugins';
import type { PluginCandidate, PluginSource } from '../../../src/frontend/src/plugins/pluginRegistry';
import { ok, err, type FetchCall } from '../shell/helpers';

export { ok, err, fakeApiClient, fakeFetch, render, flush, byTestId, click, fire, installDomStubs } from '../shell/helpers';
export type { FetchCall } from '../shell/helpers';

/** A valid raw §6.3 manifest payload (override fields per test). */
export function manifestRaw(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'com.example.widget',
    name: 'Widget Report',
    version: '1.2.0',
    description: 'Adds a widget report tab.',
    engine: { minAppVersion: '0.1.0' },
    entry: 'index.js',
    hooks: ['onLoad', 'onCrawlComplete'],
    contributes: { tabs: [{ id: 'widget-tab', label: 'Widgets' }] },
    ...overrides
  };
}

/** A scanner candidate (structurally the WP-06-01 shape). */
export function candidate(
  id: string,
  source: PluginSource = 'user',
  overrides: Partial<PluginCandidate> = {}
): PluginCandidate {
  return {
    manifest: { id, name: `Plugin ${id}`, version: '1.0.0', entry: 'index.js', apiVersion: 1 },
    directory: `/plugins/${id}`,
    manifestPath: `/plugins/${id}/plugin.json`,
    source,
    ...overrides
  };
}

/** A wire registry entry (backend view). */
export function wireEntry(id: string, overrides: Partial<PluginRegistryEntry> = {}): PluginRegistryEntry {
  return {
    id,
    name: `Plugin ${id}`,
    version: '1.0.0',
    description: null,
    directory: `/plugins/${id}`,
    state: 'pending',
    enabled: true,
    permissions: ['read-data'],
    loadError: null,
    discoveredAt: '2026-11-14T12:00:00Z',
    ...overrides
  };
}

/** Mutable backend state for the fake REST routes. */
export interface FakePluginBackend {
  entries: PluginRegistryEntry[];
  rescanAdded: number;
  rescanRemoved: number;
}

/**
 * Fake locked REST routes: GET list, PUT update (commands → 400 envelope),
 * POST rescan, DELETE remove. Matches the WP-05-01 wire behavior.
 */
export function pluginRoutes(backend: FakePluginBackend): Record<string, (call: FetchCall) => unknown> {
  return {
    'plugins/rescan': () => ok({ added: backend.rescanAdded, removed: backend.rescanRemoved, plugins: backend.entries }),
    '/api/plugins/': (call) => {
      const id = decodeURIComponent(call.url.split('/api/plugins/')[1].split('?')[0]);
      const entry = backend.entries.find((existing) => existing.id === id);
      if (!entry) return err('not-found', `unknown plugin: ${id}`);
      if (call.method === 'DELETE') {
        backend.entries = backend.entries.filter((existing) => existing.id !== id);
        return ok({ id, deleted: true });
      }
      const body = call.body ? (JSON.parse(call.body) as Record<string, unknown>) : {};
      const grants = (body.grantPermissions ?? []) as PluginPermission[];
      if (grants.includes('commands')) {
        return new Response(
          JSON.stringify(err('validation', 'unsupported permission', { grantPermissions: ['commands is not grantable'] })),
          { status: 400, headers: { 'Content-Type': 'application/json' } }
        );
      }
      let permissions = [...entry.permissions];
      for (const permission of grants) {
        if (!permissions.includes(permission)) permissions.push(permission);
      }
      for (const permission of (body.revokePermissions ?? []) as PluginPermission[]) {
        permissions = permissions.filter((existing) => existing !== permission);
      }
      const updated: PluginRegistryEntry = {
        ...entry,
        enabled: typeof body.enabled === 'boolean' ? body.enabled : entry.enabled,
        permissions
      };
      backend.entries = backend.entries.map((existing) => (existing.id === id ? updated : existing));
      return ok(updated);
    },
    '/api/plugins': (call) => {
      if (call.method === 'GET') return ok(backend.entries);
      return err('validation', `unsupported method ${call.method}`);
    }
  };
}

/** Fake read-only bridge with subscription tracking (store-bus stand-in). */
export interface FakeBridge extends PluginBridge {
  readonly listeners: Set<(update: PluginDataUpdate) => void>;
  emit(update: PluginDataUpdate): void;
}

/** Builds a fake bridge exposing a minimal readable state. */
export function fakeBridge(permissions: readonly PluginPermission[] = ['read-data', 'ui-contributions']): FakeBridge {
  const listeners = new Set<(update: PluginDataUpdate) => void>();
  const state = {
    projects: [],
    audits: [],
    settings: { theme: 'dark' },
    plugins: []
  } as unknown as PluginReadableState;
  return {
    pluginId: 'com.example.widget',
    permissions,
    listeners,
    getState: () => state,
    getSlice: <K extends PluginRelaySliceName>(slice: K) => state[slice],
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    emit: (update) => {
      for (const listener of [...listeners]) listener(update);
    }
  };
}