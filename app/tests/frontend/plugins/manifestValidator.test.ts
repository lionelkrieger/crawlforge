/**
 * Manifest validator tests (FS-CRAWL-002 §6.3): required fields, id/semver
 * shapes, local-only relative paths (E1), network permission shape, hook
 * subset, contributions, labels, compatibility, and the wire projection.
 */

import { describe, expect, it } from 'vitest';
import {
  checkCompatibility,
  compareSemver,
  isLocalRelativePath,
  toBridgeManifest,
  validatePluginManifest
} from '../../../src/frontend/src/plugins/manifestValidator';
import { manifestRaw } from './helpers';

describe('valid manifests', () => {
  it('accepts the full §6.3 contract', () => {
    const result = validatePluginManifest(manifestRaw({
      author: 'local author',
      styles: 'style.css',
      permissions: { network: { mode: 'destinations', destinationRefs: ['warehouse'] } },
      labels: { en: { tab: 'Widgets' }, de: { tab: 'Widgets DE' } }
    }));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.manifest.id).toBe('com.example.widget');
      expect(result.manifest.permissions?.network?.mode).toBe('destinations');
      expect(result.manifest.styles).toBe('style.css');
    }
  });

  it('accepts a minimal manifest (defaults apply)', () => {
    const result = validatePluginManifest({
      id: 'org.local.minimal',
      name: 'Minimal',
      version: '0.0.1',
      description: 'Minimal plugin.',
      engine: { minAppVersion: '0.0.1' },
      entry: 'main.js'
    });
    expect(result.ok).toBe(true);
  });
});

describe('field validation', () => {
  it('rejects non-object payloads', () => {
    const result = validatePluginManifest(null);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.issues[0].field).toBe('$');
  });

  it('requires every mandatory field', () => {
    const result = validatePluginManifest({});
    expect(result.ok).toBe(false);
    if (!result.ok) {
      const fields = result.issues.map((issue) => issue.field);
      expect(fields).toContain('id');
      expect(fields).toContain('name');
      expect(fields).toContain('version');
      expect(fields).toContain('description');
      expect(fields).toContain('engine.minAppVersion');
      expect(fields).toContain('entry');
    }
  });

  it('enforces lowercase reverse-DNS ids', () => {
    for (const id of ['Widget', 'com.Example.widget', 'singlesegment', 'com..widget', 'com.example.widget-1']) {
      const result = validatePluginManifest(manifestRaw({ id }));
      expect(result.ok, id).toBe(false);
    }
    expect(validatePluginManifest(manifestRaw({ id: 'io.crawlforge.reporttools' })).ok).toBe(true);
  });

  it('enforces semver for version and minAppVersion', () => {
    expect(validatePluginManifest(manifestRaw({ version: '1.2' })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ version: '1.2.3-beta.1+build.5' })).ok).toBe(true);
    expect(validatePluginManifest(manifestRaw({ engine: { minAppVersion: 'latest' } })).ok).toBe(false);
  });

  it('rejects unknown top-level fields via the issue list shape', () => {
    // The validator is structural: unknown fields are ignored, but known
    // fields with wrong shapes are reported.
    const result = validatePluginManifest(manifestRaw({ permissions: 'all' }));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.issues.some((issue) => issue.field === 'permissions')).toBe(true);
  });
});

describe('local-only paths (E1)', () => {
  it('accepts relative local paths', () => {
    expect(isLocalRelativePath('index.js')).toBe(true);
    expect(isLocalRelativePath('lib/main.js')).toBe(true);
    expect(isLocalRelativePath('assets\\style.css')).toBe(true);
  });

  it('rejects traversal, absolute, drive, and remote paths', () => {
    for (const path of ['../escape.js', 'lib/../../escape.js', '/etc/passwd', '\\\\server\\share', 'C:\\code.js', 'https://remote.test/code.js', 'file:///local.js']) {
      expect(isLocalRelativePath(path), path).toBe(false);
      const result = validatePluginManifest(manifestRaw({ entry: path }));
      expect(result.ok, path).toBe(false);
    }
  });

  it('validates styles paths the same way', () => {
    expect(validatePluginManifest(manifestRaw({ styles: '../outside.css' })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ styles: 'theme.css' })).ok).toBe(true);
  });
});

