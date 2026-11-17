import { describe, expect, it } from 'vitest';
import {
  PluginScanner,
  PluginCandidate,
  PluginRegistryDelegate,
  validateManifest
} from '../src/plugins/plugin-scanner';
import { FakeFileSystem, FakeLogger } from './fakes';

const DATA_DIR = 'C:/data';
const WORKSPACE = 'C:/workspace';

function manifest(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    id: 'sample-plugin',
    name: 'Sample Plugin',
    version: '1.0.0',
    entry: 'index.js',
    apiVersion: 1,
    ...overrides
  });
}

function setup(trust = true) {
  const fs = new FakeFileSystem();
  const logger = new FakeLogger();
  const prompted: PluginCandidate[] = [];
  const registered: PluginCandidate[] = [];
  const registry: PluginRegistryDelegate = {
    async register(candidate) {
      registered.push(candidate);
    }
  };
  const scanner = new PluginScanner(
    fs,
    logger,
    async (candidate) => {
      prompted.push(candidate);
      return trust;
    },
    registry
  );
  return { fs, logger, prompted, registered, scanner };
}

describe('PluginScanner (§6.2 insertion point)', () => {
  it('refuses to run before hydration', async () => {
    const { scanner } = setup();
    await expect(scanner.scanAfterHydration(DATA_DIR, null, false)).rejects.toThrow(/hydration/);
  });

  it('scans user and workspace directories after hydration', async () => {
    const { fs, scanner, registered } = setup();
    fs.seed(`${DATA_DIR}/plugins/user-plugin/plugin.json`, manifest({ id: 'user-plugin' }));
    fs.seed(`${WORKSPACE}/plugins/ws-plugin/plugin.json`, manifest({ id: 'ws-plugin' }));

    const result = await scanner.scanAfterHydration(DATA_DIR, WORKSPACE, true);
    expect(result.registered).toHaveLength(2);
    expect(result.errors).toHaveLength(0);
    expect(registered.map((candidate) => candidate.source).sort()).toEqual(['user', 'workspace']);
  });

  it('skips directories without a manifest', async () => {
    const { fs, scanner } = setup();
    fs.seed(`${DATA_DIR}/plugins/not-a-plugin/readme.txt`, 'hello');
    const result = await scanner.scanAfterHydration(DATA_DIR, null, true);
    expect(result.registered).toHaveLength(0);
    expect(result.errors).toHaveLength(0);
  });

  it('collects manifest errors without aborting the scan', async () => {
    const { fs, scanner } = setup();
    fs.seed(`${DATA_DIR}/plugins/bad-json/plugin.json`, '{oops');
    fs.seed(`${DATA_DIR}/plugins/bad-shape/plugin.json`, manifest({ apiVersion: 99 }));
    fs.seed(`${DATA_DIR}/plugins/good/plugin.json`, manifest({ id: 'good' }));

    const result = await scanner.scanAfterHydration(DATA_DIR, null, true);
    expect(result.registered).toHaveLength(1);
    expect(result.errors).toHaveLength(2);
  });

  it('honors trust decline (consent before load)', async () => {
    const { fs, scanner, registered, prompted } = setup(false);
    fs.seed(`${DATA_DIR}/plugins/p/plugin.json`, manifest());

    const result = await scanner.scanAfterHydration(DATA_DIR, null, true);
    expect(result.declined).toHaveLength(1);
    expect(result.registered).toHaveLength(0);
    expect(registered).toHaveLength(0);
    expect(prompted).toHaveLength(1);
  });

  it('reports registry failures as scan errors', async () => {
    const fs = new FakeFileSystem();
    const scanner = new PluginScanner(
      fs,
      new FakeLogger(),
      async () => true,
      {
        async register() {
          throw new Error('registry offline');
        }
      }
    );
    fs.seed(`${DATA_DIR}/plugins/p/plugin.json`, manifest());
    const result = await scanner.scanAfterHydration(DATA_DIR, null, true);
    expect(result.registered).toHaveLength(0);
    expect(result.errors[0].reason).toContain('registry offline');
  });

  it('stays inert (no load) when the WP-06-06 registry is not yet bound', async () => {
    const fs = new FakeFileSystem();
    const scanner = new PluginScanner(fs, new FakeLogger(), async () => true, null);
    fs.seed(`${DATA_DIR}/plugins/p/plugin.json`, manifest());
    const result = await scanner.scanAfterHydration(DATA_DIR, null, true);
    expect(result.registered).toHaveLength(1); // consented, reported, not loaded
  });
});

describe('validateManifest', () => {
  it('accepts a well-formed manifest', () => {
    const result = validateManifest(JSON.parse(manifest()));
    expect(result.ok).toBe(true);
  });

  it.each([
    [{ id: 'Bad_ID' }, 'id'],
    [{ name: '' }, 'name'],
    [{ version: 'abc' }, 'version'],
    [{ entry: '' }, 'entry'],
    [{ entry: '../escape.js' }, 'relative'],
    [{ entry: '/absolute.js' }, 'relative'],
    [{ entry: 'C:/abs.js' }, 'relative'],
    [{ apiVersion: 2 }, 'apiVersion']
  ])('rejects invalid field %#', (overrides, reasonPart) => {
    const result = validateManifest(JSON.parse(manifest(overrides)));
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.reason).toContain(reasonPart);
    }
  });

  it('rejects non-objects', () => {
    expect(validateManifest(null).ok).toBe(false);
    expect(validateManifest('text').ok).toBe(false);
  });
});
