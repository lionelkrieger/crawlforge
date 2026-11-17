/**
 * Window manager (WP-06-01): creates/focuses/closes catalog windows through an
 * injected window factory, enforces the §12 lifecycle/focus rules, and
 * persists per-kind placement to the local data directory. No Electron import
 * — the entry binds BrowserWindow; tests bind fakes.
 */
import {
  resolveOpen,
  sanitizeWindowState,
  WINDOW_CATALOG,
  WINDOW_STATE_FILE,
  WindowIdentity,
  mayOpenBeforeReady
} from './window-catalog';
import { FileSystemPort, ShellLogger, WindowKind, WindowState } from '../types';
import { joinPath } from '../handshake/port-discovery';

/** Minimal window instance surface the manager needs (injected). */
export interface WindowInstanceLike {
  readonly id: string;
  focus(): void;
  close(): void;
  isVisible(): boolean;
  getState(): WindowState;
  onDestroyed(listener: () => void): void;
  loadRoute(route: string, query?: Readonly<Record<string, string>>): Promise<void>;
}

/** Factory creating real windows (Electron main binds BrowserWindow). */
export interface WindowFactory {
  create(kind: WindowKind, state: WindowState): WindowInstanceLike;
}

/** Result of an open request. */
export interface OpenResult {
  readonly key: string;
  readonly focusedExisting: boolean;
}

/** Manages the live window set against the catalog rules. */
export class WindowManager {
  private readonly factory: WindowFactory;
  private readonly fs: FileSystemPort;
  private readonly logger: ShellLogger;
  private readonly dataDirectory: string;
  private readonly windows = new Map<string, WindowInstanceLike>();
  private persisted: Record<string, unknown> = {};
  private loaded = false;

  constructor(
    factory: WindowFactory,
    fs: FileSystemPort,
    logger: ShellLogger,
    dataDirectory: string
  ) {
    this.factory = factory;
    this.fs = fs;
    this.logger = logger;
    this.dataDirectory = dataDirectory;
  }

  /** Loads persisted placement (best effort; malformed state falls back). */
  async loadPersistedState(): Promise<void> {
    if (this.loaded) {
      return;
    }

    this.loaded = true;
    try {
      const text = await this.fs.readTextFile(joinPath(this.dataDirectory, WINDOW_STATE_FILE));
      if (text !== null) {
        const parsed: unknown = JSON.parse(text);
        if (typeof parsed === 'object' && parsed !== null) {
          this.persisted = parsed as Record<string, unknown>;
        }
      }
    } catch (error) {
      this.logger.warn('Window state unreadable; using defaults.');
    }
  }

  /** Opens (or focuses) a catalog window. Enforces readiness gating. */
  async open(identity: WindowIdentity, backendReady: boolean): Promise<OpenResult> {
    if (!backendReady && !mayOpenBeforeReady(identity.kind)) {
      throw new Error(`Window '${identity.kind}' requires a ready backend.`);
    }

    await this.loadPersistedState();
    const decision = resolveOpen(identity, new Set(this.windows.keys()));
    if ('focusKey' in decision) {
      const existing = this.windows.get(decision.focusKey);
      existing?.focus();
      return { key: decision.focusKey, focusedExisting: true };
    }

    const definition = WINDOW_CATALOG[identity.kind];
    const state = definition.persistState
      ? sanitizeWindowState(this.persisted[decision.createKey], definition.defaultState)
      : definition.defaultState;
    const instance = this.factory.create(identity.kind, state);
    this.windows.set(decision.createKey, instance);
    instance.onDestroyed(() => {
      this.windows.delete(decision.createKey);
    });

    const query = identity.instanceKey !== undefined ? { key: identity.instanceKey } : undefined;
    await instance.loadRoute(definition.route, query);
    return { key: decision.createKey, focusedExisting: false };
  }

  /** Closes one window by key (no-op when absent). */
  close(key: string): void {
    const instance = this.windows.get(key);
    if (instance) {
      instance.close();
      this.windows.delete(key);
    }
  }

  /** Closes every window except the given kind (splash → main transition). */
  closeAllExcept(kind: WindowKind): void {
    for (const [key, instance] of [...this.windows.entries()]) {
      const instanceKind = key.split(':')[0] as WindowKind;
      if (instanceKind !== kind) {
        instance.close();
        this.windows.delete(key);
      }
    }
  }

  /** Live window keys (diagnostics/tests). */
  get openKeys(): readonly string[] {
    return [...this.windows.keys()];
  }

  /** Whether any window of a kind is open. */
  hasKind(kind: WindowKind): boolean {
    return [...this.windows.keys()].some((key) => key.split(':')[0] === kind);
  }

  /** Persists placement of all stateful windows (call on close/quit). */
  async persistState(): Promise<void> {
    for (const [key, instance] of this.windows.entries()) {
      const kind = key.split(':')[0] as WindowKind;
      if (WINDOW_CATALOG[kind].persistState) {
        this.persisted[key] = instance.getState();
      }
    }

    try {
      await this.fs.ensureDirectory(this.dataDirectory);
      await this.fs.writeTextFile(
        joinPath(this.dataDirectory, WINDOW_STATE_FILE),
        JSON.stringify(this.persisted, null, 2)
      );
    } catch (error) {
      this.logger.error('Failed to persist window state.', error);
    }
  }
}
