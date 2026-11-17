import { afterEach, describe, expect, it } from 'vitest';
import { RecoveryService, classifyArtifact } from '../src/recovery/recovery-service';
import { BackendSupervisor } from '../src/supervisor/backend-supervisor';
import { SESSION_TOKEN_HEADER } from '../src/types';
import { FakeClock, FakeFileSystem, FakeHttp, FakeLogger, FakeSpawner, handshakeJson } from './fakes';

const DATA_DIR = 'C:/data';
const PORT = 53000;

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
  const recovery = new RecoveryService(fs, http, logger, supervisor, DATA_DIR);
  return { spawner, fs, clock, http, logger, supervisor, recovery };
}

describe('RecoveryService', () => {
  it('builds a report from supervisor state and local artifacts', async () => {
    const { fs, recovery } = setup();
    fs.seed(`${DATA_DIR}/logs/backend.log`, 'log');
    fs.seed(`${DATA_DIR}/crash-dumps/crash.dmp`, 'dump');
    fs.seed(`${DATA_DIR}/diagnostics/loopback-handshake.json`, '{}');

    const report = await recovery.buildReport();
    expect(report.backendState).toBe('stopped');
    expect(report.artifacts).toHaveLength(3);
    const kinds = report.artifacts.map((artifact) => artifact.kind).sort();
    expect(kinds).toEqual(['crash-dump', 'descriptor', 'log']);
  });

  it('purges through the backend API when ready (registry-tracked)', async () => {
    const { fs, http, supervisor, recovery } = setup();
    fs.seed(`${DATA_DIR}/loopback-handshake.json`, handshakeJson(PORT, 'tok'));
    http.responder = (method, url) => {
      if (method === 'GET' && url.endsWith('/api/health')) return { status: 200, body: '{}' };
      if (method === 'DELETE' && url.endsWith('/api/diagnostics/artifacts')) {
        return { status: 200, body: '{"data":{"purged":5}}' };
      }
      return null;
    };
    await supervisor.start();
    fs.seed(`${DATA_DIR}/logs/keep.log`, 'x');

    const purged = await recovery.purgeDiagnostics();
    expect(purged).toBe(5);
    const call = http.requests.find((request) => request.method === 'DELETE');
    expect(call?.headers?.[SESSION_TOKEN_HEADER]).toBe('tok');
    // Local files are untouched when the API handled the purge.
    expect(fs.files.has(`${DATA_DIR}/logs/keep.log`)).toBe(true);
  });

  it('falls back to local deletion when the backend is down', async () => {
    const { fs, recovery } = setup();
    fs.seed(`${DATA_DIR}/logs/a.log`, 'x');
    fs.seed(`${DATA_DIR}/crash-dumps/b.dmp`, 'y');

    const purged = await recovery.purgeDiagnostics();
    expect(purged).toBe(2);
    expect(fs.files.size).toBe(0);
  });

  it('retry restarts the supervisor and reports readiness', async () => {
    const { fs, http, recovery } = setup();
    fs.seed(`${DATA_DIR}/loopback-handshake.json`, handshakeJson(PORT));
    http.responder = (method, url) =>
      method === 'GET' && url.endsWith('/api/health') ? { status: 200, body: '{}' } : null;

    expect(await recovery.retry()).toBe(true);
  });

  it('retry reports failure when the backend cannot start', async () => {
    const { spawner, recovery } = setup();
    spawner.exitOnSpawn = 1;
    expect(await recovery.retry()).toBe(false);
  });

  it('classifies artifacts by name', () => {
    expect(classifyArtifact('crash.dmp')).toBe('crash-dump');
    expect(classifyArtifact('minidump-2026.dmp')).toBe('crash-dump');
    expect(classifyArtifact('backend.log')).toBe('log');
    expect(classifyArtifact('loopback-handshake.json')).toBe('descriptor');
    expect(classifyArtifact('mcp-endpoint.json')).toBe('descriptor');
    expect(classifyArtifact('other.bin')).toBe('other');
  });
});
