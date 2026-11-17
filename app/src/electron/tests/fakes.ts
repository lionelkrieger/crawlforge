/**
 * Shared deterministic fakes for the shell suites (WP-06-01). No network, no
 * timers, no filesystem — every port is an in-memory fake with a manual clock.
 */
import {
  ChildProcessLike,
  ClockPort,
  FileSystemPort,
  HttpProbePort,
  HttpResponseLike,
  ProcessSpawner,
  ShellLogger,
  SpawnOptions
} from '../src/types';

/** Manual clock: delays advance virtual time and yield to the event loop. */
export class FakeClock implements ClockPort {
  current = 1_700_000_000_000;
  readonly delays: number[] = [];

  now(): number {
    return this.current;
  }

  async delay(milliseconds: number): Promise<void> {
    this.delays.push(milliseconds);
    this.current += milliseconds;
    // Yield to the event loop (check phase) so detached loops (health probes)
    // interleave with test code and cleanup; a pure microtask delay would
    // starve the loop and OOM the worker.
    await new Promise((resolve) => setImmediate(resolve));
  }
}

/** In-memory filesystem keyed by normalized path. */
export class FakeFileSystem implements FileSystemPort {
  readonly files = new Map<string, string>();
  readonly directories = new Set<string>();
  failReads = false;

  async readTextFile(path: string): Promise<string | null> {
    if (this.failReads) {
      throw new Error('read denied');
    }
    return this.files.get(path) ?? null;
  }

  async writeTextFile(path: string, content: string): Promise<void> {
    this.files.set(path, content);
  }

  async deleteFile(path: string): Promise<void> {
    this.files.delete(path);
  }

  async listDirectory(path: string): Promise<readonly string[]> {
    if (!this.directories.has(path)) {
      throw new Error('directory missing');
    }
    const prefix = `${path}/`;
    const names = new Set<string>();
    for (const key of this.files.keys()) {
      if (key.startsWith(prefix)) {
        names.add(key.slice(prefix.length).split('/')[0]);
      }
    }
    for (const directory of this.directories) {
      if (directory.startsWith(prefix)) {
        names.add(directory.slice(prefix.length).split('/')[0]);
      }
    }
    return [...names];
  }

  async exists(path: string): Promise<boolean> {
    return this.files.has(path) || this.directories.has(path);
  }

  async ensureDirectory(path: string): Promise<void> {
    this.directories.add(path);
  }

  /** Seeds a file and registers every ancestor directory. */
  seed(path: string, content: string): void {
    this.files.set(path, content);
    const parts = path.split('/');
    for (let index = 1; index < parts.length; index++) {
      this.directories.add(parts.slice(0, index).join('/'));
    }
  }
}

/** One fake child process with manual exit control. */
export class FakeChild implements ChildProcessLike {
  pid: number | undefined = 4242;
  exitCode: number | null = null;
  killed = false;
  private readonly exitListeners: ((code: number | null, signal: string | null) => void)[] = [];
  private readonly stderrListeners: ((chunk: string) => void)[] = [];

  onExit(listener: (code: number | null, signal: string | null) => void): void {
    this.exitListeners.push(listener);
  }

  onStderr(listener: (chunk: string) => void): void {
    this.stderrListeners.push(listener);
  }

  kill(): void {
    this.killed = true;
    this.exit(0, 'SIGTERM');
  }

  exit(code: number | null, signal: string | null = null): void {
    if (this.exitCode !== null) {
      return;
    }
    this.exitCode = code;
    for (const listener of this.exitListeners) {
      listener(code, signal);
    }
  }

  emitStderr(chunk: string): void {
    for (const listener of this.stderrListeners) {
      listener(chunk);
    }
  }
}

/** Spawner producing scripted children. */
export class FakeSpawner implements ProcessSpawner {
  readonly children: FakeChild[] = [];
  readonly calls: { command: string; args: readonly string[]; options: SpawnOptions }[] = [];
  throwOnSpawn = false;
  /** When set, every spawned child exits immediately with this code. */
  exitOnSpawn: number | null = null;

  spawn(command: string, args: readonly string[], options: SpawnOptions): ChildProcessLike {
    this.calls.push({ command, args, options });
    if (this.throwOnSpawn) {
      throw new Error('spawn refused');
    }
    const child = new FakeChild();
    this.children.push(child);
    if (this.exitOnSpawn !== null) {
      child.exit(this.exitOnSpawn);
    }
    return child;
  }
}

/** Scripted HTTP probe: routes matched by exact URL or predicate. */
export class FakeHttp implements HttpProbePort {
  readonly requests: { method: string; url: string; body: string | null; headers?: Readonly<Record<string, string>> }[] = [];
  responder: (method: string, url: string) => HttpResponseLike | null = () => null;

  async get(url: string, headers?: Readonly<Record<string, string>>): Promise<HttpResponseLike | null> {
    this.requests.push({ method: 'GET', url, body: null, headers });
    return this.responder('GET', url);
  }

  async post(
    url: string,
    body: string | null,
    headers?: Readonly<Record<string, string>>
  ): Promise<HttpResponseLike | null> {
    this.requests.push({ method: 'POST', url, body, headers });
    return this.responder('POST', url);
  }

  async delete(url: string, headers?: Readonly<Record<string, string>>): Promise<HttpResponseLike | null> {
    this.requests.push({ method: 'DELETE', url, body: null, headers });
    return this.responder('DELETE', url);
  }
}

/** Collecting logger. */
export class FakeLogger implements ShellLogger {
  readonly infos: string[] = [];
  readonly warnings: string[] = [];
  readonly errors: string[] = [];

  info(message: string): void {
    this.infos.push(message);
  }

  warn(message: string): void {
    this.warnings.push(message);
  }

  error(message: string): void {
    this.errors.push(message);
  }
}

/** Builds a valid handshake descriptor JSON for a port/token. */
export function handshakeJson(port: number, token = 'test-token', createdAtUtc?: string): string {
  return JSON.stringify({
    version: 1,
    port,
    sessionToken: token,
    createdAtUtc: createdAtUtc ?? new Date(1_700_000_000_000).toISOString()
  });
}
