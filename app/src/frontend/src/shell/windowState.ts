/**
 * Window-state persistence (FS-CRAWL-001 §12: persist window state).
 * Pane layout + active view survive reloads via localStorage; bounded,
 * validated on read, never throws in private-mode/storage-denied contexts.
 *
 * @module shell/windowState
 */

/** The shell's navigable views (window catalog subset owned by the shell). */
export type ShellView = 'hub' | 'audits' | 'report' | 'tools' | 'settings' | 'project' | 'configurations';

/** Persisted split-pane layout. */
export interface PaneLayoutState {
  /** Primary pane fraction (0.15..0.85). */
  readonly split: number;
  /** Secondary pane collapsed flag. */
  readonly collapsed: boolean;
  /** Split orientation. */
  readonly direction: 'horizontal' | 'vertical';
}

/** Complete persisted window state. */
export interface ShellWindowState {
  readonly view: ShellView;
  readonly layout: PaneLayoutState;
  /** Selected project for the project/configuration views (WP-09-11). */
  readonly selectedProjectId: string | null;
}

const STORAGE_KEY = 'crawlforge.shell.windowState.v1';

/** Default layout: 62/38 horizontal split, hub view. */
export const DEFAULT_WINDOW_STATE: ShellWindowState = {
  view: 'hub',
  layout: { split: 0.62, collapsed: false, direction: 'horizontal' },
  selectedProjectId: null
};

const VIEWS: readonly ShellView[] = ['hub', 'audits', 'report', 'tools', 'settings', 'project', 'configurations'];

/** Validates and clamps an unknown persisted value. */
export function sanitizeWindowState(value: unknown): ShellWindowState {
  if (typeof value !== 'object' || value === null) return DEFAULT_WINDOW_STATE;
  const candidate = value as Partial<ShellWindowState>;
  const view = VIEWS.includes(candidate.view as ShellView) ? (candidate.view as ShellView) : 'hub';
  const layout = candidate.layout;
  const split =
    typeof layout?.split === 'number' && Number.isFinite(layout.split)
      ? Math.max(0.15, Math.min(0.85, layout.split))
      : DEFAULT_WINDOW_STATE.layout.split;
  const selectedProjectId =
    typeof candidate.selectedProjectId === 'string' && candidate.selectedProjectId.length > 0
      ? candidate.selectedProjectId
      : null;
  return {
    view,
    layout: {
      split,
      collapsed: layout?.collapsed === true,
      direction: layout?.direction === 'vertical' ? 'vertical' : 'horizontal'
    },
    selectedProjectId
  };
}

/** Reads persisted window state (defaults when absent/corrupt/denied). */
export function loadWindowState(storage: Storage | null = safeStorage()): ShellWindowState {
  if (!storage) return DEFAULT_WINDOW_STATE;
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_WINDOW_STATE;
    return sanitizeWindowState(JSON.parse(raw));
  } catch {
    return DEFAULT_WINDOW_STATE;
  }
}

/** Persists window state (best-effort; never throws). */
export function saveWindowState(state: ShellWindowState, storage: Storage | null = safeStorage()): void {
  if (!storage) return;
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(sanitizeWindowState(state)));
  } catch {
    // Storage denied/full — window state is advisory, not critical.
  }
}

/** Storage accessor tolerant of denied contexts (SSR/tests/private mode). */
export function safeStorage(): Storage | null {
  try {
    return typeof localStorage !== 'undefined' ? localStorage : null;
  } catch {
    return null;
  }
}
