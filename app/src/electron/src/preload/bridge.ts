/**
 * Preload bridge contract (WP-06-01, §8.1): contextIsolation ON,
 * nodeIntegration OFF; the renderer receives ONLY this minimal typed surface.
 *
 * Core contract (matches the accepted WP-06-02 shell, zero delta):
 *   window.crawlForge { backendBaseUrl, getSessionToken }
 *     - backendBaseUrl: loopback origin discovered from the handshake;
 *     - getSessionToken(): per-launch token for X-CrawlForge-Session
 *       (/api/health is exempt).
 *
 * Shell duties (native dialogs, notifications, taskbar progress, window
 * controls) arrive additively under window.crawlForgeShell so the accepted
 * core contract stays untouched. No Node or Electron API is ever exposed
 * directly; every member is a frozen, typed function over IPC.
 */
import { CrawlForgeBridge, HandshakeDescriptor, loopbackOrigin, TaskbarProgressMode } from '../types';

/** Builds the core bridge from a validated handshake descriptor. */
export function createBridge(descriptor: HandshakeDescriptor): CrawlForgeBridge {
  if (descriptor.version !== 1) {
    throw new Error('Unsupported handshake version.');
  }
  if (!Number.isInteger(descriptor.port) || descriptor.port < 1 || descriptor.port > 65535) {
    throw new Error('Handshake port is invalid.');
  }
  if (descriptor.sessionToken.length === 0) {
    throw new Error('Handshake session token is empty.');
  }

  const baseUrl = loopbackOrigin(descriptor.port);
  const token = descriptor.sessionToken;
  return Object.freeze({
    backendBaseUrl: baseUrl,
    getSessionToken: (): string => token
  });
}

/** Native file-dialog request (shell duty; results are local paths only). */
export interface FileDialogRequest {
  readonly mode: 'open-file' | 'open-directory' | 'save-file';
  readonly title?: string;
  readonly defaultPath?: string;
  readonly filters?: readonly { readonly name: string; readonly extensions: readonly string[] }[];
}

/** Additive shell-duty surface (window.crawlForgeShell). */
export interface CrawlForgeShellBridge {
  showFileDialog(request: FileDialogRequest): Promise<readonly string[]>;
  showNotification(title: string, message: string): void;
  setTaskbarProgress(mode: TaskbarProgressMode, value?: number): void;
  minimizeWindow(): void;
  maximizeWindow(): void;
  closeWindow(): void;
  restartBackend(): Promise<void>;
  purgeDiagnostics(): Promise<number>;
}

/** IPC channel names shared by preload and main (typed, frozen). */
export const IPC_CHANNELS = Object.freeze({
  fileDialog: 'crawlforge:file-dialog',
  notification: 'crawlforge:notification',
  taskbarProgress: 'crawlforge:taskbar-progress',
  windowMinimize: 'crawlforge:window-minimize',
  windowMaximize: 'crawlforge:window-maximize',
  windowClose: 'crawlforge:window-close',
  backendRestart: 'crawlforge:backend-restart',
  diagnosticsPurge: 'crawlforge:diagnostics-purge',
  backendStatus: 'crawlforge:backend-status'
} as const);

/**
 * Validates a descriptor arriving over IPC before building the bridge
 * (defense in depth: the main process is the only source, but the shape is
 * still checked at the boundary).
 */
export function parseDescriptor(raw: unknown): HandshakeDescriptor | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }

  const record = raw as Record<string, unknown>;
  if (
    record.version === 1 &&
    typeof record.port === 'number' &&
    Number.isInteger(record.port) &&
    record.port >= 1 &&
    record.port <= 65535 &&
    typeof record.sessionToken === 'string' &&
    record.sessionToken.length > 0 &&
    typeof record.createdAtUtc === 'string'
  ) {
    return {
      version: 1,
      port: record.port,
      sessionToken: record.sessionToken,
      createdAtUtc: record.createdAtUtc
    };
  }

  return null;
}
