import { describe, expect, it } from 'vitest';
import {
  WINDOW_CATALOG,
  mayOpenBeforeReady,
  resolveOpen,
  sanitizeWindowState,
  windowKey
} from '../src/windows/window-catalog';
import { WindowKind } from '../src/types';

const ALL_KINDS: WindowKind[] = [
  'splash',
  'main',
  'report',
  'visualization',
  'analyzer',
  'tools',
  'settings',
  'recovery'
];

describe('window catalog (§12)', () => {
  it('defines all eight window kinds', () => {
    expect(Object.keys(WINDOW_CATALOG).sort()).toEqual([...ALL_KINDS].sort());
  });

  it('gates backend-dependent windows before readiness', () => {
    expect(mayOpenBeforeReady('splash')).toBe(true);
    expect(mayOpenBeforeReady('recovery')).toBe(true);
    expect(mayOpenBeforeReady('settings')).toBe(true);
    expect(mayOpenBeforeReady('main')).toBe(false);
    expect(mayOpenBeforeReady('report')).toBe(false);
    expect(mayOpenBeforeReady('visualization')).toBe(false);
    expect(mayOpenBeforeReady('analyzer')).toBe(false);
    expect(mayOpenBeforeReady('tools')).toBe(false);
  });

  it('uses singleton keys for hub/settings/recovery and per-key instances for reports', () => {
    expect(windowKey({ kind: 'main' })).toBe('main');
    expect(windowKey({ kind: 'report', instanceKey: '42' })).toBe('report:42');
    expect(windowKey({ kind: 'report', instanceKey: '43' })).toBe('report:43');
    expect(windowKey({ kind: 'settings', instanceKey: 'ignored' })).toBe('settings');
  });

  it('focuses existing instances instead of duplicating them', () => {
    const open = new Set(['main', 'report:42']);
    expect(resolveOpen({ kind: 'main' }, open)).toEqual({ focusKey: 'main' });
    expect(resolveOpen({ kind: 'report', instanceKey: '42' }, open)).toEqual({ focusKey: 'report:42' });
    expect(resolveOpen({ kind: 'report', instanceKey: '99' }, open)).toEqual({ createKey: 'report:99' });
  });

  it('sanitizes persisted state and falls back on garbage', () => {
    const fallback = { width: 1000, height: 700 };
    expect(sanitizeWindowState(null, fallback)).toEqual(fallback);
    expect(sanitizeWindowState({ width: 50, height: 50 }, fallback)).toEqual({
      width: 1000,
      height: 700,
      x: undefined,
      y: undefined,
      maximized: false
    });
    expect(sanitizeWindowState({ width: 1200, height: 800, x: 10, y: 20, maximized: true }, fallback)).toEqual({
      width: 1200,
      height: 800,
      x: 10,
      y: 20,
      maximized: true
    });
    expect(sanitizeWindowState({ width: 1200, height: 800, x: 'bad' }, fallback)).toEqual({
      width: 1200,
      height: 800,
      x: undefined,
      y: undefined,
      maximized: false
    });
  });

  it('keeps splash and recovery frameless-appropriate and non-persistent', () => {
    expect(WINDOW_CATALOG.splash.frame).toBe(false);
    expect(WINDOW_CATALOG.splash.persistState).toBe(false);
    expect(WINDOW_CATALOG.recovery.persistState).toBe(false);
    expect(WINDOW_CATALOG.main.persistState).toBe(true);
  });
});

describe('WindowManager', () => {
  it('opens, focuses, closes, and persists through the injected ports', async () => {
    const { WindowManager } = await import('../src/windows/window-manager');
    const { FakeFileSystem, FakeLogger } = await import('./fakes');
    const fs = new FakeFileSystem();
    const logger = new FakeLogger();
    const created: { kind: WindowKind; state: unknown }[] = [];

    class FakeWindow {
      destroyed: (() => void)[] = [];
      closed = false;
      focused = 0;
      route = '';
      query: Record<string, string> | undefined;
      constructor(readonly kind: WindowKind, readonly state: unknown) {}
      focus() {
        this.focused++;
      }
      close() {
        this.closed = true;
        for (const listener of this.destroyed) listener();
      }
      isVisible() {
        return !this.closed;
      }
      getState() {
        return { width: 1234, height: 789 };
      }
      onDestroyed(listener: () => void) {
        this.destroyed.push(listener);
      }
      async loadRoute(route: string, query?: Record<string, string>) {
        this.route = route;
        this.query = query;
      }
    }

    const manager = new WindowManager(
      {
        create(kind, state) {
          created.push({ kind, state });
          const window = new FakeWindow(kind, state);
          return window as never;
        }
      },
      fs,
      logger,
      'C:/data'
    );

    const first = await manager.open({ kind: 'main' }, true);
    expect(first.focusedExisting).toBe(false);
    const again = await manager.open({ kind: 'main' }, true);
    expect(again.focusedExisting).toBe(true);
    expect(created).toHaveLength(1);

    const report = await manager.open({ kind: 'report', instanceKey: '7' }, true);
    expect(report.key).toBe('report:7');
    expect(manager.openKeys).toContain('report:7');

    await manager.persistState();
    const persisted = JSON.parse(fs.files.get('C:/data/window-state.json') ?? '{}');
    expect(persisted.main).toEqual({ width: 1234, height: 789 });
    expect(persisted['report:7']).toEqual({ width: 1234, height: 789 });

    manager.close('report:7');
    expect(manager.openKeys).not.toContain('report:7');

    manager.closeAllExcept('main');
    expect(manager.hasKind('main')).toBe(true);
  });

  it('refuses backend-dependent windows before readiness', async () => {
    const { WindowManager } = await import('../src/windows/window-manager');
    const { FakeFileSystem, FakeLogger } = await import('./fakes');
    const stub = {
      id: 'stub',
      focus() {},
      close() {},
      isVisible: () => true,
      getState: () => ({ width: 800, height: 600 }),
      onDestroyed() {},
      loadRoute: async () => {}
    };
    const manager = new WindowManager(
      { create: () => stub as never },
      new FakeFileSystem(),
      new FakeLogger(),
      'C:/data'
    );
    await expect(manager.open({ kind: 'main' }, false)).rejects.toThrow(/ready backend/);
    await expect(manager.open({ kind: 'splash' }, false)).resolves.toBeDefined();
  });

  it('restores persisted placement on open', async () => {
    const { WindowManager } = await import('../src/windows/window-manager');
    const { FakeFileSystem, FakeLogger } = await import('./fakes');
    const fs = new FakeFileSystem();
    fs.seed('C:/data/window-state.json', JSON.stringify({ main: { width: 1400, height: 900, x: 5, y: 6 } }));
    let seen: unknown = null;
    const manager = new WindowManager(
      {
        create(_kind, state) {
          seen = state;
          return {
            focus() {},
            close() {},
            isVisible: () => true,
            getState: () => state,
            onDestroyed() {},
            loadRoute: async () => {}
          } as never;
        }
      },
      fs,
      new FakeLogger(),
      'C:/data'
    );
    await manager.open({ kind: 'main' }, true);
    expect(seen).toEqual({ width: 1400, height: 900, x: 5, y: 6, maximized: false });
  });
});
