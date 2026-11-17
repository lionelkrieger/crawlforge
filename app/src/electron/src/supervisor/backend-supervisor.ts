/**
 * Backend supervisor (WP-06-01, process-model spec §2.1–§2.5): spawns the
 * local ASP.NET Core backend, discovers its loopback port through the Security
 * handshake, health-probes readiness, restarts on crash with bounded backoff,
 * and drains gracefully on shell exit. After repeated failures the supervisor
 * stops retrying and surfaces the recovery state (§12 recovery screen).
 *
 * All I/O goes through injected ports; the module never imports electron or
 * node built-ins, so the full behavior is vitest-provable offline. Network
 * probes target 127.0.0.1 exclusively (clean-room loopback boundary).
 */
import { PortDiscovery } from '../handshake/port-discovery';
import {
  BackendState,
  BackendStatus,
  ChildProcessLike,
  ClockPort,
  FileSystemPort,
  HttpProbePort,
  ProcessSpawner,
  SESSION_TOKEN_HEADER,
  ShellLogger,
  SpawnOptions
} from '../types';

/** Supervisor tuning (all local configuration). */
export interface SupervisorOptions {
  readonly backendCommand: string;
  readonly backendArgs: readonly string[];
  readonly dataDirectory: string;
  readonly spawnOptions?: SpawnOptions;
  readonly healthIntervalMs?: number;
  readonly startupTimeoutMs?: number;
  readonly maxConsecutiveFailures?: number;
  readonly backoffBaseMs?: number;
  readonly backoffCapMs?: number;
  readonly gracefulShutdownTimeoutMs?: number;
  readonly healthFailureThreshold?: number;
}

/** Listener for supervisor state transitions. */
export type StatusListener = (status: BackendStatus) => void;

const DEFAULTS = {
  healthIntervalMs: 2000,
  startupTimeoutMs: 30000,
  maxConsecutiveFailures: 3,
  backoffBaseMs: 1000,
  backoffCapMs: 15000,
  gracefulShutdownTimeoutMs: 10000,
  healthFailureThreshold: 3
} as const;

/** Supervises one local backend process across its lifetime. */
export class BackendSupervisor {
  private readonly spawner: ProcessSpawner;
  private readonly clock: ClockPort;
  private readonly http: HttpProbePort;
  private readonly logger: ShellLogger;
  private readonly options: SupervisorOptions;
  private readonly discovery: PortDiscovery;
  private readonly listeners = new Set<StatusListener>();

  private child: ChildProcessLike | null = null;
  private generation = 0;
  private stopping = false;
  private consecutiveFailures = 0;
  private lastError: string | null = null;
  private state: BackendState = 'stopped';
  private baseUrl: string | null = null;
  private sessionToken: string | null = null;
  private stderrTail: string[] = [];

  constructor(
    spawner: ProcessSpawner,
    fs: FileSystemPort,
    clock: ClockPort,
    http: HttpProbePort,
    logger: ShellLogger,
    options: SupervisorOptions
  ) {
    this.spawner = spawner;
    this.clock = clock;
    this.http = http;
    this.logger = logger;
    this.options = options;
    this.discovery = new PortDiscovery(fs, () => clock.now());
  }

  /** Current supervisor status snapshot. */
  get status(): BackendStatus {
    return {
      state: this.state,
      baseUrl: this.baseUrl,
      sessionToken: this.sessionToken,
      consecutiveFailures: this.consecutiveFailures,
      lastError: this.lastError
    };
  }

