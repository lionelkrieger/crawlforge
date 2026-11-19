// Root renderer component — mounts the CrawlForge shell (WP-06-02).
// Backend origin + session token come from the Electron preload bridge
// (WP-06-01); without a bridge the shell runs in offline mode (dev/tests).
// WP-09-11 (G20): the Electron window catalog opens renderer windows with
// ?route=<catalog-route>; the route wins over the persisted view.
import { useMemo } from 'react';
import { CrawlForgeShell } from './shell/CrawlForgeShell';
import { createShellServices } from './shell/ShellContext';
import { DEFAULT_WINDOW_STATE, type ShellView, type ShellWindowState } from './shell/windowState';

/** Preload bridge surface (shape proposed to WP-06-01 via orchestrator). */
interface CrawlForgePreloadBridge {
  /** Loopback backend origin discovered by the Electron main process. */
  backendBaseUrl?: string | null;
  /** Per-launch session token (X-CrawlForge-Session); never read from disk here. */
  getSessionToken?: () => string | null;
}

declare global {
  interface Window {
    crawlForge?: CrawlForgePreloadBridge;
  }
}

/** Window-catalog route → shell view (G20; unknown routes keep persistence). */
const ROUTE_VIEWS: Readonly<Record<string, ShellView>> = {
  splash: 'hub',
  hub: 'hub',
  report: 'report',
  visualization: 'tools',
  analyzer: 'tools',
  tools: 'tools',
  settings: 'settings',
  recovery: 'hub'
};

/** Parses the ?route= deep link (file:// URLs carry the query on location). */
export function parseWindowRoute(search: string): { view: ShellView | null; auditId: string | null } {
  try {
    const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
    const route = params.get('route');
    const view = route !== null && Object.prototype.hasOwnProperty.call(ROUTE_VIEWS, route) ? ROUTE_VIEWS[route] : null;
    const auditId = params.get('auditId');
    return { view, auditId: auditId !== null && auditId.length > 0 ? auditId : null };
  } catch {
    return { view: null, auditId: null };
  }
}

export function App() {
  const services = useMemo(() => {
    const bridge = typeof window !== 'undefined' ? window.crawlForge : undefined;
    const envBase = import.meta.env.VITE_API_BASE_URL as string | undefined;
    const baseUrl = bridge?.backendBaseUrl ?? envBase ?? null;
    return createShellServices({
      baseUrl,
      getSessionToken: bridge?.getSessionToken
    });
  }, []);

  const { initialWindowState, initialAuditId } = useMemo(() => {
    const search = typeof window !== 'undefined' ? window.location.search : '';
    const { view, auditId } = parseWindowRoute(search);
    const state: ShellWindowState | undefined = view
      ? { ...DEFAULT_WINDOW_STATE, view }
      : undefined;
    return { initialWindowState: state, initialAuditId: view === 'report' ? auditId : null };
  }, []);

  return (
    <CrawlForgeShell
      services={services}
      initialWindowState={initialWindowState}
      initialAuditId={initialAuditId}
    />
  );
}
