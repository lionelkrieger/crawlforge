import { afterEach, describe, expect, it } from 'vitest';
import { BackendSupervisor, SupervisorOptions } from '../src/supervisor/backend-supervisor';
import { SESSION_TOKEN_HEADER } from '../src/types';
import { FakeClock, FakeFileSystem, FakeHttp, FakeLogger, FakeSpawner, handshakeJson } from './fakes';

const DATA_DIR = 'C:/data';
const HANDSHAKE_PATH = `${DATA_DIR}/loopback-handshake.json`;
const PORT = 51999;
const BASE = `http://127.0.0.1:${PORT}`;

// The fake clock resolves delays instantly, so a live health loop would spin
// forever after a test ends. Every supervisor is drained after each test.
const active: BackendSupervisor[] = [];
afterEach(async () => {
  for (const supervisor of active.splice(0)) {
    await supervisor.stop();
  }
});

function setup(overrides: Partial<SupervisorOptions> = {}) {
  const spawner = new FakeSpawner();
  const fs = new FakeFileSystem();
  const clock = new FakeClock();
  const http = new FakeHttp();
  const logger = new FakeLogger();
  const options: SupervisorOptions = {
    backendCommand: 'dotnet',
    backendArgs: ['CrawlForge.Host.dll'],
    dataDirectory: DATA_DIR,
    startupTimeoutMs: 5000,
    healthIntervalMs: 1000,
    ...overrides
  };
  const supervisor = new BackendSupervisor(spawner, fs, clock, http, logger, options);
  active.push(supervisor);
  return { spawner, fs, clock, http, logger, supervisor };
}

function healthy(http: FakeHttp): void {
  http.responder = (method, url) => {
    if (method === 'GET' && url === `${BASE}/api/health`) {
      return { status: 200, body: '{"data":{"ready":true}}' };
    }
    return null;
  };
}

/** Flushes pending async chains (each fake-clock delay is one event-loop turn). */
async function flush(turns = 400): Promise<void> {
  for (let index = 0; index < turns; index++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

describe('BackendSupervisor', () => {
  it('starts, discovers the handshake, and becomes ready through the health probe', async () => {
    const { spawner, fs, http, supervisor } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT, 'tok-ready'));
    healthy(http);

    const status = await supervisor.start();
    expect(status.state).toBe('ready');
    expect(status.baseUrl).toBe(BASE);
    expect(status.sessionToken).toBe('tok-ready');
    expect(spawner.calls).toHaveLength(1);
    expect(spawner.calls[0].command).toBe('dotnet');
    // Health is probed without a token (exempt route).
    const healthCall = http.requests.find((request) => request.url.endsWith('/api/health'));
    expect(healthCall?.headers?.[SESSION_TOKEN_HEADER]).toBeUndefined();
  });

  it('waits for the handshake to appear during startup', async () => {
    const { fs, http, supervisor } = setup();
    healthy(http);
    // Handshake appears only after a few probe cycles.
    let reads = 0;
    const original = fs.readTextFile.bind(fs);
    fs.readTextFile = async (path: string) => {
      reads++;
      if (reads === 3) {
        fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
      }
      return original(path);
    };

    const status = await supervisor.start();
    expect(status.state).toBe('ready');
    expect(reads).toBeGreaterThanOrEqual(3);
  });

  it('enters recovery after the startup failure budget is exhausted', async () => {
    const { spawner, supervisor } = setup({ maxConsecutiveFailures: 2 });
    spawner.exitOnSpawn = 1;

    const status = await supervisor.start();
    expect(status.state).toBe('recovering');
    expect(status.consecutiveFailures).toBe(2);
    expect(spawner.calls).toHaveLength(2);
    expect(status.lastError).toContain('backend exited');
  });

  it('restarts with bounded backoff after a post-ready crash', async () => {
    const { spawner, fs, clock, http, supervisor } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
    healthy(http);

    await supervisor.start();
    expect(supervisor.status.state).toBe('ready');

    spawner.children[0].exit(1);
    await flush();

    expect(spawner.calls.length).toBeGreaterThanOrEqual(2);
    expect(supervisor.status.state).toBe('ready');
    expect(supervisor.status.consecutiveFailures).toBe(0);
    // Exponential backoff was applied (base 1000ms for the first retry).
    expect(clock.delays).toContain(1000);
  });

  it('stops retrying and enters recovery after repeated crashes', async () => {
    const { spawner, fs, http, supervisor } = setup({ maxConsecutiveFailures: 2 });
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
    healthy(http);

    await supervisor.start();
    // Every subsequent launch dies before readiness; the running one crashes.
    spawner.exitOnSpawn = 1;
    spawner.children[0].exit(1);
    await flush();

    expect(supervisor.status.state).toBe('recovering');
    expect(supervisor.status.consecutiveFailures).toBe(2);
  });

  it('requests graceful shutdown with the session token and waits for exit', async () => {
    const { spawner, fs, http, supervisor } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT, 'tok-stop'));
    healthy(http);
    await supervisor.start();

    http.responder = (method, url) => {
      if (method === 'POST' && url === `${BASE}/api/system/shutdown`) {
        spawner.children[0].exit(0);
        return { status: 200, body: '{"stopping":true}' };
      }
      return null;
    };

    await supervisor.stop();
    expect(supervisor.status.state).toBe('stopped');
    const shutdown = http.requests.find((request) => request.url.endsWith('/api/system/shutdown'));
    expect(shutdown?.headers?.[SESSION_TOKEN_HEADER]).toBe('tok-stop');
    expect(spawner.children[0].killed).toBe(false);
  });

  it('force-kills when the graceful shutdown times out', async () => {
    const { spawner, fs, http, supervisor } = setup({ gracefulShutdownTimeoutMs: 500 });
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
    healthy(http);
    await supervisor.start();
    // Shutdown endpoint answers but the process never exits.
    http.responder = (method, url) =>
      method === 'POST' && url.endsWith('/api/system/shutdown') ? { status: 200, body: '{}' } : null;

    await supervisor.stop();
    expect(supervisor.status.state).toBe('stopped');
    expect(spawner.children[0].killed).toBe(true);
  });

  it('restarts after repeated health-probe failures (unresponsive)', async () => {
    const { spawner, fs, http, supervisor } = setup({ healthFailureThreshold: 2 });
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
    let healthyMode = true;
    http.responder = (method, url) => {
      if (method === 'GET' && url === `${BASE}/api/health`) {
        return healthyMode ? { status: 200, body: '{}' } : null;
      }
      if (method === 'POST' && url.endsWith('/api/system/shutdown')) {
        return { status: 200, body: '{}' };
      }
      return null;
    };

    const states: string[] = [];
    supervisor.onStatusChange((status) => states.push(status.state));
    await supervisor.start();

    healthyMode = false;
    await flush();

    expect(states).toContain('unresponsive');
    expect(spawner.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('stop is idempotent and safe before start', async () => {
    const { supervisor } = setup();
    await supervisor.stop();
    await supervisor.stop();
    expect(supervisor.status.state).toBe('stopped');
  });

  it('restart resets the failure budget', async () => {
    const { spawner, fs, http, supervisor } = setup({ maxConsecutiveFailures: 2 });
    fs.seed(HANDSHAKE_PATH, handshakeJson(PORT));
    healthy(http);
    spawner.exitOnSpawn = 1;
    await supervisor.start();
    expect(supervisor.status.state).toBe('recovering');

    spawner.exitOnSpawn = null;
    const status = await supervisor.restart();
    expect(status.state).toBe('ready');
    expect(status.consecutiveFailures).toBe(0);
  });
});