  /** Registers a status listener; returns an unsubscribe function. */
  onStatusChange(listener: StatusListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /**
   * Starts the backend and waits until readiness (handshake + health probe).
   * On crash before readiness, retries with bounded backoff; after the failure
   * budget is exhausted the supervisor enters 'recovering' and stops retrying.
   */
  async start(): Promise<BackendStatus> {
    this.stopping = false;
    while (!this.stopping) {
      this.setState('starting');
      const launched = this.launch();
      if (!launched) {
        if (!this.registerFailure('spawn failed')) {
          break;
        }
        await this.backoff();
        continue;
      }

      const ready = await this.waitForReady();
      if (this.stopping) {
        break;
      }

      if (ready) {
        this.consecutiveFailures = 0;
        this.lastError = null;
        this.setState('ready');
        void this.healthLoop(this.generation);
        return this.status;
      }

      // Startup failed (timeout or early exit): tear down and maybe retry.
      this.killChild();
      if (!this.registerFailure(this.lastError ?? 'backend did not become ready')) {
        break;
      }
      await this.backoff();
    }

    return this.status;
  }

  /**
   * Graceful stop (§2.3): request the backend's local shutdown endpoint with
   * the session token, wait for the process to exit within the timeout, then
   * force-kill as a last resort. Idempotent.
   */
  async stop(): Promise<void> {
    if (this.stopping && this.state === 'stopped') {
      return;
    }

    this.stopping = true;
    this.generation++;
    const child = this.child;
    const baseUrl = this.baseUrl;
    const token = this.sessionToken;

    if (child && baseUrl && token) {
      try {
        await this.http.post(
          `${baseUrl}/api/system/shutdown`,
          null,
          { [SESSION_TOKEN_HEADER]: token }
        );
      } catch {
        // Shutdown request failed; the exit wait + kill fallback still apply.
      }

      const exited = await this.waitForExit(child, this.option('gracefulShutdownTimeoutMs'));
      if (!exited) {
        this.logger.warn('Backend did not exit within the graceful timeout; forcing termination.');
        this.killChild();
      }
    } else {
      this.killChild();
    }

    this.child = null;
    this.baseUrl = null;
    this.sessionToken = null;
    this.setState('stopped');
  }

  /** Restarts the backend (manual recovery action). */
  async restart(): Promise<BackendStatus> {
    await this.stop();
    this.consecutiveFailures = 0;
    this.lastError = null;
    return this.start();
  }

  // ------------------------------------------------------------------
  // Internals
  // ------------------------------------------------------------------

  private launch(): boolean {
    this.generation++;
    this.stderrTail = [];
    try {
      const child = this.spawner.spawn(
        this.options.backendCommand,
        this.options.backendArgs,
        this.options.spawnOptions ?? {}
      );
      this.child = child;
      const generation = this.generation;
      child.onExit((code, signal) => this.handleExit(generation, code, signal));
      child.onStderr((chunk) => {
        this.stderrTail.push(chunk);
        if (this.stderrTail.length > 50) {
          this.stderrTail.shift();
        }
      });
      return true;
    } catch (error) {
      this.lastError = error instanceof Error ? error.message : String(error);
      this.logger.error('Backend spawn failed.', error);
      return false;
    }
  }

  private handleExit(generation: number, code: number | null, signal: string | null): void {
    if (generation !== this.generation || this.stopping) {
      return;
    }

    this.child = null;
    const detail = this.stderrTail.join('').trim();
    this.lastError = `backend exited (code=${code ?? 'null'}, signal=${signal ?? 'null'})${detail ? `: ${detail.slice(-400)}` : ''}`;

    // Only post-ready crashes drive the restart path here; exits during
    // startup are handled by waitForReady (single failure registration).
    if (this.state !== 'ready') {
      return;
    }

    this.baseUrl = null;
    this.sessionToken = null;
    this.logger.warn(this.lastError);
    this.setState('crashed');
    if (this.registerFailure(this.lastError)) {
      void this.backoff().then(() => {
        if (!this.stopping && this.state === 'crashed') {
          void this.start();
        }
      });
    }
  }

  private async waitForReady(): Promise<boolean> {
    const deadline = this.clock.now() + this.option('startupTimeoutMs');
    const generation = this.generation;
    while (!this.stopping && generation === this.generation && this.clock.now() < deadline) {
      // Liveness first: a dead process can never be ready, even if a stale
      // descriptor and a lingering listener would answer the probe.
      if (this.child === null || this.child.exitCode !== null || this.child.killed) {
        this.lastError ??= `backend exited during startup (code=${this.child?.exitCode ?? 'null'})`;
        return false;
      }

      const result = await this.discovery.discover(this.options.dataDirectory);
      if (result.ok) {
        const probe = await this.http.get(`${result.baseUrl}/api/health`);
        if (probe !== null && probe.status === 200) {
          this.baseUrl = result.baseUrl;
          this.sessionToken = result.descriptor.sessionToken;
          return true;
        }
      }

      await this.clock.delay(250);
    }

    if (this.lastError === null) {
      this.lastError = 'backend startup timed out before readiness';
    }
    return false;
  }

  private async healthLoop(generation: number): Promise<void> {
    let failures = 0;
    const threshold = this.option('healthFailureThreshold');
    while (!this.stopping && generation === this.generation && this.state === 'ready') {
      await this.clock.delay(this.option('healthIntervalMs'));
      if (this.stopping || generation !== this.generation || this.state !== 'ready') {
        return;
      }

      const baseUrl = this.baseUrl;
      if (baseUrl === null) {
        return;
      }

      const probe = await this.http.get(`${baseUrl}/api/health`);
      if (probe !== null && probe.status === 200) {
        failures = 0;
        continue;
      }

      failures++;
      if (failures >= threshold) {
        this.logger.warn('Backend health probe failed repeatedly; restarting.');
        this.lastError = 'backend stopped responding to health probes';
        this.setState('unresponsive');
        this.killChild();
        if (this.registerFailure(this.lastError)) {
          await this.backoff();
          if (!this.stopping) {
            void this.start();
          }
        }
        return;
      }
    }
  }

  private async waitForExit(child: ChildProcessLike, timeoutMs: number): Promise<boolean> {
    const deadline = this.clock.now() + timeoutMs;
    while (this.clock.now() < deadline) {
      if (child.exitCode !== null || child.killed) {
        return true;
      }
      await this.clock.delay(100);
    }
    return child.exitCode !== null || child.killed;
  }

  private killChild(): void {
    const child = this.child;
    if (child && !child.killed && child.exitCode === null) {
      try {
        child.kill();
      } catch (error) {
        this.logger.error('Failed to terminate backend process.', error);
      }
    }
  }

  /** Records a failure; returns false when the retry budget is exhausted. */
  private registerFailure(reason: string): boolean {
    this.consecutiveFailures++;
    this.lastError = reason;
    if (this.consecutiveFailures >= this.option('maxConsecutiveFailures')) {
      this.setState('recovering');
      this.logger.error(
        `Backend failed ${this.consecutiveFailures} times consecutively; entering recovery.`
      );
      return false;
    }
    return true;
  }

  private async backoff(): Promise<void> {
    const base = this.option('backoffBaseMs');
    const cap = this.option('backoffCapMs');
    const exponent = Math.max(0, this.consecutiveFailures - 1);
    const delay = Math.min(cap, base * 2 ** exponent);
    await this.clock.delay(delay);
  }

  private option<K extends keyof typeof DEFAULTS>(key: K): number {
    return ((this.options as unknown as Record<string, unknown>)[key] as number | undefined) ?? DEFAULTS[key];
  }

  private setState(state: BackendState): void {
    this.state = state;
    const snapshot = this.status;
    for (const listener of this.listeners) {
      try {
        listener(snapshot);
      } catch (error) {
        this.logger.error('Status listener threw.', error);
      }
    }
  }
}
