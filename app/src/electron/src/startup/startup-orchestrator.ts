/**
 * Startup orchestration (WP-06-01, process model §2.4 + shell §12):
 * splash → backend supervision → main hub window → hydration → plugin scan
 * insertion (§6.2). When the backend exhausts its retry budget the flow
 * diverts to the recovery window instead of the hub. Offline startup is the
 * default path — nothing in this flow touches the network beyond loopback.
 */
import { PluginScanner } from '../plugins/plugin-scanner';
import { RecoveryService } from '../recovery/recovery-service';
import { BackendSupervisor } from '../supervisor/backend-supervisor';
import { WindowManager } from '../windows/window-manager';
import { ShellLogger } from '../types';

/** Waits for the renderer's hydration signal (IPC-bound in the entry). */
export interface HydrationSignal {
  /** Resolves when the main window reports hydrated; rejects on timeout. */
  wait(timeoutMs: number): Promise<void>;
}

/** Outcome of the startup flow. */
export type StartupOutcome = 'ready' | 'recovery';

/** Coordinates the splash → hub (or recovery) startup sequence. */
export class StartupOrchestrator {
  private readonly supervisor: BackendSupervisor;
  private readonly windows: WindowManager;
  private readonly recovery: RecoveryService;
  private readonly plugins: PluginScanner;
  private readonly hydration: HydrationSignal;
  private readonly logger: ShellLogger;
  private readonly dataDirectory: string;
  private readonly workspaceDirectory: string | null;
  private readonly hydrationTimeoutMs: number;

  constructor(
    supervisor: BackendSupervisor,
    windows: WindowManager,
    recovery: RecoveryService,
    plugins: PluginScanner,
    hydration: HydrationSignal,
    logger: ShellLogger,
    dataDirectory: string,
    workspaceDirectory: string | null = null,
    hydrationTimeoutMs = 30000
  ) {
    this.supervisor = supervisor;
    this.windows = windows;
    this.recovery = recovery;
    this.plugins = plugins;
    this.hydration = hydration;
    this.logger = logger;
    this.dataDirectory = dataDirectory;
    this.workspaceDirectory = workspaceDirectory;
    this.hydrationTimeoutMs = hydrationTimeoutMs;
  }

  /** Runs the full startup sequence; never throws for backend failures. */
  async run(): Promise<StartupOutcome> {
    await this.windows.open({ kind: 'splash' }, false);

    const status = await this.supervisor.start();
    if (status.state !== 'ready') {
      this.logger.warn(`Backend not ready (${status.state}); opening recovery.`);
      await this.windows.open({ kind: 'recovery' }, false);
      this.windows.closeAllExcept('recovery');
      return 'recovery';
    }

    await this.windows.open({ kind: 'main' }, true);
    this.windows.closeAllExcept('main');

    let hydrated = true;
    try {
      await this.hydration.wait(this.hydrationTimeoutMs);
    } catch {
      // Hydration timeout: the hub still works offline-first; the plugin
      // insertion point stays closed because §6.2 requires hydration.
      hydrated = false;
      this.logger.warn('Main window hydration timed out; plugin scan deferred.');
    }

    if (hydrated) {
      try {
        const result = await this.plugins.scanAfterHydration(
          this.dataDirectory,
          this.workspaceDirectory,
          true
        );
        this.logger.info(
          `Plugin scan: ${result.registered.length} registered, ${result.declined.length} declined, ${result.errors.length} errors.`
        );
      } catch (error) {
        this.logger.error('Plugin scan failed.', error);
      }
    }

    return 'ready';
  }

  /** Recovery-screen retry: restart and re-enter the hub flow on success. */
  async retryFromRecovery(): Promise<StartupOutcome> {
    const success = await this.recovery.retry();
    if (!success) {
      return 'recovery';
    }

    await this.windows.open({ kind: 'main' }, true);
    this.windows.closeAllExcept('main');
    return 'ready';
  }
}
