/**
 * CrawlForge preload (WP-06-01, §8.1): contextIsolation ON, nodeIntegration
 * OFF, sandbox ON. Exposes ONLY the typed bridge — no Node/Electron API
 * leaks. Self-contained on purpose: sandboxed preloads cannot require local
 * modules, so the bridge construction is inlined (the pure, testable version
 * lives in src/preload/bridge.ts and is kept in sync by contract tests).
 *
 * Core contract (accepted WP-06-02 shell): window.crawlForge
 * { backendBaseUrl, getSessionToken }. The shell renders offline-first when
 * the bridge is absent, so exposure happens once the descriptor arrives.
 */
import { contextBridge, ipcRenderer } from 'electron';

interface HandshakeDescriptor {
  version: number;
  port: number;
  sessionToken: string;
  createdAtUtc: string;
}

function isValidDescriptor(raw: unknown): raw is HandshakeDescriptor {
  if (typeof raw !== 'object' || raw === null) {
    return false;
  }
  const record = raw as Record<string, unknown>;
  return (
    record.version === 1 &&
    typeof record.port === 'number' &&
    Number.isInteger(record.port) &&
    record.port >= 1 &&
    record.port <= 65535 &&
    typeof record.sessionToken === 'string' &&
    record.sessionToken.length > 0 &&
    typeof record.createdAtUtc === 'string'
  );
}

// Additive shell-duty surface (does not alter the accepted core contract).
contextBridge.exposeInMainWorld('crawlForgeShell', {
  showFileDialog: (request: unknown) => ipcRenderer.invoke('crawlforge:file-dialog', request),
  showNotification: (title: string, message: string) =>
    ipcRenderer.send('crawlforge:notification', title, message),
  setTaskbarProgress: (mode: string, value?: number) =>
    ipcRenderer.send('crawlforge:taskbar-progress', mode, value),
  minimizeWindow: () => ipcRenderer.send('crawlforge:window-minimize'),
  maximizeWindow: () => ipcRenderer.send('crawlforge:window-maximize'),
  closeWindow: () => ipcRenderer.send('crawlforge:window-close'),
  restartBackend: () => ipcRenderer.invoke('crawlforge:backend-restart'),
  purgeDiagnostics: () => ipcRenderer.invoke('crawlforge:diagnostics-purge'),
  onBackendStatus: (listener: (status: unknown) => void) => {
    const wrapped = (_event: unknown, status: unknown): void => listener(status);
    ipcRenderer.on('crawlforge:backend-status', wrapped);
    return () => ipcRenderer.removeListener('crawlforge:backend-status', wrapped);
  },
  signalHydrated: () => ipcRenderer.send('crawlforge:hydrated')
});

// Core bridge: exposed once the main process supplies a valid descriptor.
void ipcRenderer.invoke('crawlforge:get-bridge').then((raw: unknown) => {
  if (!isValidDescriptor(raw)) {
    return; // Shell stays offline-first without the bridge.
  }
  const baseUrl = `http://127.0.0.1:${raw.port}`;
  const token = raw.sessionToken;
  contextBridge.exposeInMainWorld(
    'crawlForge',
    Object.freeze({
      backendBaseUrl: baseUrl,
      getSessionToken: (): string => token
    })
  );
});
