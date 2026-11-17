import { describe, expect, it } from 'vitest';
import { PortDiscovery, HANDSHAKE_MAX_AGE_MS } from '../src/handshake/port-discovery';
import { FakeClock, FakeFileSystem, handshakeJson } from './fakes';

const DATA_DIR = 'C:/Users/test/AppData/Roaming/CrawlForge';
const HANDSHAKE_PATH = `${DATA_DIR}/loopback-handshake.json`;

function setup() {
  const fs = new FakeFileSystem();
  const clock = new FakeClock();
  const discovery = new PortDiscovery(fs, () => clock.now());
  return { fs, clock, discovery };
}

describe('PortDiscovery', () => {
  it('discovers a valid descriptor and builds the loopback origin', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(51234, 'tok-1'));
    const result = await discovery.discover(DATA_DIR);
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.baseUrl).toBe('http://127.0.0.1:51234');
      expect(result.descriptor.sessionToken).toBe('tok-1');
      expect(result.descriptor.port).toBe(51234);
    }
  });

  it('reports missing when the file does not exist', async () => {
    const { discovery } = setup();
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'missing' });
  });

  it('reports unreadable when the filesystem throws', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(1));
    fs.failReads = true;
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'unreadable' });
  });

  it('rejects malformed JSON', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, '{not json');
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'malformed' });
  });

  it('rejects unsupported schema versions', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, JSON.stringify({ version: 2, port: 80, sessionToken: 't', createdAtUtc: new Date(1_700_000_000_000).toISOString() }));
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'unsupported-version' });
  });

  it.each([0, -1, 65536, 1.5, '8080'])('rejects invalid port %s', async (port) => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, JSON.stringify({ version: 1, port, sessionToken: 't', createdAtUtc: new Date(1_700_000_000_000).toISOString() }));
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'invalid-port' });
  });

  it('rejects an empty session token', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(8080, ''));
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'missing-token' });
  });

  it('rejects stale descriptors beyond the max age', async () => {
    const { fs, clock, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(8080));
    clock.current += HANDSHAKE_MAX_AGE_MS + 1;
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'stale' });
  });

  it('accepts descriptors just inside the max age', async () => {
    const { fs, clock, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(8080));
    clock.current += HANDSHAKE_MAX_AGE_MS - 1;
    const result = await discovery.discover(DATA_DIR);
    expect(result.ok).toBe(true);
  });

  it('rejects an unparseable timestamp', async () => {
    const { fs, discovery } = setup();
    fs.seed(HANDSHAKE_PATH, handshakeJson(8080, 't', 'not-a-date'));
    const result = await discovery.discover(DATA_DIR);
    expect(result).toEqual({ ok: false, reason: 'malformed' });
  });
});
