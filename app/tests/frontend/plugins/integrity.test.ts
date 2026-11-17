/**
 * Integrity tests: canonical JSON determinism, SHA-256 known vectors,
 * tamper detection (manifest or entry change → changed → re-review).
 */

import { describe, expect, it } from 'vitest';
import {
  canonicalJson,
  compareIntegrity,
  computeIntegrityHashes,
  sha256Hex,
  UNKNOWN_INTEGRITY
} from '../../../src/frontend/src/plugins/integrity';

describe('canonical JSON', () => {
  it('sorts object keys recursively', () => {
    const a = canonicalJson({ b: 1, a: { d: 2, c: [3, { f: 4, e: 5 }] } });
    const b = canonicalJson({ a: { c: [3, { e: 5, f: 4 }], d: 2 }, b: 1 });
    expect(a).toBe(b);
    expect(a).toBe('{"a":{"c":[3,{"e":5,"f":4}],"d":2},"b":1}');
  });

  it('preserves array order and handles primitives', () => {
    expect(canonicalJson([2, 1])).toBe('[2,1]');
    expect(canonicalJson(null)).toBe('null');
    expect(canonicalJson('x')).toBe('"x"');
    expect(canonicalJson(true)).toBe('true');
  });
});

describe('SHA-256', () => {
  it('matches the known vector for "abc"', async () => {
    expect(await sha256Hex('abc')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });

  it('matches the known vector for the empty string', async () => {
    expect(await sha256Hex('')).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
  });

  it('is deterministic across calls', async () => {
    expect(await sha256Hex('plugin entry code')).toBe(await sha256Hex('plugin entry code'));
  });
});

describe('tamper detection', () => {
  it('reports verified for identical artifacts', async () => {
    const manifest = { id: 'com.example.widget', entry: 'index.js' };
    const first = await computeIntegrityHashes(manifest, 'console.log(1);');
    const second = await computeIntegrityHashes({ entry: 'index.js', id: 'com.example.widget' }, 'console.log(1);');
    expect(first).not.toBeNull();
    expect(compareIntegrity(first, second)).toBe('verified');
  });

  it('reports changed when the manifest changes', async () => {
    const before = await computeIntegrityHashes({ id: 'com.example.widget', version: '1.0.0' }, 'code');
    const after = await computeIntegrityHashes({ id: 'com.example.widget', version: '1.0.1' }, 'code');
    expect(compareIntegrity(before, after)).toBe('changed');
  });

  it('reports changed when the entry module changes', async () => {
    const before = await computeIntegrityHashes({ id: 'com.example.widget' }, 'code');
    const after = await computeIntegrityHashes({ id: 'com.example.widget' }, 'code // tampered');
    expect(compareIntegrity(before, after)).toBe('changed');
  });

  it('reports unknown when hashes are missing on either side', async () => {
    const hashes = await computeIntegrityHashes({ id: 'com.example.widget' }, null);
    expect(compareIntegrity(null, hashes)).toBe('unknown');
    expect(compareIntegrity(hashes, null)).toBe('unknown');
    expect(compareIntegrity(UNKNOWN_INTEGRITY, hashes)).toBe('unknown');
  });

  it('handles a missing entry module (null hash) consistently', async () => {
    const hashes = await computeIntegrityHashes({ id: 'com.example.widget' }, null);
    expect(hashes?.entryHash).toBeNull();
    const again = await computeIntegrityHashes({ id: 'com.example.widget' }, null);
    expect(compareIntegrity(hashes, again)).toBe('verified');
  });
});