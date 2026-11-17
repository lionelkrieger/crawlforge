// @vitest-environment jsdom
/**
 * Status-bar telemetry + compact action tests (criterion #41 UI facet):
 * heartbeat values render with binary units, advisory thresholds color the
 * cells, stale heartbeats dim, and the compact modal shows before/after
 * snapshots with reclaimed deltas and honest action labels.
 */

import { beforeEach, describe, expect, it } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { StatusBar, advisoryLevel } from '../../../src/frontend/src/shell/StatusBar';
import { ShellProvider } from '../../../src/frontend/src/shell/ShellContext';
import { crawlForgeTheme } from '../../../src/frontend/src/theme/theme';
import type { SystemHealthSnapshot } from '../../../src/frontend/src/contracts/api/system';
import { render, byTestId, flush, click, ok, err, fakeServices, offlineServices, installDomStubs } from './helpers';

installDomStubs();

// Compact modal portals outlive short flushes (200ms exit transition);
// clear leftover roots so queries never hit a previous test's modal.
beforeEach(() => {
  document.body.replaceChildren();
});

const heartbeat = (overrides: Partial<SystemHealthSnapshot> = {}): SystemHealthSnapshot => ({
  timestamp: new Date().toISOString(),
  backendWorkingSetBytes: 1_503_316_480, // ~1.40 GiB
  managedHeapBytes: 734_003_200, // ~700 MiB
  gcGen0Count: 120,
  gcGen1Count: 40,
  gcGen2Count: 10,
  sqliteWalBytes: 268_435_456, // 256 MiB
  auditId: null,
  ...overrides
});

function seedHealth(services: ReturnType<typeof offlineServices>, snapshot: SystemHealthSnapshot | null) {
  services.store.setState({
    systemHealth: {
      latest: snapshot,
      frontendJsHeapBytes: 52_428_800,
      lastHeartbeatAt: snapshot?.timestamp ?? null
    }
  });
}

async function renderStatusBar(services: ReturnType<typeof offlineServices>) {
  return render(
    <MantineProvider theme={crawlForgeTheme} defaultColorScheme="dark" forceColorScheme="dark">
      <ShellProvider services={services}>
        <StatusBar />
      </ShellProvider>
    </MantineProvider>
  );
}

describe('advisoryLevel', () => {
  it('escalates ok → warning → critical at the configured thresholds', () => {
    const advisory = 2 * 1024 ** 3;
    expect(advisoryLevel(advisory - 1, advisory)).toBe('ok');
    expect(advisoryLevel(advisory, advisory)).toBe('warning');
    expect(advisoryLevel(advisory * 1.5, advisory)).toBe('critical');
    expect(advisoryLevel(null, advisory)).toBe('ok');
  });
});

describe('status bar telemetry', () => {
  it('renders heartbeat values with binary units and GC counts', async () => {
    const services = offlineServices();
    seedHealth(services, heartbeat());
    const mounted = await renderStatusBar(services);
    const bar = byTestId(mounted.container, 'cf-statusbar')!;
    expect(bar.textContent).toContain('1.40 GiB'); // working set
    expect(bar.textContent).toContain('700 MiB'); // managed heap (0.7 GiB → 700 MiB)
    expect(bar.textContent).toContain('120/40/10'); // GC gens
    expect(bar.textContent).toContain('256 MiB'); // WAL
    expect(bar.textContent).toContain('50.0 MiB'); // frontend JS heap
    mounted.unmount();
  });

  it('shows external provider when WAL is null', async () => {
    const services = offlineServices();
    seedHealth(services, heartbeat({ sqliteWalBytes: null }));
    const mounted = await renderStatusBar(services);
    expect(byTestId(mounted.container, 'status-wal')!.textContent).toContain('external');
    mounted.unmount();
  });

  it('colors advisories and dims stale heartbeats', async () => {
    const services = offlineServices();
    seedHealth(services, heartbeat({
      backendWorkingSetBytes: 2_200_000_000, // > 2 GiB advisory
      timestamp: new Date(Date.now() - 30_000).toISOString() // stale
    }));
    const mounted = await renderStatusBar(services);
    const ws = byTestId(mounted.container, 'status-ws')!;
    expect(ws.getAttribute('data-advisory')).toBe('warning');
    expect(ws.className).toContain('cf-status-stale');
    mounted.unmount();
  });

  it('renders placeholders without a heartbeat', async () => {
    const services = offlineServices();
    const mounted = await renderStatusBar(services);
    expect(byTestId(mounted.container, 'status-ws')!.textContent).toContain('—');
    mounted.unmount();
  });
});

describe('compact action (criterion #41)', () => {
  const before = heartbeat({ backendWorkingSetBytes: 2_000_000_000, managedHeapBytes: 900_000_000, sqliteWalBytes: 300_000_000 });
  const after = heartbeat({ backendWorkingSetBytes: 1_200_000_000, managedHeapBytes: 400_000_000, sqliteWalBytes: 0 });

  it('shows before/after snapshots, reclaimed deltas, and action labels', async () => {
    const { services } = fakeServices({
      '/api/system/compact': () => ok({
        before,
        after,
        actionsPerformed: ['managed-gc-compaction', 'wal-checkpoint-truncate', 'working-set-trim']
      })
    });
    const mounted = await renderStatusBar(services);
    click(byTestId(mounted.container, 'compact-button')!);
    await flush(250);

    const table = document.querySelector('[data-testid="compact-table"]')!;
    expect(table.textContent).toContain('1.86 GiB'); // before WS
    expect(table.textContent).toContain('1.12 GiB'); // after WS
    expect(table.textContent).toContain('−763 MiB'); // reclaimed (800 MB)
    const actions = document.querySelector('[data-testid="compact-actions"]')!;
    expect(actions.textContent).toContain('Managed GC compaction');
    expect(actions.textContent).toContain('WAL checkpoint (truncate)');
    expect(actions.textContent).toContain('Working-set trim');
    mounted.unmount();
  });

  it('accepts the optional completedAt tolerance', async () => {
    const { services } = fakeServices({
      '/api/system/compact': () => ok({ before, after, actionsPerformed: ['managed-gc-compaction'], completedAt: '2026-11-14T12:00:00Z' })
    });
    const mounted = await renderStatusBar(services);
    click(byTestId(mounted.container, 'compact-button')!);
    await flush(250);
    expect(document.querySelector('[data-testid="compact-table"]')).not.toBeNull();
    mounted.unmount();
  });

  it('renders conflict refusals inline (rate limit / audit in flight)', async () => {
    const { services } = fakeServices({
      '/api/system/compact': () => err('conflict', 'Compact refused: an audit is in flight.')
    });
    const mounted = await renderStatusBar(services);
    click(byTestId(mounted.container, 'compact-button')!);
    await flush(250);
    const error = document.querySelector('[data-testid="compact-error"]')!;
    expect(error.textContent).toContain('audit is in flight');
    mounted.unmount();
  });

  it('degrades gracefully offline', async () => {
    const services = offlineServices();
    const mounted = await renderStatusBar(services);
    click(byTestId(mounted.container, 'compact-button')!);
    await flush(250);
    const error = document.querySelector('[data-testid="compact-error"]')!;
    expect(error.textContent).toContain('disconnected');
    mounted.unmount();
  });
});
