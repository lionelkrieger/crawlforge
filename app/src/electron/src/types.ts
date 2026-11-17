/**
 * CrawlForge desktop shell — shared types and injected ports (WP-06-01).
 *
 * Every shell module is pure TypeScript over these ports so the full startup,
 * supervision, window, recovery, and plugin-scan behavior is provable offline
 * in vitest without a display or the Electron binary (ratified test posture).
 * The thin Electron entry (main.ts) binds real implementations at runtime.
 */

/** Published loopback handshake (Security LoopbackPortHandshake, schema v1). */
export interface HandshakeDescriptor {
  readonly version: number;
  readonly port: number;
  readonly sessionToken: string;
  readonly createdAtUtc: string;
}

/** Supervisor-visible backend lifecycle states. */
export type BackendState =
  | 'stopped'
  | 'starting'
  | 'ready'
  | 'crashed'
  | 'unresponsive'
  | 'recovering';

/** One supervised backend launch attempt outcome. */
export interface BackendStatus {
  readonly state: BackendState;
  readonly baseUrl: string | null;
  readonly sessionToken: string | null;
  readonly consecutiveFailures: number;
  readonly lastError: string | null;
}

/** Window kinds of the §12 catalog. */
export type WindowKind =
  | 'splash'
  | 'main'
  | 'report'
  | 'visualization'
  | 'analyzer'
  | 'tools'
  | 'settings'
  | 'recovery';

/** Persisted per-window placement (§12 window-state persistence). */
export interface WindowState {
  readonly x?: number;
  readonly y?: number;
  readonly width: number;
  readonly height: number;
  readonly maximized?: boolean;
}

/** Minimal child-process surface the supervisor needs (injected). */
export interface ChildProcessLike {
  readonly pid: number | undefined;
  readonly exitCode: number | null;
  readonly killed: boolean;
  onExit(listener: (code: number | null, signal: string | null) => void): void;
  onStderr(listener: (chunk: string) => void): void;
  kill(): void;
}

/** Process spawning port (Electron main binds child_process.spawn). */
export interface ProcessSpawner {
  spawn(command: string, args: readonly string[], options: SpawnOptions): ChildProcessLike;
}

/** Spawn options understood by the port. */
export interface SpawnOptions {
  readonly cwd?: string;
  readonly env?: Readonly<Record<string, string>>;
  readonly detached?: boolean;
}

/** Filesystem port (Electron main binds node:fs). */
export interface FileSystemPort {
  readTextFile(path: string): Promise<string | null>;
  writeTextFile(path: string, content: string): Promise<void>;
  deleteFile(path: string): Promise<void>;
  listDirectory(path: string): Promise<readonly string[]>;
  exists(path: string): Promise<boolean>;
  ensureDirectory(path: string): Promise<void>;
}

/** Clock port (deterministic in tests). */
export interface ClockPort {
  now(): number;
  delay(milliseconds: number): Promise<void>;
}

/** HTTP probe port (Electron main binds fetch against loopback only). */
export interface HttpProbePort {
  /** GET returning status + body text; network errors resolve to null. */
  get(url: string, headers?: Readonly<Record<string, string>>): Promise<HttpResponseLike | null>;
  /** POST returning status + body text; network errors resolve to null. */
  post(
    url: string,
    body: string | null,
    headers?: Readonly<Record<string, string>>
  ): Promise<HttpResponseLike | null>;
  /** DELETE returning status + body text; network errors resolve to null. */
  delete(url: string, headers?: Readonly<Record<string, string>>): Promise<HttpResponseLike | null>;
}

/** Minimal HTTP response shape. */
export interface HttpResponseLike {
  readonly status: number;
  readonly body: string;
}

/** Local notification request (shell renders as OS notification; local only). */
export interface LocalNotification {
  readonly title: string;
  readonly message: string;
}

/** Taskbar progress modes (§12 shell duties). */
export type TaskbarProgressMode = 'none' | 'normal' | 'indeterminate' | 'error' | 'paused';

/**
 * Preload bridge contract consumed by the renderer shell (WP-06-02, accepted):
 * window.crawlForge { backendBaseUrl, getSessionToken }. backendBaseUrl is the
 * loopback origin discovered from the handshake; getSessionToken returns the
 * per-launch token for X-CrawlForge-Session (/api/health is exempt).
 */
export interface CrawlForgeBridge {
  readonly backendBaseUrl: string;
  getSessionToken(): string;
}

/** Typed logger port (Electron main binds console / local log file). */
export interface ShellLogger {
  info(message: string): void;
  warn(message: string): void;
  error(message: string, error?: unknown): void;
}

/** Session-token header name (Host composition convention, ratified). */
export const SESSION_TOKEN_HEADER = 'X-CrawlForge-Session';

/** Handshake file name inside the per-user data directory. */
export const HANDSHAKE_FILE_NAME = 'loopback-handshake.json';

/** Builds the loopback origin for a handshake port. */
export function loopbackOrigin(port: number): string {
  return `http://127.0.0.1:${port}`;
}
