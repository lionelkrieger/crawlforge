// @vitest-environment jsdom
/**
 * Plugin management surface tests (FS-CRAWL-002 §6.7): registry table with
 * load state + integrity status, enable/disable/rescan/remove via the locked
 * routes, workspace trust consent, collision warnings, grant review panel
 * (commands locked, network destination consent), and error surfacing.
 * Dark-first under the shell theme.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { PluginManagementSurface } from '../../../src/frontend/src/plugins/PluginManagementSurface';
import { PluginRegistry } from '../../../src/frontend/src/plugins/pluginRegistry';
import { crawlForgeTheme } from '../../../src/frontend/src/theme/theme';
import type { DestinationRule } from '../../../src/frontend/src/plugins/networkGrantGuard';
import {
  byTestId,
  candidate,
  click,
  fakeApiClient,
  flush,
  installDomStubs,
  manifestRaw,
  pluginRoutes,
  render,
  wireEntry,
  type FakePluginBackend
} from './helpers';

installDomStubs();

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

const destinations: readonly DestinationRule[] = [
  { ref: 'warehouse', host: 'warehouse.internal.example', port: 8443, scheme: 'https' }
];

async function makeSurface(options: {
  backend?: FakePluginBackend;
  manifests?: Record<string, unknown>;
  candidates?: ReturnType<typeof candidate>[];
} = {}) {
  const backend = options.backend ?? {
    entries: [wireEntry('com.example.widget')],
    rescanAdded: 0,
    rescanRemoved: 0
  };
  const { api } = fakeApiClient(pluginRoutes(backend));
  const registry = new PluginRegistry({
    client: api,
    appVersion: '1.0.0',
    destinations,
    now: () => '2026-11-14T12:00:00Z',
    manifestSource: async (directory) => options.manifests?.[directory] ?? null
  });
  for (const item of options.candidates ?? [candidate('com.example.widget')]) {
    await registry.register(item);
  }
  const mounted = await render(
    <MantineProvider theme={crawlForgeTheme} forceColorScheme="dark">
      <PluginManagementSurface registry={registry} />
    </MantineProvider>
  );
  return { registry, backend, mounted };
}

function toggleInput(container: HTMLElement, testId: string): HTMLInputElement {
  const element = container.querySelector(`[data-testid="${testId}"]`);
  if (!element) throw new Error(`no element for ${testId}`);
  if (element instanceof HTMLInputElement) return element;
  const input = element.querySelector('input');
  if (!input) throw new Error(`no input under ${testId}`);
  return input as HTMLInputElement;
}

describe('registry table', () => {
  it('renders rows with state and integrity badges', async () => {
    const { mounted } = await makeSurface({ manifests: { '/plugins/com.example.widget': manifestRaw() } });
    expect(byTestId(mounted.container, 'plugin-management-surface')).not.toBeNull();
    expect(byTestId(mounted.container, 'plugin-row-com.example.widget')).not.toBeNull();
    expect(byTestId(mounted.container, 'plugin-state-com.example.widget')?.textContent).toBe('pending');
    expect(byTestId(mounted.container, 'plugin-integrity-com.example.widget')?.textContent).toBe('unknown');
    mounted.unmount();
  });

  it('surfaces load errors on failed plugins', async () => {
    const { mounted } = await makeSurface({
      manifests: { '/plugins/com.example.widget': manifestRaw({ entry: '../escape.js' }) }
    });
    expect(byTestId(mounted.container, 'plugin-state-com.example.widget')?.textContent).toBe('failed');
    expect(byTestId(mounted.container, 'plugin-error-com.example.widget')?.textContent).toContain('entry');
    mounted.unmount();
  });

  it('shows collision warnings', async () => {
    const { mounted } = await makeSurface({
      backend: { entries: [wireEntry('com.example.dup')], rescanAdded: 0, rescanRemoved: 0 },
      candidates: [
        candidate('com.example.dup', 'user'),
        candidate('com.example.dup', 'workspace', { directory: '/ws/plugins/com.example.dup' })
      ]
    });
    expect(byTestId(mounted.container, 'plugin-warning-0')?.textContent).toContain('overrides');
    expect(byTestId(mounted.container, 'plugin-collision-com.example.dup')).not.toBeNull();
    mounted.unmount();
  });
});

describe('enable / disable / rescan / remove', () => {
  it('toggles enabled through the locked PUT route', async () => {
    const { mounted, backend } = await makeSurface();
    click(toggleInput(mounted.container, 'plugin-toggle-com.example.widget'));
    await flush();
    expect(byTestId(mounted.container, 'plugin-state-com.example.widget')?.textContent).toBe('disabled');
    expect(backend.entries[0].enabled).toBe(false);
    click(toggleInput(mounted.container, 'plugin-toggle-com.example.widget'));
    await flush();
    expect(byTestId(mounted.container, 'plugin-state-com.example.widget')?.textContent).toBe('pending');
    mounted.unmount();
  });

  it('rescans and reports the summary', async () => {
    const { mounted } = await makeSurface({
      backend: { entries: [wireEntry('com.example.widget')], rescanAdded: 2, rescanRemoved: 1 }
    });
    click(byTestId(mounted.container, 'plugin-rescan')!);
    await flush();
    expect(byTestId(mounted.container, 'rescan-summary')?.textContent).toContain('2 added, 1 removed');
    mounted.unmount();
  });

  it('removes through the locked DELETE route', async () => {
    const { mounted, backend } = await makeSurface();
    click(byTestId(mounted.container, 'plugin-remove-com.example.widget')!);
    await flush();
    expect(byTestId(mounted.container, 'plugin-row-com.example.widget')).toBeNull();
    expect(backend.entries).toHaveLength(0);
    mounted.unmount();
  });

  it('surfaces backend errors in the management UI', async () => {
    const { mounted } = await makeSurface({
      backend: { entries: [], rescanAdded: 0, rescanRemoved: 0 }
    });
    click(toggleInput(mounted.container, 'plugin-toggle-com.example.widget'));
    await flush();
    expect(byTestId(mounted.container, 'operation-error')?.textContent).toContain('not-found');
    mounted.unmount();
  });
});

describe('workspace trust consent', () => {
  it('offers one-time trust and clears the prompt after consent', async () => {
    const { mounted, registry } = await makeSurface({
      backend: { entries: [wireEntry('com.example.ws')], rescanAdded: 0, rescanRemoved: 0 },
      candidates: [candidate('com.example.ws', 'workspace', { directory: '/ws/plugins/com.example.ws' })]
    });
    const trustButton = byTestId(mounted.container, 'plugin-trust-com.example.ws');
    expect(trustButton).not.toBeNull();
    click(trustButton!);
    await flush();
    expect(registry.getSnapshot().entries[0].trusted).toBe(true);
    expect(byTestId(mounted.container, 'plugin-trust-com.example.ws')).toBeNull();
    mounted.unmount();
  });
});

describe('grant review panel', () => {
  it('grants and revokes read-only permissions; commands stays locked', async () => {
    const { mounted } = await makeSurface({
      manifests: { '/plugins/com.example.widget': manifestRaw({ contributes: undefined }) }
    });
    click(byTestId(mounted.container, 'plugin-review-com.example.widget')!);
    await flush();
    expect(byTestId(mounted.container, 'grant-panel')).not.toBeNull();
    expect(byTestId(mounted.container, 'grant-commands-locked')?.textContent).toContain('locked');

    // read-data granted, ui-contributions not yet.
    expect(byTestId(mounted.container, 'grant-toggle-read-data')?.textContent).toBe('Revoke');
    expect(byTestId(mounted.container, 'grant-toggle-ui-contributions')?.textContent).toBe('Grant');
    click(byTestId(mounted.container, 'grant-toggle-ui-contributions')!);
    await flush();
    expect(byTestId(mounted.container, 'grant-toggle-ui-contributions')?.textContent).toBe('Revoke');
    click(byTestId(mounted.container, 'grant-toggle-ui-contributions')!);
    await flush();
    expect(byTestId(mounted.container, 'grant-toggle-ui-contributions')?.textContent).toBe('Grant');
    mounted.unmount();
  });

  it('reviews network destinations and grants/revokes local consent', async () => {
    const { mounted, registry } = await makeSurface({
      manifests: {
        '/plugins/com.example.widget': manifestRaw({
          permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } }
        })
      }
    });
    click(byTestId(mounted.container, 'plugin-review-com.example.widget')!);
    await flush();
    expect(byTestId(mounted.container, 'network-grant-state')?.textContent).toBe('not granted');

    // Declared references prefill the selection; grant consent directly.
    expect(toggleInput(mounted.container, 'destination-checkbox-warehouse').checked).toBe(true);
    click(byTestId(mounted.container, 'network-grant-button')!);
    await flush();
    expect(byTestId(mounted.container, 'network-grant-state')?.textContent).toBe('granted');
    expect(registry.getSnapshot().entries[0].networkGrant.granted).toBe(true);

    click(byTestId(mounted.container, 'network-revoke-button')!);
    await flush();
    expect(byTestId(mounted.container, 'network-grant-state')?.textContent).toBe('not granted');
    expect(registry.getSnapshot().entries[0].networkGrant.granted).toBe(false);
    mounted.unmount();
  });

  it('shows mode none when no network is declared', async () => {
    const { mounted } = await makeSurface({ manifests: { '/plugins/com.example.widget': manifestRaw() } });
    click(byTestId(mounted.container, 'plugin-review-com.example.widget')!);
    await flush();
    expect(byTestId(mounted.container, 'network-none')?.textContent).toContain('none');
    mounted.unmount();
  });
});