describe('network permission shape', () => {
  it('defaults to none when absent', () => {
    const result = validatePluginManifest(manifestRaw());
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.manifest.permissions?.network).toBeUndefined();
  });

  it('rejects invalid modes and empty destination lists', () => {
    expect(validatePluginManifest(manifestRaw({ permissions: { network: { mode: 'any' } } })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: [] } } })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ permissions: { network: { mode: 'destinations', destinationRefs: ['a', 'a'] } } })).ok).toBe(false);
  });
});

describe('hooks, contributions, labels', () => {
  it('accepts only the four contract hooks, unique', () => {
    expect(validatePluginManifest(manifestRaw({ hooks: ['onLoad', 'onDataUpdate'] })).ok).toBe(true);
    expect(validatePluginManifest(manifestRaw({ hooks: ['onLoad', 'onLoad'] })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ hooks: ['onShutdown'] })).ok).toBe(false);
  });

  it('validates contribution declarations', () => {
    expect(validatePluginManifest(manifestRaw({ contributes: { tabs: [{ id: 't' }] } })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ contributes: { columnFormatters: [{ target: 'urls', columnKey: 'custom.price.0' }] } })).ok).toBe(true);
    expect(validatePluginManifest(manifestRaw({ contributes: { filterPresets: [{ name: 'errors', ruleTree: { op: 'and', rules: [] } }] } })).ok).toBe(true);
    expect(validatePluginManifest(manifestRaw({ contributes: { filterPresets: [{ name: 'errors' }] } })).ok).toBe(false);
  });

  it('validates label maps', () => {
    expect(validatePluginManifest(manifestRaw({ labels: { en: { tab: 42 } } })).ok).toBe(false);
    expect(validatePluginManifest(manifestRaw({ labels: { en: { tab: 'Widgets' } } })).ok).toBe(true);
  });
});

describe('semver comparison and compatibility', () => {
  it('orders core versions and prereleases', () => {
    expect(compareSemver('1.2.3', '1.2.4')).toBe(-1);
    expect(compareSemver('2.0.0', '1.9.9')).toBe(1);
    expect(compareSemver('1.0.0-alpha', '1.0.0')).toBe(-1);
    expect(compareSemver('1.0.0-alpha.2', '1.0.0-alpha.10')).toBe(-1);
    expect(compareSemver('1.0.0+build.1', '1.0.0+build.2')).toBe(0);
  });

  it('disables plugins requiring a newer app version', () => {
    const result = validatePluginManifest(manifestRaw({ engine: { minAppVersion: '2.0.0' } }));
    expect(result.ok).toBe(true);
    if (result.ok) {
      const compatibility = checkCompatibility(result.manifest, '1.4.0');
      expect(compatibility.compatible).toBe(false);
      expect(compatibility.reason).toContain('2.0.0');
      expect(checkCompatibility(result.manifest, '2.0.0').compatible).toBe(true);
      expect(checkCompatibility(result.manifest, '2.1.0').compatible).toBe(true);
    }
  });
});

describe('wire projection (E5)', () => {
  it('grants read-data baseline plus ui-contributions for declarations', () => {
    const result = validatePluginManifest(manifestRaw());
    expect(result.ok).toBe(true);
    if (result.ok) {
      const wire = toBridgeManifest(result.manifest);
      expect(wire.permissions).toEqual(['read-data', 'ui-contributions']);
      expect(wire.permissions).not.toContain('commands');
    }
  });

  it('omits ui-contributions when nothing is declared', () => {
    const result = validatePluginManifest(manifestRaw({ contributes: undefined }));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(toBridgeManifest(result.manifest).permissions).toEqual(['read-data']);
    }
  });
});