/**
 * Window catalog (WP-06-01, frontend-shell spec §12): the eight shell window
 * kinds with their lifecycle and focus rules, plus window-state persistence
 * shapes. Pure definitions + rules — the Electron window manager binds real
 * BrowserWindow instances against this catalog.
 *
 * Lifecycle rules encoded here:
 *  - splash is shown first and closed once the main window hydrates;
 *  - main is the single hub instance (focus instead of duplicate);
 *  - report/visualization/analyzer/tools are per-key instances (one per audit
 *    or tool key; re-opening focuses the existing instance);
 *  - settings and recovery are singletons;
 *  - recovery replaces the splash/main flow when the backend cannot start;
 *  - closing the last non-splash window does not quit the app on macOS
 *    convention is handled by the entry; the catalog only declares kinds.
 */
import { WindowKind, WindowState } from '../types';

/** Static definition of one catalog window kind. */
export interface WindowDefinition {
  readonly kind: WindowKind;
  /** Renderer route served from local UI assets only (CSP default-src 'self'). */
  readonly route: string;
  /** Singleton kinds focus the existing instance instead of opening a new one. */
  readonly singleton: boolean;
  /** Default placement when no persisted state exists. */
  readonly defaultState: WindowState;
  /** Whether the window persists placement across launches. */
  readonly persistState: boolean;
  /** Whether the window may be opened before backend readiness. */
  readonly allowedBeforeReady: boolean;
  /** Frame/chrome behavior. */
  readonly frame: boolean;
  /** Whether the window is resizable. */
  readonly resizable: boolean;
}

/** The §12 catalog, keyed by window kind. */
export const WINDOW_CATALOG: Readonly<Record<WindowKind, WindowDefinition>> = {
  splash: {
    kind: 'splash',
    route: 'splash',
    singleton: true,
    defaultState: { width: 480, height: 320 },
    persistState: false,
    allowedBeforeReady: true,
    frame: false,
    resizable: false
  },
  main: {
    kind: 'main',
    route: 'hub',
    singleton: true,
    defaultState: { width: 1280, height: 800 },
    persistState: true,
    allowedBeforeReady: false,
    frame: true,
    resizable: true
  },
  report: {
    kind: 'report',
    route: 'report',
    singleton: false,
    defaultState: { width: 1100, height: 760 },
    persistState: true,
    allowedBeforeReady: false,
    frame: true,
    resizable: true
  },
  visualization: {
    kind: 'visualization',
    route: 'visualization',
    singleton: false,
    defaultState: { width: 1200, height: 800 },
    persistState: true,
    allowedBeforeReady: false,
    frame: true,
    resizable: true
  },
  analyzer: {
    kind: 'analyzer',
    route: 'analyzer',
    singleton: true,
    defaultState: { width: 1000, height: 720 },
    persistState: true,
    allowedBeforeReady: false,
    frame: true,
    resizable: true
  },
  tools: {
    kind: 'tools',
    route: 'tools',
    singleton: false,
    defaultState: { width: 900, height: 680 },
    persistState: true,
    allowedBeforeReady: false,
    frame: true,
    resizable: true
  },
  settings: {
    kind: 'settings',
    route: 'settings',
    singleton: true,
    defaultState: { width: 960, height: 700 },
    persistState: true,
    allowedBeforeReady: true,
    frame: true,
    resizable: true
  },
  recovery: {
    kind: 'recovery',
    route: 'recovery',
    singleton: true,
    defaultState: { width: 760, height: 560 },
    persistState: false,
    allowedBeforeReady: true,
    frame: true,
    resizable: true
  }
};

/** Identity of a window instance: kind + optional instance key (audit id etc.). */
export interface WindowIdentity {
  readonly kind: WindowKind;
  readonly instanceKey?: string;
}

/** Stable registry key for a window identity. */
export function windowKey(identity: WindowIdentity): string {
  const definition = WINDOW_CATALOG[identity.kind];
  if (definition.singleton || identity.instanceKey === undefined) {
    return identity.kind;
  }
  return `${identity.kind}:${identity.instanceKey}`;
}

/** Whether a window kind may open before the backend is ready. */
export function mayOpenBeforeReady(kind: WindowKind): boolean {
  return WINDOW_CATALOG[kind].allowedBeforeReady;
}

/**
 * Focus rule (§12): re-opening an existing instance focuses it rather than
 * creating a duplicate. Returns the key to focus, or null when a new window
 * should be created.
 */
export function resolveOpen(
  identity: WindowIdentity,
  openKeys: ReadonlySet<string>
): { readonly focusKey: string } | { readonly createKey: string } {
  const key = windowKey(identity);
  if (openKeys.has(key)) {
    return { focusKey: key };
  }
  return { createKey: key };
}

/** Sanitizes persisted window state (never trust disk blindly). */
export function sanitizeWindowState(raw: unknown, fallback: WindowState): WindowState {
  if (typeof raw !== 'object' || raw === null) {
    return fallback;
  }

  const record = raw as Record<string, unknown>;
  const width = typeof record.width === 'number' && record.width >= 200 ? record.width : fallback.width;
  const height = typeof record.height === 'number' && record.height >= 150 ? record.height : fallback.height;
  const x = typeof record.x === 'number' && Number.isFinite(record.x) ? record.x : undefined;
  const y = typeof record.y === 'number' && Number.isFinite(record.y) ? record.y : undefined;
  const maximized = record.maximized === true;
  return { width, height, x, y, maximized };
}

/** File name of the persisted window-state store inside the data directory. */
export const WINDOW_STATE_FILE = 'window-state.json';
