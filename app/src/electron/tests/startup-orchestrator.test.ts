import { afterEach, describe, expect, it } from 'vitest';
import { PluginScanner } from '../src/plugins/plugin-scanner';
import { RecoveryService } from '../src/recovery/recovery-service';
import { BackendSupervisor } from '../src/supervisor/backend-supervisor';
import { StartupOrchestrator } from '../src/startup/startup-orchestrator';
import { WindowManager } from '../src/windows/window-manager';
import { WindowKind, WindowState } from '../src/types';
import { FakeClock, FakeFileSystem, FakeHttp, FakeLogger, FakeSpawner, handshakeJson } from './fakes';

const DATA_DIR = 'C:/data';
const PORT = 54000;

// Drain live supervisors so fake-clock health loops cannot spin after tests.
const active: BackendSupervisor[] = [];
afterEach(async () => {
  for (const supervisor of active.splice(0)) {
    await supervisor.stop();
  }
});

function setup() {
  const spawner = new FakeSpawner();
  const fs = new FakeFileSystem();
  const clock = new FakeClock();
  const http = new FakeHttp();
  const logger = new FakeLogger();
  const supervisor = new BackendSupervisor(spawner, fs, clock, http, logger, {
    backendCommand: 'dotnet',
    backendArgs: ['Host.dll'],
    dataDirectory: DATA_DIR
  });
  active.push(supervisor);

  const opened: { kind: WindowKind; ready: boolean }[] = [];
  const closed: string[] = [];
  const windows = new WindowManager(
    {
      create(kind: WindowKind, _state: WindowState) {
        return {
          id: kind,
          focus() {},
          close() {
            closed.push(kind);
          },
          isVisible: () => true,
          getState: () => ({ width: 800, height: 600 }),
          onDestroyed() {},
          loadRoute: async () => {}
        } as never;
      }
    },
    fs,
    logger,
    DATA_DIR
  );
  const originalOpen = windows.open.bind(windows);
  windows.open = async (identity, ready) => {
    opened.push({ kind: identity.kind, ready });
    return originalOpen(identity, ready);
  };

  const recovery = new RecoveryService(fs, http, logger, supervisor, DATA_DIR);
  const scanCalls: boolean[] = [];
  const plugins = new PluginScanner(fs, logger, async () => true, null);
  const originalScan = plugins.scanAfterHydration.bind(plugins);
  plugins.scanAfterHydration = async (data, workspace, hydrated) => {
    scanCalls.push(hydrated);
    return originalScan(data, workspace, hydrated);
  };

  let hydrated = true;
  const orchestrator = new StartupOrchestrator(
    supervisor,
    windows,
    recovery,
    plugins,
    {
      wait: async () => {
        if (!hydrated) {
          throw new Error('timeout');
        }
      }
    },
    logger,
    DATA_DIR,
    null
  );

  return {
    spawner,
    fs,
    http,
    supervisor,
    windows,
    orchestrator,
    opened,
    closed,
    scanCalls,
    setHydrated: (value: boolean) => {
      hydrated = value;
    }
  };
}

function healthy(http: FakeHttp): void {
  http.responder = (method, url) =>
    method === 'GET' && url.endsWith('/api/health') ? { status: 200, body: '{}' } : null;
}

describe('StartupOrchestrator', () => {
  it('runs splash → backend → main hub and scans plugins after hydration', async () => {
    const { fs, http, orchestrator, opened, scanCalls } = setup();
    fs.seed(`${DATA_DIR}/loopback-handshake.json`, handshakeJson(PORT));
    healthy(http);

    const outcome = await orchestrator.run();
    expect(outcome).toBe('ready');
    expect(opened.map((entry) => entry.kind)).toEqual(['splash', 'main']);
    expect(scanCalls).toEqual([true]);
  });

  it('diverts to recovery when the backend exhausts its budget', async () => {
    const { spawner, orchestrator, opened, scanCalls } = setup();
    spawner.exitOnSpawn = 1;

    const outcome = await orchestrator.run();
    expect(outcome).toBe('recovery');
    expect(opened.map((entry) => entry.kind)).toEqual(['splash', 'recovery']);
    expect(scanCalls).toHaveLength(0);
  });

  it('defers the plugin scan when hydration times out', async () => {
    const { fs, http, orchestrator, scanCalls, setHydrated } = setup();
    fs.seed(`${DATA_DIR}/loopback-handshake.json`, handshakeJson(PORT));
    healthy(http);
    setHydrated(false);

    const outcome = await orchestrator.run();
    expect(outcome).toBe('ready');
    expect(scanCalls).toHaveLength(0);
  });

  it('retryFromRecovery re-enters the hub on success', async () => {
    const { spawner, fs, http, orchestrator, opened } = setup();
    spawner.exitOnSpawn = 1;
    expect(await orchestrator.run()).toBe('recovery');

    spawner.exitOnSpawn = null;
    fs.seed(`${DATA_DIR}/loopback-handshake.json`, handshakeJson(PORT));
    healthy(http);

    expect(await orchestrator.retryFromRecovery()).toBe('ready');
    expect(opened[opened.length - 1].kind).toBe('main');
  });

  it('retryFromRecovery stays in recovery on repeated failure', async () => {
    const { spawner, orchestrator } = setup();
    spawner.exitOnSpawn = 1;
    await orchestrator.run();
    expect(await orchestrator.retryFromRecovery()).toBe('recovery');
  });
});
