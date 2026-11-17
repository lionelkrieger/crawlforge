// Root renderer component — mounts the CrawlForge shell (WP-06-02).
// Backend origin + session token come from the Electron preload bridge
// (WP-06-01); without a bridge the shell runs in offline mode (dev/tests).
import { useMemo } from 'react';
import { CrawlForgeShell } from './shell/CrawlForgeShell';
import { createShellServices } from './shell/ShellContext';

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

  return <CrawlForgeShell services={services} />;
}
